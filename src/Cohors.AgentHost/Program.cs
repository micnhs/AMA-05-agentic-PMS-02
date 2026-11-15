using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;

var boundaryHashOnly = args.Contains("--boundary-hash");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--boundary-hash").ToArray());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var config = builder.Configuration;
if (boundaryHashOnly)
{
    var review = config.GetSection("Foundry:BoundaryReview").Get<FoundryBoundaryApproval>()
        ?? throw new InvalidOperationException("Foundry:BoundaryReview required; set ConfigurationHash to 'pending' when computing.");
    var settings = new FoundryOptions(config["Foundry:Endpoint"], config["Foundry:Deployment"],
        config["Foundry:ModelVersion"], config["Foundry:ProcessingBoundary"], review);
    var hash = FoundryBoundary.ConfigurationHash(settings, review);
    FoundryBoundary.RequireApproved(settings with { BoundaryApproval = review with { ConfigurationHash = hash } });
    Console.WriteLine(hash);
    return;
}
var localKey = config["Cohors:LocalWorkloadKey"];
var local = localKey is not null;
if (local && (!builder.Environment.IsDevelopment() || localKey!.Length < 32))
    throw new InvalidOperationException("Local agent authentication is Development-only and requires a generated secret.");
if (!local)
{
    var tenant = config["Entra:TenantId"] ?? throw new InvalidOperationException("Agent tenant required.");
    var audience = config["Entra:Audience"] ?? throw new InvalidOperationException("Agent audience required.");
    var caller = config["Agents:AllowedCallerClientId"] ?? throw new InvalidOperationException("API caller identity required.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        options.Audience = audience;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, RoleClaimType = "roles",
            ValidIssuer = $"https://login.microsoftonline.com/{tenant}/v2.0", ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var user = context.Principal!;
                if (user.FindFirstValue("tid") != tenant || user.FindFirstValue("oid") is null ||
                    user.FindFirstValue("idtyp") != "app" || user.FindFirstValue("scp") is not null ||
                    (user.FindFirstValue("azp") ?? user.FindFirstValue("appid")) != caller || !user.IsInRole(Roles.Agent))
                    context.Fail("Dedicated API workload identity and Agent role required.");
                return Task.CompletedTask;
            }
        };
    });
    builder.Services.AddAuthorization();
}
builder.Services.AddSingleton<IAgentRunner>(new AgentRunner(new(config["Foundry:Endpoint"], config["Foundry:Deployment"],
    config["Foundry:ModelVersion"], config["Foundry:ProcessingBoundary"],
    config.GetSection("Foundry:BoundaryReview").Get<FoundryBoundaryApproval>())));
var products = config.GetSection("Agents:Products").Get<string[]>() ?? (local ? ["ortho", "cardio"] :
    throw new InvalidOperationException("Explicit agent product grants required."));
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var domain = error as DomainException;
    app.Logger.LogWarning("Agent request failed: {Type}.", domain?.Code ?? error?.GetType().Name);
    context.Response.StatusCode = domain?.Status ?? 503;
    await context.Response.WriteAsJsonAsync(new { code = domain?.Code ?? "agent_runtime_failed", message = "Kein bestaetigtes Agentenergebnis; kein Replay-Fallback." });
}));
if (local)
{
    app.Use(async (context, next) =>
    {
        if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip) ||
            context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (context.Request.Path.StartsWithSegments("/internal"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), Encoding.UTF8.GetBytes(localKey!)))
            {
                context.Response.StatusCode = 401;
                return;
            }
        }
        await next();
    });
}
else
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", role = "cohors-agents" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", roles = 6, modelMode = "explicit-per-request",
    liveConfigurationRequiresVerifiedDeployment = true }));
var run = app.MapPost("/internal/run", async (AgentEnvelope envelope, IAgentRunner runner, CancellationToken ct) =>
{
    var product = AgentBoundary.Validate(envelope.Input);
    Guard.Against(envelope.ProductId != product || !products.Contains(product), "agent_product", "Produktscope nicht freigegeben.", 403);
    Guard.Against(envelope.InputHash != Canonical.Hash(envelope.Input), "agent_input_hash", "Input-Snapshot wurde veraendert.", 409);
    var output = await runner.Run(envelope.Input, ct);
    AgentBoundary.ValidateResult(envelope.Input, output);
    return new AgentResultEnvelope(envelope.InputHash, output);
});
if (!local) run.RequireAuthorization();
app.Run();
