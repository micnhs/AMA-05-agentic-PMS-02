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
    private string master = "";
    private string objectDirectory = "";
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
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync();
            using var command = new SqlCommand($"CREATE DATABASE [{database}]", connection);
            await command.ExecuteNonQueryAsync();
        }
        builder.InitialCatalog = database;
        ConnectionString = builder.ConnectionString;
        Store = new(ConnectionString);
        await Store.Initialize();
        objectDirectory = Path.Combine(home, ".local", database, "blobs");
        Objects = new LocalObjectStore(objectDirectory);
        Policies = DemoBaseline.LoadPolicies(Path.Combine(home, "policies"));
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
        await DemoBaseline.Seed(Path.Combine(home, "demo"), Store, Objects, Service(clock), clock, includeCases: SeedCases);
    }

    public CaseService Service(TimeProvider clock, IKnowledgeSearch? knowledge = null) =>
        new(Store, Objects, Policies, new AgentRunner(new(null, null, null, null)),
            knowledge ?? new KnowledgeSearch(Store, Objects, Store), clock);

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(master)) return;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        using var command = new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", connection);
        await command.ExecuteNonQueryAsync();
        if (Directory.Exists(objectDirectory)) Directory.Delete(objectDirectory, recursive: true);
        var directory = Path.GetDirectoryName(objectDirectory);
        if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
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
        UdiDi = Case.CurrentReport?.Kind == "Final" ? "DEMO-TEST-UDI" : null,
        UdiPi = Case.CurrentReport?.Kind == "Final" ? "DEMO-TEST-LOT" : null,
        UdiEntity = Case.CurrentReport?.Kind == "Final" ? "N/A" : null,
        BasicUdiEntity = Case.CurrentReport?.Kind == "Final" ? "N/A" : null,
        Distribution = "SYNTHETISCH: IE; keine echten Marktangaben.",
        RootCauses = "Die Ursache bleibt trotz abgeschlossener synthetischer Untersuchung unsicher; dies ist keine Risikoakzeptanz.",
        RootCauseConfirmed = "No", RiskAssessmentReviewed = "Yes",
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
        Case = await Service.AddInformationRequest(Case.Id, new(Case.Version,
            "Welche gezielten Befunde liegen zur Ursachenabgrenzung vor?", "Minimaler relevanter synthetischer Revisionsbefund",
            "Direkte Befunde sollen konkurrierende klinische und technische Ursachen abgrenzen.",
            "Simulated clinical safety contact", "Nur synthetische Demo, keine echte Patientenverarbeitung.",
            "Nur simulierte Offenlegung, keine echte Klinikkommunikation.", Clock.Now.AddDays(7)), SqlFixture.Medical, Key());
        var request = Case.InformationRequests.Last();
        Case = await Service.ApproveInformation(Case.Id, request.Id, new(Case.Version, "Medizinische Notwendigkeit und Datensparsamkeit geprueft.", null), SqlFixture.Medical, Key());
        Case = await Service.ApproveInformation(Case.Id, request.Id, new(Case.Version, "Getrennte Offenlegungsbefugnis und Simulatorgrenze geprueft.", null), SqlFixture.Ra, Key());
        Case = await Service.RespondInformation(Case.Id, new(Case.Version, request.Id,
            "SYNTHETISCH: Gezielter Befund liegt vor. Mechanische und klinische Ursachen bleiben unabhaengig zu pruefen.",
            "Simulierten gezielten Befundausschnitt als neue Evidenz speichern."), SqlFixture.Pms, Key());
    }
}
