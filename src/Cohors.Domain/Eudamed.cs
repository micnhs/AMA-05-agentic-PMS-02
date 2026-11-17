namespace Cohors.Domain;

public enum EudamedMode { Simulator, Domibus }
public enum EudamedEnvironment { Simulation, Playground, Production }
public enum EudamedDossierKind { MIR, MTR, PSR, PSRP, PSUR, FSCA, FSN }

public sealed record EudamedContact(string FirstName, string FamilyName, string Email, string Telephone);
public sealed record EudamedActorDetails(string Organization, string Role, string Country,
    string Address, EudamedContact Contact, bool Synthetic);
public sealed record EudamedActorRound(int Revision, string Status, string Scenario, string Issuer,
    string Reason, DateTimeOffset At, string ApplicationHash, string? SimulatedIdentity);
public sealed record EudamedActorApplication(string Id, int Version, string ProductId,
    EudamedActorDetails Details, string Status, string? SimulatedIdentity, EudamedActorRound[] Rounds,
    string Origin, DateTimeOffset CreatedAt);

public sealed record EudamedCertificate(string Number, string Revision, string NotifiedBody,
    string Scope, DateOnly ValidFrom, DateOnly ValidThrough, string OriginalHash, string Provenance,
    string VerificationStatus, bool Synthetic);
public sealed record EudamedReferences(string Id, int Version, string ProductId,
    VersionReference ActorApplication, EudamedActorDetails Manufacturer, string ActorIdentity,
    Device Device, VersionReference GovernanceSource, DeviceRegulatoryProfile RegulatoryDevice,
    EudamedCertificate Certificate, string EvaluatingNotifiedBody, string MappingRationale,
    string ConfirmedBy, DateTimeOffset ConfirmedAt, bool Synthetic, string UdiIssuingAgency);

public sealed record DomibusProfile(string EndpointReference, string SecretReference, string SecretVersion,
    string ModuleKeyReference, string ModuleKeyVersion, string Domain, string AccessPointPartyId,
    string ReceiverPartyId, string PartyIdType, string FromRole, string ToRole, string Service,
    string ServiceType, string Action, string OriginalSender, string FinalRecipient,
    string OnboardingEvidenceReference);
public sealed record EudamedProfile(EudamedMode Mode, EudamedEnvironment Environment,
    string ActorId, int ActorVersion, string ReferencesId, int ReferencesVersion, string Recipient,
    string BusinessContract, string GatewayContract, DomibusProfile? Domibus);
public sealed record EudamedConfigurationDraft(string Id, int Version, string ProductId,
    EudamedProfile Profile, string ChangedBy, DateTimeOffset ChangedAt);
public sealed record EudamedChannel(string Id, int Version, string ProductId, long DispatchGeneration,
    EudamedProfile Profile, VersionReference DraftSource, string ActivatedBy, DateTimeOffset ActivatedAt, bool Enabled);
public sealed record EudamedReadiness(string ConfigurationStatus, string ConnectionStatus,
    string BusinessStatus, bool CanActivate, bool CanDispatch, string[] Blockers, string EvidenceLevel);
public sealed record DomibusConnectionCheck(string Id, int Version, string ProductId,
    VersionReference DraftSource, DomibusProfile Profile, string Status, string? ErrorCode, DateTimeOffset CheckedAt,
    string CheckedBy, string EvidenceLevel);
public sealed record EudamedBinding(string ChannelId, int ChannelVersion, long DispatchGeneration,
    string ProfileHash, EudamedProfile Profile, VersionReference ActorSource,
    VersionReference ReferencesSource, string ActorIdentity, string WsdlSha256, bool Simulated);

public sealed record EudamedSource(string Kind, VersionReference Source, string Json);
public sealed record EudamedDocument(string Id, int Version, string ProductId, string Name, string MediaType,
    string OriginalHash, int Length, string Provenance, string RecordedBy, DateTimeOffset RecordedAt, bool Synthetic);
public sealed record EudamedAttachment(string Name, string MediaType, string Hash, VersionReference Source);
public sealed record EudamedPeriod(DateOnly Start, DateOnly End, string Basis);
public sealed record EudamedPsrTerms(int FrequencyMonths, DateOnly FirstPeriodStart, string Rationale);
public sealed record EudamedMtrContext(DateOnly IdentifiedAt, bool ClinicalInvestigationRelated,
    string? ClinicalInvestigationId, string Rationale);
public sealed record FscaContent(string[] Batches, string[] SerialNumbers, string MeasureType,
    string ManufacturerAction, string UserAction, string ResponsiblePerson, string EffectivenessCriterion,
    DateTimeOffset DueAt, string[] CapaIds, DateOnly DecisionDate, string HealthHazard, string HazardProbability,
    string RootCauseStatus, string RootCauseRationale, string[] ManufacturerActionTypes, string[] UserActionTypes,
    string PatientFollowUp);
public sealed record FsnContent(string FscaId, int FscaVersion, string Audience, string Instructions,
    string DocumentHash, string DocumentName, string DocumentVersion, string PublicationState,
    bool Urgent, string ReportType, DateOnly DocumentDate);
public sealed record EudamedDossierContent(string Title, string ClinicalRationale,
    string RegulatoryRationale, string Language, string Jurisdiction, string[] Countries,
    string IntendedAction, string? ParentId, int? ParentVersion, EudamedPeriod? Period,
    FscaContent? Fsca, FsnContent? Fsn, string? ReplyToCorrespondenceId = null, EudamedPsrTerms? PsrTerms = null,
    EudamedMtrContext? Mtr = null);
public sealed record EudamedValidation(string Contract, string SchemaVersion, string TemplateVersion,
    string MappingVersion, string SchemaFingerprint, string EnumerationFingerprint,
    string SchemaRuntime, string RuntimeSchemaFingerprint, string[] Errors, string[] Warnings, string EvidenceLevel);
public sealed record EudamedPackage(string Contract, EudamedDossierKind Kind, string DossierId,
    int Revision, string ProductId, EudamedBinding Binding, EudamedReferences References,
    EudamedDossierContent Content, CalendarProfile Calendar, EudamedSource[] Sources, string BusinessXml,
    string BusinessXmlHash, EudamedAttachment[] Attachments, EudamedValidation Validation, bool Simulated);
public sealed record EudamedAttempt(string Id, string CorrelationId, string MessageId,
    int DossierRevision, string PackageHash, EudamedBinding Binding, string Scenario, string ScenarioVersion,
    string Status, int QueuedVersion, DateTimeOffset QueuedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, string? ReceiptHash, string? ErrorCode);
public sealed record EudamedCaseObligation(string CaseId, string ReportScopeHash, int ReportVersion,
    string Jurisdiction, string[] ObligationIds, string[] FollowUpIds);
public sealed record EudamedDeliveryEntry(string DossierId, VersionReference ApprovedDossier,
    EudamedAttempt Attempt, string BusinessStatus, int LastBusinessSequence)
{
    public string IntendedAction { get; init; } = "Submit";
    public EudamedCaseObligation? CaseObligation { get; init; }
    public EudamedPlannedResponse[] PlannedResponses { get; init; } = [];
    public EudamedBusinessResponse[] Responses { get; init; } = [];
    public EudamedCorrespondence[] Correspondence { get; init; } = [];
}
public sealed record EudamedDeliveryLedger(string Id, int Version, string ProductId,
    string ObligationKey, EudamedDeliveryEntry[] Entries);
public sealed record EudamedPlannedResponse(string Id, string AttemptId, int Sequence,
    string Kind, DateTimeOffset DueAt, string Status, string Content, string? PublishedInboxId);
public sealed record EudamedBusinessResponse(string InboxId, string MessageId, string AttemptId,
    string OriginalHash, int Sequence, string Status, string? DossierCode, int? DossierVersion,
    string[] Errors, EudamedPeriod? ReturnedPeriod, DateTimeOffset At, bool Simulated, bool Applied);
public sealed record EudamedCorrespondence(string Id, string InboxId, string OriginalHash,
    string Issuer, string Kind, string Content, int DossierRevision, DateTimeOffset ReceivedAt,
    DateTimeOffset? DueAt, string OwnerRole, string Status, string? ReplyDossierId, bool Simulated);
public sealed record FsnDistribution(string Id, int DossierRevision, string PackageHash,
    string Audience, string Channel, string EvidenceHash, DateTimeOffset At, string ActorId, bool Simulated);
public sealed record EudamedDossier(string Id, int Version, string ProductId, EudamedDossierKind Kind,
    int Revision, EudamedPackage Package, string ScopeHash, string ReviewStatus, Approval[] Approvals,
    string? FrozenPackageHash, EudamedAttempt[] Attempts, EudamedPlannedResponse[] PlannedResponses,
    EudamedBusinessResponse[] BusinessResponses, EudamedCorrespondence[] Correspondence,
    FsnDistribution[] Distributions, string CreatedBy, DateTimeOffset CreatedAt)
{
    public string[] AccessProducts { get; init; } = [];
}

public sealed record EudamedInboxOriginal(string MessageId, string CorrelationId, string AttemptId,
    string DossierId, int DossierRevision, string PackageHash, int Sequence, string Kind,
    string Status, string Issuer, string Content, string? RemoteDossierCode, int? RemoteDossierVersion,
    string[] Errors, EudamedPeriod? ReturnedPeriod, DateTimeOffset? DueAt, bool Simulated);
public sealed record EudamedInbox(string Id, int Version, string ProductId, EudamedBinding Binding,
    EudamedInboxOriginal? Original, string OriginalHash, string? ProtectedTransportHash,
    string MessageId, string Status, string DownloadStatus, string? ErrorCode,
    DateTimeOffset ReceivedAt, DateTimeOffset? DownloadConfirmedAt, int DuplicateReceipts = 0);

public sealed record EudamedCapability(EudamedDossierKind Kind, string[] DocumentedOperations,
    string[] SimulatorActions, string Boundary);

public static class EudamedContracts
{
    public const string Simulator = "cohors-eudamed-simulator-v1";
    public const string Business = "eudamed-vigilance-3.0.36.1";
    public const string Mapping = "cohors-eudamed-cdm-v1";
    public const string SchemaRuntime = "cohors-dotnet-keyscope-1";
    public const string Gateway = "domibus-ws-5.2.2-JEE10";
    public const string WsdlSha256 = "568ec6889572a9bd8c3256607de3d910b2a231f2b59f472b56a4682593d4b834";
    public const string PlaygroundSchema = "3.0.36.1";
    public const string ProductionSchema = "3.0.30";
    public const string SimulationOrigin = "Cohors local simulator; no official EUDAMED registration";
    public static readonly string[] SimulatorScenarios =
        ["Accept", "DraftOnly", "Reject", "TechnicalFailure", "Unknown", "Delayed", "OutOfOrder",
            "Comment", "InformationRequest", "PeriodMismatch"];
    public static readonly EudamedCapability[] Capabilities =
    [
        new(EudamedDossierKind.MIR, ["GET", "POST", "PUT"], ["Submit", "FollowUp", "Reply"], "Produktive Vigilanz ist nicht aktiviert."),
        new(EudamedDossierKind.MTR, ["GET", "POST", "PUT"], ["Submit", "Reply"], "Statistischer Trigger ist keine menschliche Meldeentscheidung."),
        new(EudamedDossierKind.PSR, ["GET", "POST"], ["Submit", "Reply"], "Keine PSR.PUT-Faehigkeit; konkrete Vereinbarung erforderlich."),
        new(EudamedDossierKind.PSRP, ["POST"], ["Submit", "Reply"], "PSR Periodic Analysis Update; keine PSRP.GET/PUT-Faehigkeit."),
        new(EudamedDossierKind.PSUR, ["GET", "POST", "PUT"], ["Submit", "Reply"], "Reale Schreibrechte verlangen MF-Confirmer und gesonderte Serviceabnahme."),
        new(EudamedDossierKind.FSCA, ["GET", "POST", "PUT"], ["Submit", "FollowUp", "Reply"], "Dokumentierter Vorschlagsumfang ist keine reale Aktivierungsfreigabe."),
        new(EudamedDossierKind.FSN, ["GET", "POST"], ["Submit", "Revision", "Reply"], "FSN.PUT ist nicht implementiert; Revision und Verteilung sind Simulator-only.")
    ];

    public static string ChannelId(string product) => "eudamed-channel-" + product;
    public static string DraftId(string product) => "eudamed-draft-" + product;
    public static VersionReference Bind(EudamedActorApplication value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(EudamedReferences value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(EudamedConfigurationDraft value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(EudamedChannel value) => new(value.Id, value.Version, Canonical.Hash(value));
    public static VersionReference Bind(EudamedDossier value) => new(value.Id, value.Version, Canonical.Hash(value));

    public static void RequireActorDetails(EudamedActorDetails value)
    {
        Guard.Against(!value.Synthetic, "eudamed_synthetic_actor", "Dieser Antrag registriert nur einen ausdruecklich synthetischen Test-Actor.");
        Guard.Text(value.Organization, "Synthetische Organisation", 5, 200);
        Guard.Against(!value.Organization.StartsWith("DEMO ", StringComparison.Ordinal) &&
            !value.Organization.StartsWith("SYNTHETISCH ", StringComparison.Ordinal),
            "eudamed_actor_label", "Die Organisation muss mit DEMO oder SYNTHETISCH gekennzeichnet sein.");
        Guard.Against(value.Role is not ("MF" or "AR"), "eudamed_actor_role", "Der Herstellerpilot verwendet MF oder eine ausdrueckliche AR-Testrolle, keine CA-/NB-Identitaet.");
        Guard.Against(value.Country is not { Length: 2 } || value.Country.Any(c => c is < 'A' or > 'Z'),
            "eudamed_actor_country", "Expliziter zweistelliger Laendercode erforderlich.");
        Guard.Text(value.Address, "Synthetische Anschrift", 10, 500);
        var contact = value.Contact ?? throw new DomainException("eudamed_contact", "Strukturierter synthetischer Kontakt fehlt.");
        Guard.Text(contact.FirstName, "Synthetischer Vorname", 2, 100);
        Guard.Text(contact.FamilyName, "Synthetischer Nachname", 2, 100);
        Guard.Text(contact.Telephone, "Synthetische Telefonnummer", 5, 50);
        Guard.Against(!System.Net.Mail.MailAddress.TryCreate(contact.Email, out var address) ||
            !address.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase),
            "eudamed_actor_contact", "Nur eine synthetische Kontaktadresse unter .invalid verwenden.");
    }

    public static void RequireRegistered(EudamedActorApplication value)
    {
        RequireActorDetails(value.Details);
        Guard.Against(value.Status != "SimulatedApproved" || value.SimulatedIdentity is null ||
            !value.SimulatedIdentity.StartsWith("DEMO-EUDAMED-", StringComparison.Ordinal) ||
            value.Origin != SimulationOrigin, "eudamed_actor_unapproved",
            "Nur die versioniert freigeschaltete Testidentitaet ist im Simulator verwendbar.", 409);
    }

    public static string[] ProfileErrors(EudamedProfile profile)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(profile.Mode) || !Enum.IsDefined(profile.Environment)) errors.Add("Unbekannte Betriebsart oder Umgebung.");
        if (profile.Mode == EudamedMode.Simulator && (profile.Environment != EudamedEnvironment.Simulation || profile.Domibus is not null))
            errors.Add("Simulator verlangt Simulation ohne externe Endpunkte oder Secret-Referenzen.");
        if (profile.Mode == EudamedMode.Domibus && (profile.Environment == EudamedEnvironment.Simulation || profile.Domibus is null))
            errors.Add("Domibus verlangt einen getrennten Playground-/Production-Entwurf.");
        if (profile.BusinessContract != Business) errors.Add("Unbekannter fachlicher Vertrag; keine stillschweigende Versionsannahme.");
        if (profile.GatewayContract != Gateway) errors.Add("Nur der exakt gebundene Domibus-5.2.2-JEE10-WS-Vertrag wird unterstuetzt.");
        if (string.IsNullOrWhiteSpace(profile.Recipient)) errors.Add("Ein ausdruecklicher Empfaenger fehlt.");
        if (string.IsNullOrWhiteSpace(profile.ActorId) || profile.ActorVersion < 1) errors.Add("Eine versionierte Actor-Zuordnung fehlt.");
        if (string.IsNullOrWhiteSpace(profile.ReferencesId) || profile.ReferencesVersion < 1) errors.Add("Bestaetigte Geraete-/Zertifikatsreferenzen fehlen.");
        return errors.ToArray();
    }

    public static void RequireBinding(EudamedBinding binding)
    {
        Guard.Against(binding.ProfileHash != Canonical.Hash(binding.Profile) ||
            binding.WsdlSha256 != WsdlSha256 || binding.ChannelVersion < 1 || binding.DispatchGeneration < 1 ||
            binding.Simulated != (binding.Profile.Mode == EudamedMode.Simulator),
            "eudamed_binding", "Kanal-/Vertragsbindung ist nicht konsistent.", 409);
        Guard.Against(ProfileErrors(binding.Profile).Length != 0, "eudamed_profile", "Gebundenes Profil ist unvollstaendig.", 409);
    }

    public static void RequireCurrentChannel(EudamedBinding binding, EudamedChannel channel)
    {
        RequireBinding(binding);
        Guard.Against(!channel.Enabled || binding.ChannelId != channel.Id || binding.ChannelVersion != channel.Version ||
            binding.DispatchGeneration != channel.DispatchGeneration || binding.ProfileHash != Canonical.Hash(channel.Profile),
            "eudamed_channel_fenced", "Das urspruengliche Profil ist nicht mehr aktiv. Kein Umleiten oder automatischer Nachversand.", 409);
    }
}
