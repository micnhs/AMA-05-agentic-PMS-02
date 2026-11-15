using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Claim = System.Security.Claims.Claim;

namespace Cohors.Api;

public sealed record LocalUser(string Id, string Username, string Name, string Role, string[] Products, string Salt, string Hash);
public sealed record LoginRequest(string Username, string Password);

public static class Authentication
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Authentication:Mode"] ?? "Entra";
        if (mode == "LocalDemo")
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("LocalDemo authentication is forbidden outside Development/Testing.");
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = "LocalSelector";
                options.DefaultChallengeScheme = "LocalSelector";
            })
            .AddPolicyScheme("LocalSelector", null, options =>
                options.ForwardDefaultSelector = context => context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                    ? "LocalWorkload" : "CohorsCookie")
            .AddCookie("CohorsCookie", options =>
            {
                options.Cookie.Name = "Cohors.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(45);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = context =>
                {
                    var users = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
                        .GetSection("Authentication:Users").Get<LocalUser[]>() ?? [];
                    var id = context.Principal?.FindFirstValue("oid");
                    var user = users.SingleOrDefault(u => u.Id == id);
                    if (user is null || !context.Principal!.IsInRole(user.Role)) context.RejectPrincipal();
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, LocalWorkloadHandler>("LocalWorkload", _ => { });
        }
        else if (mode == "Entra")
        {
            var tenant = builder.Configuration["Entra:TenantId"] ?? throw new InvalidOperationException("Entra TenantId required.");
            var audience = builder.Configuration["Entra:Audience"] ?? throw new InvalidOperationException("Entra Audience required.");
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
                options.Audience = audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true,
                    ValidateAudience = true, ValidateLifetime = true, NameClaimType = "name", RoleClaimType = "roles",
                    ValidIssuer = $"https://login.microsoftonline.com/{tenant}/v2.0", ClockSkew = TimeSpan.FromMinutes(1) };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        var p = ctx.Principal!;
                        var delegated = p.FindFirstValue("scp")?.Split(' ').Contains("access_as_user") == true;
                        var workload = p.IsInRole(Roles.Workflow) && p.FindFirstValue("idtyp") == "app";
                        if (p.FindFirstValue("oid") is null || p.FindFirstValue("tid") != tenant || (!delegated && !workload))
                            ctx.Fail("Required tenant, object identity or API scope missing.");
                        return Task.CompletedTask;
                    }
                };
            });
        }
        else throw new InvalidOperationException("Unknown authentication mode.");
        builder.Services.AddAuthorization(options => options.AddPolicy("WorkloadOnly",
            policy => policy.RequireRole(Roles.Workflow).RequireAssertion(context => IsWorkload(context.User))));
        builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-Token"; options.Cookie.SameSite = SameSiteMode.Strict; });
    }

    public static Actor Actor(HttpContext context, IConfiguration configuration)
    {
        var p = context.User;
        Guard.Against(p.Identity?.IsAuthenticated != true, "unauthenticated", "Anmeldung erforderlich.", 401);
        var id = p.FindFirstValue("oid") ?? throw new DomainException("identity", "Unvollstaendige Identitaet.", 401);
        var roles = p.FindAll(ClaimTypes.Role).Concat(p.FindAll("roles")).Select(c => c.Value).Distinct().ToArray();
        var local = configuration["Authentication:Mode"] == "LocalDemo";
        var localUser = local && p.FindFirstValue("actor_kind") == "human"
            ? configuration.GetSection("Authentication:Users").Get<LocalUser[]>()?.SingleOrDefault(u => u.Id == id) : null;
        var products = local ? localUser?.Products ?? (IsWorkload(p) ? CaseService.Workflow.Products : [])
            : configuration.GetSection($"Entra:ProductGrants:{id}").Get<string[]>() ?? [];
        return new(id, p.Identity?.Name ?? p.FindFirstValue("name") ?? id, roles, products,
            local ? p.FindFirstValue("actor_kind") == "human" : p.FindFirstValue("scp")?.Split(' ').Contains("access_as_user") == true);
    }

    public static bool IsWorkload(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.IsInRole(Roles.Workflow)
        && principal.FindFirstValue("scp") is null
        && (principal.FindFirstValue("actor_kind") == "workload" || principal.FindFirstValue("idtyp") == "app");

    public static ClaimsPrincipal Principal(LocalUser user) => new(new ClaimsIdentity(
        new[] { new Claim("oid", user.Id), new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role), new Claim("actor_kind", "human") }
        .Concat(user.Products.Select(product => new Claim("product", product))), "CohorsCookie"));

    public static bool VerifyPassword(LocalUser? user, string password)
    {
        if (password.Length > 500) return false;
        var salt = user is null ? new byte[16] : Convert.FromBase64String(user.Salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
        return user is not null && CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(user.Hash));
    }
}

public sealed class LocalWorkloadHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Authentication:WorkloadKey"];
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(expected) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        var supplied = header[7..];
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid local workload token."));
        var actor = CaseService.Workflow;
        var claims = new[] { new Claim("oid", actor.Id), new Claim(ClaimTypes.Name, actor.Name),
            new Claim(ClaimTypes.Role, Roles.Workflow), new Claim("actor_kind", "workload") }
            .Concat(actor.Products.Select(product => new Claim("product", product)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
