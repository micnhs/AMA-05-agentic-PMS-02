using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Cohors.Agents;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var home = Environment.GetEnvironmentVariable("COHORS_HOME") ?? builder.Environment.ContentRootPath;
builder.Configuration.AddJsonFile(Path.Combine(home, ".local", "settings.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
Authentication.Configure(builder);
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = builder.Environment.IsEnvironment("Testing") ? 1000 : 30,
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddSingleton(TimeProvider.System);
var policyDirectory = Directory.Exists(Path.Combine(home, "policies")) ? Path.Combine(home, "policies") : Path.Combine(AppContext.BaseDirectory, "policies");
builder.Services.AddSingleton(DemoBaseline.LoadPolicies(policyDirectory));
builder.Services.AddSingleton(sp => SqlStateStore.Configure(sp.GetRequiredService<IConfiguration>().GetConnectionString("Cohors")
    ?? throw new InvalidOperationException("Cohors SQL connection is required. Run scripts\\Initialize-Local.ps1."),
    builder.Configuration["Cohors:SqlAuthentication"], builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")));
builder.Services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<SqlStateStore>());
builder.Services.AddSingleton<IRetentionStateStore>(sp => sp.GetRequiredService<SqlStateStore>());
builder.Services.AddSingleton<IWorkspaceReadStore>(sp => sp.GetRequiredService<SqlStateStore>());
var retentionMode = builder.Configuration["Retention:PhysicalMode"] ?? "Disabled";
if (retentionMode is not ("Disabled" or "SyntheticLocal") ||
    retentionMode == "SyntheticLocal" && ((!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")) ||
        builder.Configuration["Cohors:ObjectStore"] != "Local" || builder.Configuration["Cohors:Search"] == "Azure"))
    throw new InvalidOperationException("SyntheticLocal retention requires explicit Development/Testing, local objects and no Azure Search.");
builder.Services.AddSingleton(new RetentionOptions(retentionMode == "SyntheticLocal"));
builder.Services.AddSingleton<IObjectStore>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    if (config["Cohors:ObjectStore"] == "Local")
    {
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Local object storage is development-only.");
        return new LocalObjectStore(Path.Combine(home, ".local", "blobs"));
    }
    return new AzureObjectStore(new BlobContainerClient(new Uri(config["Blob:ContainerUri"]
        ?? throw new InvalidOperationException("Blob container URI required.")), new DefaultAzureCredential()));
});
builder.Services.AddSingleton<IKnowledgeSearch>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var search = config["Cohors:Search"] == "Azure"
        ? new SearchClient(new Uri(config["Search:Endpoint"]!), config["Search:Index"], new DefaultAzureCredential()) : null;
    return new KnowledgeSearch(sp.GetRequiredService<IStateStore>(), sp.GetRequiredService<IObjectStore>(),
        sp.GetRequiredService<SqlStateStore>(), search);
});
builder.Services.AddHttpClient("agent-runtime", client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IAgentRunner>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var localAllowed = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
    var transport = config["Cohors:AgentTransport"] ?? (localAllowed ? "Local" : "Remote");
    if (transport == "Local")
    {
        if (!localAllowed) throw new InvalidOperationException("In-process agents are Development/Testing-only.");
        return new AgentRunner(new(config["Foundry:Endpoint"], config["Foundry:Deployment"], config["Foundry:ModelVersion"],
            config["Foundry:ProcessingBoundary"], config.GetSection("Foundry:BoundaryReview").Get<FoundryBoundaryApproval>()));
    }
    if (transport != "Remote") throw new InvalidOperationException("Unknown agent transport.");
    var endpoint = new Uri(config["Agents:Endpoint"] ?? throw new InvalidOperationException("Internal agent endpoint required."));
    var localKey = config["Agents:LocalWorkloadKey"];
    if (localKey is not null && (!localAllowed || !endpoint.IsLoopback))
        throw new InvalidOperationException("Local agent key is restricted to loopback Development/Testing.");
    if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
        endpoint.AbsolutePath != "/" || (endpoint.Scheme != "https" && !(localKey is not null && endpoint.IsLoopback)))
        throw new InvalidOperationException("Agent runtime must be a fixed HTTPS origin.");
    var scope = config["Agents:Scope"] ?? (localKey is null ? throw new InvalidOperationException("Agent application scope required.") : "");
    return new RemoteAgentRunner(sp.GetRequiredService<IHttpClientFactory>().CreateClient("agent-runtime"),
        new(endpoint, scope, localKey), new DefaultAzureCredential());
});
builder.Services.AddSingleton<CaseService>();
builder.Services.AddSingleton<WorkspaceReadService>();
builder.Services.AddSingleton<OperationsService>();
builder.Services.AddSingleton<OutboxProcessor>();
builder.Services.AddSingleton<PublicSourceService>();
builder.Services.AddSingleton<SignalReviewService>();
builder.Services.AddSingleton<EvidenceService>();
builder.Services.AddSingleton<QualityService>();
builder.Services.AddSingleton<AnalyticsService>();
builder.Services.AddSingleton<PsurReviewService>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<PsurExportService>();
builder.Services.AddSingleton<ProductAuditService>();
builder.Services.AddSingleton<IMirComposer, MirComposer>();
builder.Services.AddSingleton<RegulatoryService>();
builder.Services.AddHttpClient<IPublicSourceReader, PublicSources>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
if (builder.Configuration.GetValue<bool>("Cohors:LocalOperations"))
{
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException("Local operations emulator must not replace isolated Azure operations in production.");
    builder.Services.AddHostedService<LocalWatchdog>();
    builder.Services.AddHostedService<LocalOutboxWorker>();
}

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var domain = exception as DomainException;
    var status = domain?.Status ?? (exception is AntiforgeryValidationException ? 400 : 500);
    app.Logger.LogWarning("Request failed: {Code}, trace {TraceId}.", domain?.Code ?? exception?.GetType().Name, context.TraceIdentifier);
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status,
        Title = domain?.Message ?? "Die Operation ist fehlgeschlagen; kein Erfolg wurde bestaetigt.",
        Extensions = { ["code"] = domain?.Code ?? "request_failed", ["traceId"] = context.TraceIdentifier } });
}));
app.Use(async (context, next) =>
{
    if (app.Configuration["Authentication:Mode"] == "LocalDemo" &&
        (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip)
         || context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]")))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { code = "local_only", message = "LocalDemo ist auf Loopback begrenzt." });
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self' https://login.microsoftonline.com; frame-src https://login.microsoftonline.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers["Cache-Control"] = context.Request.Path.StartsWithSegments("/api") ? "no-store" : "no-cache";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Method is "POST" or "PUT" or "DELETE" &&
        context.Request.Path.StartsWithSegments("/api") && !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

Actor Who(HttpContext context) => Authentication.Actor(context, app.Configuration);
string Key(HttpContext context) => Guard.Text(context.Request.Headers["Idempotency-Key"].ToString(), "Idempotency-Key", 8, 120);
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", pilot = true }));
app.MapGet("/health/ready", async (SqlStateStore store, CancellationToken ct) =>
{
    await store.RequireReady(ct);
    return Results.Ok(new { status = "ready", persistence = "SQL Server", transmissions = "simulated-only" });
});
app.MapGet("/api/auth/session", (HttpContext context, IAntiforgery antiforgery) =>
{
    var token = antiforgery.GetAndStoreTokens(context).RequestToken;
    return Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true,
        actor = context.User.Identity?.IsAuthenticated == true ? Who(context) : null, csrfToken = token,
        mode = app.Configuration["Authentication:Mode"], entra = new { tenantId = app.Configuration["Entra:TenantId"],
            clientId = app.Configuration["Entra:SpaClientId"], scope = app.Configuration["Entra:Scope"] } });
});
app.MapPost("/api/auth/login", async (LoginRequest input, HttpContext context) =>
{
    Guard.Against(app.Configuration["Authentication:Mode"] != "LocalDemo", "entra_required", "Entra-Anmeldung erforderlich.", 403);
    var user = app.Configuration.GetSection("Authentication:Users").Get<LocalUser[]>()?.SingleOrDefault(u => u.Username == input.Username);
    Guard.Against(!Authentication.VerifyPassword(user, input.Password), "invalid_login", "Benutzername oder Passwort ungueltig.", 401);
    await context.SignInAsync("CohorsCookie", Authentication.Principal(user!));
    return Results.Ok(new { signedIn = true });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext context) => { await context.SignOutAsync("CohorsCookie"); return Results.NoContent(); }).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/cockpit", (string? productId, DateOnly? from, DateOnly? through, string? timeZone, string? focus,
    int? offset, WorkspaceReadService service, HttpContext ctx, CancellationToken ct) =>
    service.Cockpit(Who(ctx), productId, from, through, timeZone, focus, offset ?? 0, ct));
api.MapGet("/deadline-calendar", (string? productId, DateOnly? month, string? timeZone, string? kind, string? status,
    DateOnly? day, int? offset, WorkspaceReadService service, HttpContext ctx, CancellationToken ct) =>
    service.Calendar(Who(ctx), productId, month, timeZone, kind, status, day, offset ?? 0, ct));
api.MapGet("/products", (PolicyCatalog policies, HttpContext ctx) => policies.Devices.Where(d => Who(ctx).Products.Contains(d.Id)));
api.MapGet("/products/{id}/audit", async (string id, ProductAuditService exporter, HttpContext ctx, CancellationToken ct) =>
{
    var result = await exporter.Export(id, Who(ctx), ct);
    return Results.File(result.Bytes, result.ContentType, result.FileName);
});
api.MapGet("/status", async (IStateStore store, HttpContext ctx, CancellationToken ct) =>
{
    _ = Who(ctx);
    var heartbeat = await store.Get<OperationsHeartbeat>("operations", "heartbeat", ct);
    return Results.Ok(new { mode = "Synthetic pilot", agentDefault = "Replay", foundryConfigured = !string.IsNullOrEmpty(app.Configuration["Foundry:Endpoint"]),
        persistence = "SQL Server", objects = app.Configuration["Cohors:ObjectStore"], search = app.Configuration["Cohors:Search"],
        orchestration = app.Configuration.GetValue<bool>("Cohors:LocalOperations") ? "Local operations emulator (SQL outbox)" : "Durable Functions",
        agentRuntime = app.Configuration["Cohors:AgentTransport"] ??
            (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing") ? "Local" : "Remote"),
        heartbeat, heartbeatMissing = OperationsService.HeartbeatMissing(heartbeat, DateTimeOffset.UtcNow), submissions = "Simulator only",
        warnings = new[] { "Keine Fach-/Rechtsabnahme", "118 Originalanforderungen zugeordnet; Pilot-/Zielbetriebsnachweise weiter offen", "Azure nicht live validiert", "Kalenderprofil ist synthetisch" } });
});
api.MapGet("/cases", async (IStateStore store, HttpContext ctx, CancellationToken ct) =>
    (await store.List<SafetyCase>("case", Who(ctx).Products, ct)).Select(c => new { c.Id, c.Version, c.Reference, c.ProductId, c.Title,
        c.Stage, c.Disposition, c.Jurisdiction, c.Seriousness, c.FirstAwarenessAt, c.SystemReceivedAt, c.Udi, c.Batch,
        c.Deadlines, c.FollowUps, c.Alerts, sourceCount = c.Sources.Length, assessmentVersion = c.CurrentAssessment?.Version,
        reportState = c.CurrentReport?.ReviewReturn is not null ? "ReturnedForCorrection" : c.CurrentReport?.State.ToString(), outcome = CaseOutcomes.Read(c), c.Synthetic }));
api.MapPost("/cases", (RegisterComplaint input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Register(input, Who(ctx), Key(ctx), ct));
api.MapGet("/cases/{id}", (string id, CaseService service, HttpContext ctx, CancellationToken ct) => service.Get(id, Who(ctx), ct));
api.MapGet("/cases/{id}/process", (string id, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.Process(id, Who(ctx), ct));
api.MapPost("/cases/{id}/assessment", (string id, AssessmentCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.RequestAssessment(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/assessment/revise", (string id, ReviseAssessmentCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Revise(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/manual", (string id, ManualAssessmentCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Manual(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/report", (string id, PrepareReportCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Prepare(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/report/return", (string id, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.ReturnReport(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/approve/medical", (string id, ApprovalCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Approve(id, input, Who(ctx), Key(ctx), true, ct));
api.MapPost("/cases/{id}/approve/regulatory", (string id, ApprovalCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Approve(id, input, Who(ctx), Key(ctx), false, ct));
api.MapPost("/cases/{id}/submit", (string id, SubmissionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Submit(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/reconcile", (string id, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Reconcile(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/disposition", (string id, DispositionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Decide(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/disposition/approve", (string id, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.ApproveDisposition(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/awareness", (string id, AwarenessCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Awareness(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/evidence", (string id, LinkEvidenceCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.Link(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information", (string id, RequestInformationCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.AddInformationRequest(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/{requestId}/approve", (string id, string requestId, ApprovalCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.ApproveInformation(id, requestId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/{requestId}/dispatch", (string id, string requestId, DispatchInformationCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.DispatchInformation(id, requestId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/{requestId}/dispatch/{dispatchId}/reconcile", (string id, string requestId, string dispatchId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.ReconcileInformation(id, requestId, dispatchId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/respond", (string id, RespondInformationCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.RespondInformation(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/responses/{responseId}/map", (string id, string responseId, MapInformationResponseCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.MapInformationResponse(id, responseId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/information/responses/{responseId}/review", (string id, string responseId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) => service.ReviewInformationResponse(id, responseId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/investigations", (string id, RecordSpecimenPlanCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.RecordSpecimenPlan(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/investigations/{investigationId}/approve", (string id, string investigationId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.ApproveSpecimen(id, investigationId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/investigations/{investigationId}/inform", (string id, string investigationId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.InformSpecimenAuthority(id, investigationId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/investigations/{investigationId}/simulate", (string id, string investigationId, SimulateSpecimenCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.SimulateSpecimen(id, investigationId, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/investigations/{investigationId}/cancel", (string id, string investigationId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.CancelSpecimen(id, investigationId, input, Who(ctx), Key(ctx), ct));
api.MapGet("/cases/{id}/investigations/{investigationId}/export", async (string id, string investigationId, int? caseVersion,
    CaseService service, HttpContext ctx, CancellationToken ct) =>
{
    var export = await service.ExportSpecimen(id, investigationId, Who(ctx), caseVersion, ct);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(Canonical.Serialize(export)), "application/json",
        $"COHORS-DEMO-SPECIMEN-{investigationId}-case-v{export.SnapshotSource.Version}.json");
});
api.MapGet("/cases/{id}/audit", async (string id, CaseService service, IStateStore store, HttpContext ctx, CancellationToken ct) =>
{
    await service.Get(id, Who(ctx), ct);
    return await store.Audit(id, ct);
});
api.MapGet("/cases/{id}/manifest", async (string id, CaseService service, SqlStateStore store, HttpContext ctx, CancellationToken ct) =>
{
    await service.Get(id, Who(ctx), ct);
    return Results.Ok(await store.Manifest(id, ct));
});
api.MapGet("/cases/{id}/evidence/{evidenceId}", (string id, string evidenceId, int? caseVersion,
    CaseService service, HttpContext ctx, CancellationToken ct) => service.ReadEvidence(id, evidenceId, caseVersion, Who(ctx), ct));
api.MapGet("/evidence", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<Evidence>("evidence", Who(ctx).Products, ct));
api.MapGet("/evidence/{id}/versions", async (string id, IStateStore store, HttpContext ctx, CancellationToken ct) =>
    (await store.History<Evidence>("evidence", Who(ctx).Products, ct)).Where(e => e.Id == id));
api.MapGet("/evidence/{id}/versions/{version:int}/content", (string id, int version, EvidenceService service,
    HttpContext ctx, CancellationToken ct) => service.Read(id, version, Who(ctx), ct));
api.MapPost("/evidence/{id}/revise", (string id, ReviseEvidenceCommand input, EvidenceService service, HttpContext ctx, CancellationToken ct) =>
    service.Revise(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/exposures", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<Exposure>("exposure", Who(ctx).Products, ct));
api.MapPost("/cases/{id}/mir", (string id, MirCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.AttachMir(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/cases/{id}/mir", (string id, RegulatoryService service, HttpContext ctx, CancellationToken ct) => service.Export(id, Who(ctx), ct));
api.MapGet("/summary-agreements", (RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.ListSummaryAgreements(Who(ctx), ct));
api.MapPost("/summary-agreements", (RecordSummaryAgreementCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.RecordSummaryAgreement(input, Who(ctx), Key(ctx), ct));
api.MapPost("/summary-agreements/{id}/withdraw", (string id, VersionCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.WithdrawSummaryAgreement(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/summary-reports", (RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.ListSummaries(Who(ctx), ct));
api.MapGet("/summary-reports/{id}", (string id, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.GetSummary(id, Who(ctx), ct));
api.MapPost("/summary-reports", (DraftSummaryCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.DraftSummary(input, Who(ctx), Key(ctx), ct));
api.MapPost("/summary-reports/{id}/approve", (string id, VersionCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.ApproveSummary(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/summary-reports/{id}/simulate", (string id, VersionCommand input, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
    service.SimulateSummary(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/summary-reports/{id}/export", async (string id, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
{
    var report = await service.ExportSummary(id, Who(ctx), ct);
    var json = System.Text.Json.JsonSerializer.Serialize(new {
        contract = "cohors-periodic-summary-export-1", boundary = SummaryReportingRules.Boundary, report
    }, new System.Text.Json.JsonSerializerOptions(Canonical.Json) { WriteIndented = true });
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{report.Reference}.json");
});
api.MapGet("/products/profiles", (IStateStore store, HttpContext ctx, CancellationToken ct) =>
    store.List<ProductGovernance>("productGovernance", Who(ctx).Products, ct));
api.MapPost("/cases/{id}/reclassify", (string id, ReclassifyCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.Reclassify(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/terminology", (PolicyCatalog policies) => new { policies.Terminology.Reference, referenceHash = policies.Terminology.Hash });
api.MapPost("/cases/{id}/coding", (string id, RecordCodingCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.RecordCoding(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/close", (string id, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.Close(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/reopen", (string id, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.Reopen(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/cases/{id}/runs/{runId}/abandon", (string id, string runId, VersionCommand input, CaseService service, HttpContext ctx, CancellationToken ct) =>
    service.AbandonRun(id, runId, input, Who(ctx), Key(ctx), ct));
api.MapGet("/operations/agent-invocations", async (OperationsService service, HttpContext ctx, CancellationToken ct) =>
{
    var actor = Who(ctx);
    actor.RequireHuman(Roles.Quality);
    return (await service.ListInvocations(actor, ct))
        .Select(i => new { i.Id, i.Version, i.ProductId, i.Status, i.StartedAt, i.FinishedAt, i.ErrorCode });
});
api.MapPost("/operations/agent-invocations/{id}/abandon", async (string id, VersionCommand input, OperationsService service, HttpContext ctx, CancellationToken ct) =>
{
    var item = await service.AbandonInvocation(id, input, Who(ctx), Key(ctx), ct);
    return new { item.Id, item.Version, item.ProductId, item.Status, item.ErrorCode };
});
api.MapGet("/cases/{id}/mir/xml", async (string id, RegulatoryService service, HttpContext ctx, CancellationToken ct) =>
{
    var artifact = await service.Export(id, Who(ctx), ct);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(artifact.Xml), "application/xml", $"COHORS-DEMO-{id}-{artifact.Kind}.xml");
});
api.MapGet("/sources", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<PublicSourceRun>("source", Who(ctx).Products, ct));
api.MapPost("/sources/{source}/sync", (string source, ReadPublicSourceCommand input, PublicSourceService service, HttpContext ctx, CancellationToken ct) =>
{
    Guard.Against(!app.Configuration.GetValue<bool>("Cohors:PublicSources"), "public_sources_disabled",
        "Oeffentliche Leseadapter sind in dieser Umgebung nicht aktiviert.", 403);
    return service.Read(source, input, Who(ctx), Key(ctx), ct);
});
api.MapPost("/sources/{id}/approve", (string id, ApprovePublicSourceCommand input, PublicSourceService service, HttpContext ctx, CancellationToken ct) =>
    service.Approve(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/signals", (string? status, SignalReviewService service, HttpContext ctx, CancellationToken ct) =>
    service.List(status ?? "Rejected", Who(ctx), ct));
api.MapPost("/signals/{kind}/{id}/reject", (string kind, string id, SignalDecisionCommand input, SignalReviewService service, HttpContext ctx, CancellationToken ct) =>
    service.Decide(kind, id, "Reject", input, Who(ctx), Key(ctx), ct));
api.MapPost("/signals/{kind}/{id}/reopen", (string kind, string id, SignalDecisionCommand input, SignalReviewService service, HttpContext ctx, CancellationToken ct) =>
    service.Decide(kind, id, "Reopen", input, Who(ctx), Key(ctx), ct));
api.MapPost("/signals/{kind}/{id}/confirm-relevance", (string kind, string id, SignalDecisionCommand input, SignalReviewService service, HttpContext ctx, CancellationToken ct) =>
    service.Decide(kind, id, "ConfirmRelevance", input, Who(ctx), Key(ctx), ct));
api.MapGet("/capas", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<Capa>("capa", Who(ctx).Products, ct));
api.MapPost("/capas", (OpenCapaCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.OpenCapa(input, Who(ctx), Key(ctx), ct));
api.MapPost("/capas/{id}/verify", (string id, VerifyCapaCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.Verify(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/capas/{id}/amend", (string id, AmendCapaCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.Amend(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/changes", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<ChangeRequest>("change", Who(ctx).Products, ct));
api.MapPost("/changes", (OpenChangeCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.OpenChange(input, Who(ctx), Key(ctx), ct));
api.MapPost("/changes/{id}/approve", (string id, ChangeDecisionCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.ApproveChange(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/changes/{id}/complete", (string id, ChangeDecisionCommand input, QualityService service, HttpContext ctx, CancellationToken ct) => service.CompleteChange(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/trends", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.List<TrendRun>("trend", Who(ctx).Products, ct));
api.MapPost("/trends", (RunTrendCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.Trend(input, Who(ctx), Key(ctx), ct));
api.MapPost("/trends/{id}/review", (string id, VersionCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.ReviewTrend(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/psur-groups", (AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.ListGroups(Who(ctx), ct));
api.MapPost("/psur-groups", (RecordPsurGroupCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) =>
    service.RecordGroup(input, Who(ctx), Key(ctx), ct));
api.MapGet("/psurs", (AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.ListPsurs(Who(ctx), ct));
api.MapGet("/psurs/{id}", (string id, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.GetPsur(id, Who(ctx), ct));
api.MapGet("/psurs/{id}/export/{format}", async (string id, int version, string format, PsurExportService exporter, HttpContext ctx, CancellationToken ct) =>
{
    var result = await exporter.Export(id, version, format, Who(ctx), ct);
    return Results.File(result.Bytes, result.ContentType, result.FileName);
});
api.MapPost("/psurs", (FreezePsurCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.Freeze(input, Who(ctx), Key(ctx), ct));
api.MapPost("/psurs/{id}/revise", (string id, RevisePsurCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.Revise(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/psurs/{id}/approve", (string id, VersionCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.Approve(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/psurs/{id}/simulate", (string id, VersionCommand input, AnalyticsService service, HttpContext ctx, CancellationToken ct) => service.Simulate(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/psurs/{id}/reviews", (string id, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.List(id, Who(ctx), ct));
api.MapPost("/psur-reviews", (OpenPsurReviewCommand input, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Open(input, Who(ctx), Key(ctx), ct));
api.MapGet("/psur-reviews/{id}", (string id, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Get(id, Who(ctx), ct));
api.MapPost("/psur-reviews/{id}/draft", (string id, DraftPsurReplyCommand input, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Draft(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/psur-reviews/{id}/approve", (string id, VersionCommand input, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Approve(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/psur-reviews/{id}/simulate", (string id, VersionCommand input, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Simulate(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/psur-reviews/{id}/assess", (string id, AssessPsurReplyCommand input, PsurReviewService service, HttpContext ctx, CancellationToken ct) => service.Assess(id, input, Who(ctx), Key(ctx), ct));
api.MapGet("/psur-reviews/{id}/export", async (string id, int version, PsurReviewService service, SqlStateStore store, HttpContext ctx, CancellationToken ct) =>
{
    var review = await service.Get(id, Who(ctx), ct);
    Guard.Against(review.Version != version, "version_conflict", "BS-Vorgang hat sich geaendert; neu laden.", 409);
    var audit = await store.Manifest(id, ct, "psurReview");
    Guard.Against((await service.Get(id, Who(ctx), ct)).Version != version, "version_conflict", "BS-Vorgang wurde waehrend des Exports geaendert.", 409);
    var data = new { format = "cohors-psur-review-export-1", review, audit,
        boundary = "SYNTHETISCH. Technische Quittung, Rueckfragen und Bewertung sind getrennte Simulationen. Keine echte BS-Kommunikation, Konformitaets- oder Risikoakzeptanz." };
    return Results.File(System.Text.Encoding.UTF8.GetBytes(Canonical.Serialize(data)), "application/json", $"COHORS-DEMO-{id}-v{version}.json");
});
api.MapGet("/operations/outbox", (IStateStore store, HttpContext ctx, CancellationToken ct) => store.OutboxIssues(Who(ctx).Products, ct));
api.MapGet("/retention/policy", (RetentionService service, HttpContext ctx) =>
{
    _ = Who(ctx);
    return Results.Ok(new { RetentionRules.PolicyVersion, service.LocalPhysicalProfile,
        dataClasses = new[] { RetentionRules.Raw, RetentionRules.ImplantDocumentation } });
});
api.MapGet("/retention", (RetentionService service, HttpContext ctx, CancellationToken ct) => service.List(Who(ctx), ct));
api.MapGet("/retention/{id}", (string id, RetentionService service, HttpContext ctx, CancellationToken ct) => service.Evaluate(id, Who(ctx), ct));
api.MapGet("/product-lifecycles", (RetentionService service, HttpContext ctx, CancellationToken ct) => service.Lifecycles(Who(ctx), ct));
api.MapPost("/product-lifecycles", (RecordLifecycleCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.RecordLifecycle(input, Who(ctx), Key(ctx), ct));
api.MapPost("/retention/raw", (CreateRawRetentionCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.CreateRaw(input, Who(ctx), Key(ctx), ct));
api.MapPost("/retention/evidence", (TrackRetentionCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.Track(input, Who(ctx), Key(ctx), ct));
api.MapPost("/retention/{id}/hold", (string id, RetentionHoldCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.Hold(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/retention/{id}/authorize-deletion", (string id, AuthorizeDeletionCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.Authorize(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/retention/{id}/delete", (string id, VersionCommand input, RetentionService service, HttpContext ctx, CancellationToken ct) =>
    service.Delete(id, input, Who(ctx), Key(ctx), ct));
api.MapPost("/operations/outbox/{id}/recover", (string id, VersionCommand input, OperationsService service, HttpContext ctx, CancellationToken ct) =>
    service.Recover(id, input, Who(ctx), Key(ctx), ct));

var internalApi = app.MapGroup("/internal").RequireAuthorization("WorkloadOnly");
internalApi.MapPost("/watchdog", (OperationsService operations, CancellationToken ct) => operations.Tick(ct));
internalApi.MapGet("/ready", async (SqlStateStore store, CancellationToken ct) => { await store.RequireReady(ct); return Results.Ok(new { ready = true }); });
internalApi.MapGet("/outbox", async (IStateStore store, CancellationToken ct) =>
    (await store.PendingEvents(50, ct)).Select(e => new OutboxDescriptor(e.Id, e.Type, e.Attempts, e.RecoveryVersion)));
internalApi.MapPost("/outbox/{id}/process", (string id, OutboxProcessor processor, CancellationToken ct) => processor.Process(id, ct));
internalApi.MapPost("/search/rebuild", (IKnowledgeSearch search, CancellationToken ct) => search.Rebuild(ct));

app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
if (app.Configuration.GetValue<bool>("Cohors:AutoMigrate")) await app.Services.GetRequiredService<SqlStateStore>().Initialize();
if (app.Configuration.GetValue<bool>("Cohors:Seed"))
{
    var demoDirectory = Directory.Exists(Path.Combine(home, "demo")) ? Path.Combine(home, "demo") : Path.Combine(AppContext.BaseDirectory, "demo");
    await DemoBaseline.Seed(demoDirectory, app.Services.GetRequiredService<IStateStore>(),
        app.Services.GetRequiredService<IObjectStore>(), app.Services.GetRequiredService<CaseService>(), TimeProvider.System);
}
app.Run();

public partial class Program;
