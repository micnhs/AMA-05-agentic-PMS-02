namespace Cohors.Domain;

public sealed record ProductLifecycle(string Id, int Version, string ProductId, DateOnly? LastPlacingOnMarketLocalDate,
    bool Confirmed, string TimeZone, string RuleVersion, string SourceReference, string Rationale,
    string ConfirmedBy, DateTimeOffset RecordedAt, bool Synthetic);
public sealed record RetentionObjectBinding(string Contract, string DataClass, string ObjectKey, string Hash);
public sealed record RetentionAuthorization(string Id, string ActorId, string ScopeHash, DateTimeOffset At,
    string Rationale, bool ExternalCopiesReviewed, string ExternalCopiesReview);
public sealed record ObjectDeletionReceipt(string Contract, string OperationId, string ObjectKey, string Hash,
    DateTimeOffset At, string State, string Storage);
public sealed record RetentionRecord(string Id, int Version, string ProductId, RetentionObjectBinding Object,
    string PolicyVersion, string Reference, DateTimeOffset ReceivedAt, DateTimeOffset RecordedAt,
    bool Synthetic, bool LegalHold, string HoldReason, string Status, RetentionAuthorization? Authorization,
    int Attempts, DateTimeOffset? LastAttemptAt, ObjectDeletionReceipt? Deletion, string? ErrorCode)
{
    public string Contract { get; init; } = "cohors-retention-record-1";
}
public sealed record ObjectDependency(string Kind, string Id, int Version, string ProductId, string VersionHash);
public sealed record RetentionEvaluation(string PolicyVersion, DateOnly? MinimumRetainThroughLocalDate,
    DateTimeOffset? ExpiresAt, bool? MinimumIntervalElapsed, VersionReference? LifecycleSource,
    string ScopeHash, bool EligibleForAuthorization, bool CanDeleteNow, int RetainedReferences,
    string[] Blockers, string[] Calculation);

public static class RetentionRules
{
    public const string PolicyVersion = "cohors-retention-demo-2026.1";
    public const string ObjectContract = "cohors-retention-object-1";
    public const string DeletionContract = "cohors-raw-deletion-receipt-1";
    public const string Raw = "UnreferencedSyntheticRawQuarantine";
    public const string RawPrefix = "raw-quarantine/";
    public const string ImplantDocumentation = "ApplicableImplantTechnicalDocumentation";
    public const string LifecycleTimeZone = "Europe/Dublin";

    public static void RequireObject(RetentionObjectBinding value)
    {
        Guard.Against(value.Contract != ObjectContract || value.DataClass is not (Raw or ImplantDocumentation) ||
            value.ObjectKey != (value.DataClass == Raw ? RawPrefix + value.Hash : value.Hash) || value.Hash.Length != 64 ||
            value.Hash.Any(c => !char.IsAsciiHexDigit(c)) || value.Hash != value.Hash.ToLowerInvariant(),
            "retention_object_contract", "Explizite Datenklasse und unveraenderter inhaltsadressierter Objektbezug erforderlich.");
    }

    public static string Scope(RetentionRecord value, VersionReference? lifecycle) => Canonical.Hash(new {
        value.Id, value.ProductId, value.Object, value.PolicyVersion, value.Reference, value.ReceivedAt,
        value.RecordedAt, value.Synthetic, value.LegalHold,
        lifecycle, physicalProfile = "isolated-local-synthetic-raw-only"
    });

    public static RetentionEvaluation Evaluate(RetentionRecord value, ProductLifecycle? lifecycle,
        DateTimeOffset now, int references, bool localPhysicalProfile)
    {
        RequireObject(value.Object);
        Guard.Against(value.Contract != "cohors-retention-record-1" || value.PolicyVersion != PolicyVersion ||
            !value.Synthetic || references < 0 || value.Status is not ("Registered" or "Held" or "Authorized" or "Deleting" or "DeletionFailed" or "Deleted"),
            "retention_policy", "Unbekannte Aufbewahrungsregel, Datenklasse oder nicht synthetischer Vorgang.");
        var calculation = new List<string>();
        var blockers = new List<string>();
        DateOnly? through = null;
        DateTimeOffset? expires = null;
        VersionReference? lifecycleSource = null;
        if (value.Object.DataClass == Raw)
        {
            expires = value.ReceivedAt.AddDays(7);
            calculation.Add($"Explizites synthetisches Rohdaten-Testprofil: sieben volle Tage ab {value.ReceivedAt:O}; Grenze {expires:O}, einschliesslich.");
            calculation.Add("Keine allgemeine Rohdaten-/Personendatenregel und keine produktive WORM-Dauer.");
        }
        else if (lifecycle is { Confirmed: true, LastPlacingOnMarketLocalDate: not null })
        {
            Guard.Against(lifecycle.ProductId != value.ProductId || !lifecycle.Synthetic ||
                lifecycle.TimeZone != LifecycleTimeZone || lifecycle.RuleVersion != PolicyVersion ||
                string.IsNullOrWhiteSpace(lifecycle.SourceReference) || string.IsNullOrWhiteSpace(lifecycle.ConfirmedBy),
                "retention_lifecycle", "Produktbezogene, bestaetigte synthetische Lifecycle-Quelle passt nicht.");
            lifecycleSource = new(lifecycle.Id, lifecycle.Version, Canonical.Hash(lifecycle));
            through = lifecycle.LastPlacingOnMarketLocalDate.Value.AddYears(15);
            expires = DeadlineCalculator.LocalDateEnd(through.Value, lifecycle.TimeZone).AddTicks(1);
            calculation.Add($"Synthetisches MDR-Art.-10(8)-Mindestfristbeispiel: letztes bestaetigtes Inverkehrbringen {lifecycle.LastPlacingOnMarketLocalDate:yyyy-MM-dd} + 15 Jahre.");
            calculation.Add($"Mindestens bis einschliesslich {through:yyyy-MM-dd} in {lifecycle.TimeZone}; Objekteingang {value.ReceivedAt:O} und Erfassung {value.RecordedAt:O} sind nicht der Fristbeginn.");
            calculation.Add($"Bestaetigter Lifecycle-Stand {lifecycle.Id}@{lifecycle.Version}; Quelle {lifecycle.SourceReference}. {lifecycle.Rationale}");
        }
        else
        {
            blockers.Add("lifecycle_missing_or_unconfirmed");
            calculation.Add("Letztes Inverkehrbringen fehlt oder ist unbestaetigt. Kein Ersatz durch Upload-, Eingangs- oder Freeze-Datum.");
        }
        bool? elapsed = expires is null ? null : now >= expires;
        if (elapsed != true) blockers.Add("minimum_interval_not_elapsed");
        if (value.Object.DataClass != Raw) blockers.Add("regulatory_deletion_not_authorized_in_pilot");
        if (!localPhysicalProfile) blockers.Add("physical_profile_not_enabled");
        if (value.LegalHold) blockers.Add("legal_hold");
        if (references > 0) blockers.Add("retained_current_or_historical_reference");
        if (value.Status == "Deleted") blockers.Add("already_deleted");
        var eligible = blockers.Count == 0;
        var scope = Scope(value, lifecycleSource);
        if (value.Authorization is not { ExternalCopiesReviewed: true } authorization ||
            authorization.ScopeHash != scope || string.IsNullOrWhiteSpace(authorization.ExternalCopiesReview))
            blockers.Add("explicit_scoped_authorization_required");
        calculation.Add("Fristablauf beendet weder PSUR noch PMS und ist keine Loeschfreigabe. Holds, Original-/Snapshot-/Shared-Hash-Referenzen und alle betroffenen Speicher bleiben separate Sperren.");
        calculation.Add("Physische Pilotloeschung betrifft nur eigens erzeugte, unreferenzierte synthetische Testrohobjekte im lokalen Profil; keine produktive Langzeitsperre wird aktiviert.");
        return new(PolicyVersion, through, expires, elapsed, lifecycleSource, scope, eligible,
            eligible && blockers.Count == 0, references, blockers.ToArray(), calculation.ToArray());
    }

    public static void RequireDeletionReceipt(RetentionRecord value, ObjectDeletionReceipt receipt)
    {
        Guard.Against(receipt.Contract != DeletionContract || receipt.State != "Deleted" || receipt.Storage != "Local" ||
            receipt.ObjectKey != value.Object.ObjectKey || receipt.Hash != value.Object.Hash ||
            receipt.OperationId != value.Authorization?.Id || value.Object.DataClass != Raw,
            "retention_deletion_receipt", "Kein passender datenklassen-, objekt- und auftragsgebundener Loeschbeleg.", 409);
    }
}
