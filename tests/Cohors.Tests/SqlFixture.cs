using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[CollectionDefinition("SQL pilot", DisableParallelization = true)]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>;

public sealed class SqlFixture : IAsyncLifetime
{
    private readonly string database = "Cohors_Test_" + Guid.NewGuid().ToString("N");
    private static readonly string runId = Guid.NewGuid().ToString("N");
    private string master = "";
    private string objectDirectory = "";
    private string sourceCheckout = "";
    private string resourceJournal = "";
    private bool databaseCreated;
    public string ObjectDirectory => objectDirectory;
    public string ConnectionString { get; private set; } = "";
    public SqlStateStore Store { get; private set; } = null!;
    public IObjectStore Objects { get; private set; } = null!;
    public PolicyCatalog Policies { get; private set; } = null!;
    public bool SeedCases { get; init; } = true;
    public static readonly Actor Pms = new("test-pms", "PMS test", [Roles.Pms], ["ortho", "cardio"], true);
    public static readonly Actor Medical = new("test-medical", "Medical test", [Roles.Medical], ["ortho", "cardio"], true);
    public static readonly Actor Ra = new("test-ra", "RA test", [Roles.Regulatory], ["ortho", "cardio"], true);
    public static readonly Actor Quality = new("test-quality", "Quality test", [Roles.Quality], ["ortho", "cardio"], true);

    public async Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cohors.slnx"))) root = root.Parent;
        var home = root?.FullName ?? throw new InvalidOperationException("Run from the Cohors worktree.");
        var configured = Environment.GetEnvironmentVariable("COHORS_TEST_CONNECTION");
        if (string.IsNullOrEmpty(configured))
        {
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".local", "settings.json")));
            configured = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Cohors").GetString();
        }
        var builder = new SqlConnectionStringBuilder(configured);
        if (!System.Text.RegularExpressions.Regex.IsMatch(builder.DataSource,
            @"^(?:tcp:)?(?:127\.0\.0\.1|localhost)(?:,[1-9]\d{0,4})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("SQL integration tests are restricted to the isolated loopback SQL instance.");
        builder.InitialCatalog = "master";
        master = builder.ConnectionString;
        sourceCheckout = home;
        objectDirectory = Path.Combine(home, ".local", database, "blobs");
        resourceJournal = Environment.GetEnvironmentVariable("COHORS_TEST_RESOURCE_FILE") is { Length: > 0 } resourceFile
            ? resourceFile + ".journal.jsonl"
            : Path.Combine(home, ".local", $"sql-fixtures-{Environment.ProcessId}-{runId}.jsonl");
        await RecordResource("CreateRequested");
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync();
            using var command = new SqlCommand($"CREATE DATABASE [{database}]", connection);
            await command.ExecuteNonQueryAsync();
            databaseCreated = true;
        }
        await RecordResource("DatabaseCreated");
        builder.InitialCatalog = database;
        ConnectionString = builder.ConnectionString;
        Store = new(ConnectionString);
        await Store.Initialize();
        await RecordResource("SchemaReady");
        Objects = new LocalObjectStore(objectDirectory);
        Policies = DemoBaseline.LoadPolicies(Path.Combine(home, "policies"));
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
        await DemoBaseline.Seed(Path.Combine(home, "demo"), Store, Objects, Service(clock), clock, includeCases: SeedCases);
        await RecordResource("Ready");
    }

    public CaseService Service(TimeProvider clock, IKnowledgeSearch? knowledge = null) =>
        new(Store, Objects, Policies, new AgentRunner(),
            knowledge ?? new KnowledgeSearch(Store, Objects, Store), clock, new MirComposer());

    public async Task DisposeAsync()
    {
        if (!databaseCreated) return;
        await SqlFixtureResources.Cleanup(RecordResource, async () =>
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            using var command = new SqlCommand($"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", connection);
            await command.ExecuteNonQueryAsync();
            if (Directory.Exists(objectDirectory)) Directory.Delete(objectDirectory, recursive: true);
            var directory = Path.GetDirectoryName(objectDirectory);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            databaseCreated = false;
        });
    }

    private Task RecordResource(string stage) => SqlFixtureResources.Append(resourceJournal,
        new(database, objectDirectory, Environment.ProcessId, sourceCheckout, stage, DateTimeOffset.UtcNow));
}

internal sealed record SqlFixtureResource(string Database, string ObjectDirectory, int TestHostPid,
    string SourceCheckout, string Stage, DateTimeOffset RecordedAt);

internal static class SqlFixtureResources
{
    private static readonly SemaphoreSlim writer = new(1, 1);

    internal static async Task Append(string path, SqlFixtureResource entry)
    {
        await writer.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Canonical.Serialize(entry) + "\n"));
            stream.Flush(flushToDisk: true);
        }
        finally { writer.Release(); }
    }

    internal static async Task Cleanup(Func<string, Task> record, Func<Task> cleanup)
    {
        List<Exception> errors = [];
        try { await record("CleanupRequested"); }
        catch (Exception error) { errors.Add(error); }
        var removed = false;
        try { await cleanup(); removed = true; }
        catch (Exception error) { errors.Add(error); }
        if (removed)
        {
            try { await record("Disposed"); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("SQL fixture recording and cleanup failures.", errors);
    }
}

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class CaseScenario
{
    public TestClock Clock { get; } = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    public CaseService Service { get; }
    public RegulatoryService Regulatory { get; }
    public SafetyCase Case { get; set; } = null!;
    public RegisterComplaint Input { get; }
    public CaseScenario(SqlFixture fixture, IKnowledgeSearch? knowledge = null)
    {
        Service = fixture.Service(Clock, knowledge);
        Regulatory = new(fixture.Store, fixture.Objects, Service, new MirComposer(), fixture.Policies, Clock);
        Input = new("TEST-" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid().ToString("N"),
            "Synthetic acceptance complaint", "SYNTHETISCH: Vorzeitiger Funktionsverlust mit Revisionsbedarf; Ursachen und klinische Befunde sind ungeklaert.",
            "ortho", null, "DEMO-LOT", "IE", "EU-IE", Seriousness.PublicHealthThreat,
            DateTimeOffset.Parse("2026-11-11T10:00:00Z"), DateTimeOffset.Parse("2026-11-12T10:00:00Z"), "acceptance", true);
    }
    public static string Key() => Guid.NewGuid().ToString("N");
    public async Task Create() => Case = await Service.Register(Input, SqlFixture.Pms, Key());
    public async Task Assess(AgentMode mode = AgentMode.Replay)
    {
        Case = await Service.RequestAssessment(Case.Id, new(Case.Version, mode, "Begruendete Analyse mit Quellen und Gegenhypothesen anfordern."), SqlFixture.Pms, Key());
        Case = await Service.ProcessAssessment(Case.Id, Case.AgentRuns.Last().Id);
    }
    public async Task Prepare(string kind = "Initial")
    {
        Case = await Service.Prepare(Case.Id,
            new(Case.Version, kind, Case.Jurisdiction, "de", kind != "Final", "Offene Ursachen und technische Befunde werden gezielt nachverfolgt.",
                "Bericht mit offengelegten Luecken und explizitem Untersuchungsstand erstellen."), SqlFixture.Pms, Key());
        if (Case.Jurisdiction != "GB")
            Case = await Regulatory.AttachMir(Case.Id, new(Case.Version, MirFacts(),
                "Synthetische menschliche Testangaben fuer den echten gepinnten XSD-Kontrakt."), SqlFixture.Medical, Key());
    }
    public MirFacts MirFacts() => new()
    {
        EventDateFrom = new(2026, 11, 11), EventDateTo = new(2026, 11, 11),
        NextReportDate = DateOnly.FromDateTime(Clock.Now.UtcDateTime).AddDays(7),
        EventClassification = Case.Seriousness == Seriousness.PublicHealthThreat ? "Serious public health threat" : "All other reportable incidents",
        DeviceProblemCode = "ZZZ", DeviceLocation = "Unknown",
        PreliminaryAnalysis = "SYNTHETISCH: Befunde und Gegenhypothesen werden untersucht. Kausalitaet bleibt offen; keine patientenbezogene Handlungsanweisung.",
        InitialCorrectiveAction = "SYNTHETISCH: Beweismittel sichern und gezielte technische Untersuchung dokumentieren.",
        FurtherInvestigation = "SYNTHETISCH: Ausstehende Befunde nachvollziehbar nachverfolgen.",
        EvidenceIds = Case.Evidence.Select(e => e.Id).ToArray(),
        Rationale = "SYNTHETISCHE TESTANGABEN: ZZZ ist nur eine strukturgueltige Testkodierung, kein fachlich validierter IMDRF-Code.",
        EventDateRationale = "SYNTHETISCHE TESTANGABE: Ereigniszeitraum aus dem ausdruecklich gewaehlten Testeingang, nicht aus Systemeingang.",
        AuthorityReference = Case.CurrentReport?.Kind == "Initial" ? null : "DEMO-NCA-REFERENCE",
        AuthorityReferenceRationale = "SYNTHETISCHE TESTANGABE: separat menschlich eingegebene NCA-Testreferenz, nicht aus einer Simulatorquittung abgeleitet.",
        ManufacturerName = Case.CurrentReport?.Preparation?.Governance?.Manufacturer.Name ?? "SYNTHETIC manufacturer",
        ManufacturerSrn = Case.CurrentReport?.Preparation?.Governance?.Manufacturer.Srn ?? "DEMO-SRN",
        ManufacturerCountry = Case.CurrentReport?.Preparation?.Governance?.Manufacturer.Country ?? "IE",
        ContactFirstName = "Synthetic", ContactLastName = "Testcontact", ContactStreet = "Demo street",
        ContactCity = "Demo city", ContactPostcode = "DEMO", ContactPhone = "000000", ContactEmail = "synthetic@example.invalid",
        SubmitterIsManufacturerContact = true,
        UdiDi = Case.CurrentReport?.Kind == "Final" ? "DEMO-TEST-UDI" : null,
        UdiPi = Case.CurrentReport?.Kind == "Final" ? "DEMO-TEST-LOT" : null,
        UdiEntity = Case.CurrentReport?.Kind == "Final" ? "N/A" : null,
        BasicUdiEntity = Case.CurrentReport?.Kind == "Final" ? "N/A" : null,
        Distribution = "SYNTHETISCH: IE; keine echten Marktangaben.",
        RootCauses = "Die Ursache bleibt trotz abgeschlossener synthetischer Untersuchung unsicher; dies ist keine Risikoakzeptanz.",
        RootCauseConfirmed = "No", RiskAssessmentReviewed = "Yes",
        RiskAssessmentAdequate = "No",
        RiskAssessmentResults = "SYNTHETISCH: Der Testreview dokumentiert verbleibende Luecken. Keine reale Risikoakzeptanz oder Freigabe eines Produkts.",
        SimilarIncidents = Case.CurrentReport?.Kind != "Final" ? null : new("Model",
            "SYNTHETISCHE AUSWAHL: gleiches Testmodell, keine Behauptung einer vollstaendigen realen Recherche.",
            "Number of devices implanted", null,
            "SYNTHETISCH: expliziter Testreview der vergleichbaren Meldungen; produkt-/ereignisbezogene Auswahl statt MAUDE-Inzidenz.",
            "Ereignisland IE; Original-MIR-Region EWR/CH/TR; weltweit jeweils getrennt, nicht addiert.",
            [new(DateOnly.FromDateTime(Clock.Now.UtcDateTime).AddYears(-1), DateOnly.FromDateTime(Clock.Now.UtcDateTime).AddDays(-1),
                null, null, null, null, null, null, "SYNTHETISCHE DATENLUECKE: keine belegten Periodenzaehler oder Expositionszahlen im Test, daher keine Nullwerte.")]),
        InvestigationTypeCode = "ZZZ", InvestigationFindingCode = "ZZZ", InvestigationConclusionCode = "ZZZ", ComponentCode = "ZZZ",
        CorrectiveAction = "Synthetische Untersuchung und dokumentierte Massnahmen im PMS-Regelkreis weiterverfolgen.",
        FinalComments = "SYNTHETISCHE ABSCHLUSSMELDUNG: Unsicherheit bleibt dokumentiert; keine reale Behoerdenmeldung."
    };
    public async Task Approve()
    {
        Case = await Service.Approve(Case.Id, new(Case.Version, "Medizinischer Inhalt mit konkreten Quellen geprueft.",
            "Vorlaeufige Angaben sind nachvollziehbar begrenzt; offene Befunde werden parallel untersucht."), SqlFixture.Medical, Key(), true);
        Case = await Service.Approve(Case.Id, new(Case.Version, "Regulatorischer Inhalt, Empfaenger und Versionsbindung geprueft.", null),
            SqlFixture.Ra, Key(), false);
    }
    public async Task Respond()
    {
        var recipient = new InformationRecipient("test-clinical-contact", "Synthetic test clinic", "Synthetic findings desk",
            "Simulated clinical safety contact", "Email", "findings@example.invalid", true);
        Case = await Service.AddInformationRequest(Case.Id, new(Case.Version,
            "Welche gezielten Befunde liegen zur Ursachenabgrenzung vor?", "Minimaler relevanter synthetischer Revisionsbefund",
            "Direkte Befunde sollen konkurrierende klinische und technische Ursachen abgrenzen.",
            "Simulated clinical safety contact", "Nur synthetische Demo, keine echte Patientenverarbeitung.",
            "Nur simulierte Offenlegung, keine echte Klinikkommunikation.", Clock.Now.AddDays(7), recipient), SqlFixture.Medical, Key());
        var request = Case.InformationRequests.Last();
        Case = await Service.ApproveInformation(Case.Id, request.Id, new(Case.Version, "Medizinische Notwendigkeit und Datensparsamkeit geprueft.", null), SqlFixture.Medical, Key());
        Case = await Service.ApproveInformation(Case.Id, request.Id, new(Case.Version, "Getrennte Offenlegungsbefugnis und Simulatorgrenze geprueft.", null), SqlFixture.Ra, Key());
        Case = await Service.DispatchInformation(Case.Id, request.Id, new(Case.Version, "Accept",
            "Exakt freigegebene synthetische Anfrage separat intern zustellen."), SqlFixture.Pms, Key());
        var dispatch = Case.InformationRequests.Last().Lifecycle!.Dispatches.Single();
        Case = await Service.ProcessInformationDispatch(Case.Id, request.Id, dispatch.Id);
        Case = await Service.RespondInformation(Case.Id, new(Case.Version, request.Id,
            "SYNTHETISCH: Gezielter Befund liegt vor. Mechanische und klinische Ursachen bleiben unabhaengig zu pruefen.",
            "Simulierten gezielten Befundausschnitt als neue Evidenz speichern.",
            new("test-response-" + request.Id, Case.Id, Case.ProductId, request.Lifecycle!.CorrelationId, recipient,
                "Synthetischer Testbefund, kein Klinikabruf.", true)), SqlFixture.Pms, Key());
        var response = Case.InformationResponses!.Last();
        Case = await Service.MapInformationResponse(Case.Id, response.Id, new(Case.Version, request.Id,
            request.Lifecycle.CorrelationId, Canonical.Hash(recipient), false,
            "Anfrage, Korrelation, Produkt und synthetischer Originalabsender wurden genau gegengeprueft."), SqlFixture.Pms, Key());
        Case = await Service.ReviewInformationResponse(Case.Id, response.Id, new(Case.Version,
            "Synthetische Herkunft und Befundrelevanz fuer die Bewertung geprueft; Kausalitaet bleibt weiterhin offen."),
            SqlFixture.Medical, Key());
    }
}
