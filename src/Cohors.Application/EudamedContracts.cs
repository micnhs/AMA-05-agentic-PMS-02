using Cohors.Domain;

namespace Cohors.Application;

public sealed record SaveEudamedActorCommand(int ExpectedVersion, string ProductId,
    EudamedActorDetails Details, string Reason);
public sealed record SubmitEudamedActorCommand(int ExpectedVersion, string Scenario, string Reason);
public sealed record MapEudamedReferencesCommand(int ExpectedVersion, string ProductId,
    string ActorId, int ActorVersion, int GovernanceVersion, string CertificateRevision,
    DateOnly ValidFrom, DateOnly ValidThrough, string CertificateText, string EvaluatingNotifiedBody,
    string UdiIssuingAgency, string Reason);
public sealed record SaveEudamedConfigurationCommand(int ExpectedVersion, string ProductId, EudamedProfile Profile, string Reason);
public sealed record ActivateEudamedConfigurationCommand(int ExpectedVersion, int DraftVersion,
    bool ConfirmImpact, string Reason);
public sealed record CheckDomibusCommand(int ExpectedVersion, int DraftVersion, string Reason);
public sealed record EudamedSourceSelection(string Kind, string Id, int Version);
public sealed record DraftEudamedDossierCommand(int ExpectedVersion, string ProductId, EudamedDossierKind Kind,
    EudamedDossierContent Content, EudamedSourceSelection[] Sources, string Reason);
public sealed record SubmitEudamedDossierCommand(int ExpectedVersion, int ExpectedLedgerVersion, string Scenario, string Reason);
public sealed record AdvanceEudamedSimulatorCommand(int ExpectedVersion, string EventId, bool Duplicate, string Reason);
public sealed record EudamedDistributionCommand(int ExpectedVersion, string Audience, string Channel, string Evidence, string Reason);
public sealed record EudamedDocumentCommand(string ProductId, string Name, string MediaType,
    string ContentBase64, string Provenance, string Reason);
public sealed record EudamedRenderedDocument(byte[] Bytes, string MediaType, string Name);
public sealed record EudamedSourceOption(string Kind, string Id, int Version, string ProductId, string Label)
{
    public string? Language { get; init; }
    public string? Jurisdiction { get; init; }
    public EudamedPeriod? Period { get; init; }
    public EudamedDossierKind? DossierKind { get; init; }
    public string? OriginalHash { get; init; }
}
public sealed record EudamedDossierView(EudamedDossier Dossier, EudamedDeliveryLedger Ledger);
public sealed record EudamedDossierSummary(string Id, int Version, string ProductId, EudamedDossierKind Kind,
    int Revision, string Title, string ReviewStatus, string ScopeHash, EudamedBinding Binding,
    Approval[] Approvals, EudamedAttempt[] Attempts, EudamedBusinessResponse[] BusinessResponses,
    int OpenCorrespondence, EudamedValidation Validation);
public sealed record DomibusReferenceChoice(string Id, string Version);
public sealed record DomibusReferenceCatalog(string[] Endpoints, DomibusReferenceChoice[] Credentials,
    DomibusReferenceChoice[] ModuleKeys, string[] OnboardingEvidence);
public sealed record EudamedContractInfo(string Business, string Gateway, string WsdlSha256, string SchemaRuntime);
public sealed record EudamedWorkspace(EudamedMode EffectiveMode, bool PersistedConfiguration,
    EudamedChannel? ActiveChannel, EudamedConfigurationDraft? Draft, EudamedReadiness ActiveReadiness,
    EudamedReadiness? DraftReadiness, DomibusConnectionCheck? ConnectionCheck,
    EudamedActorApplication[] Actors, EudamedReferences[] References, EudamedDossierSummary[] Dossiers,
    EudamedInbox[] Inbox, EudamedCapability[] Capabilities, DomibusReferenceCatalog ReferenceCatalog,
    EudamedContractInfo Contract, string Boundary);

public sealed record DomibusPayload(string Id, string ContentType, byte[] Bytes);
public sealed record DomibusMessage(string MessageId, string ConversationId, string? RefToMessageId, DomibusPayload[] Payloads);
public sealed record DomibusRetrievedMessage(string MessageId, string ConversationId, string? RefToMessageId,
    string FromPartyId, string ToPartyId, DomibusPayload[] Payloads, byte[] OriginalTransport);
public sealed record DomibusSubmitReceipt(string MessageId, long? MessageEntityId, byte[] OriginalTransport);
public sealed record DomibusStatusResult(string Status, byte[] OriginalTransport);
public sealed record DomibusError(string? Code, string? MessageId, string? AccessPointRole);
public sealed record DomibusErrorsResult(DomibusError[] Errors, byte[] OriginalTransport);
public sealed record DomibusAvailability(bool Configured, string[] Blockers, string EvidenceLevel);

public interface IDomibusGateway
{
    string[] AllowedEndpointReferences { get; }
    DomibusReferenceCatalog ReferenceCatalog { get; }
    DomibusAvailability Availability(DomibusProfile profile);
    Task Check(DomibusProfile profile, CancellationToken ct = default);
    Task<DomibusSubmitReceipt> Submit(DomibusProfile profile, DomibusMessage message, CancellationToken ct = default);
    Task<DomibusStatusResult> Status(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default);
    Task<DomibusErrorsResult> Errors(DomibusProfile profile, string messageId, string accessPointRole, CancellationToken ct = default);
    Task<string[]> Pending(DomibusProfile profile, CancellationToken ct = default);
    Task<DomibusRetrievedMessage> Retrieve(DomibusProfile profile, string messageId, CancellationToken ct = default);
    Task<byte[]> ConfirmDownload(DomibusProfile profile, string messageId, CancellationToken ct = default);
}

public sealed class DomibusException(string code, bool outcomeUnknown, Exception? cause = null, byte[]? originalTransport = null)
    : Exception("Domibus-Backendoperation nicht bestaetigt: " + code)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public string? CauseType { get; } = cause?.GetType().Name;
    public byte[]? OriginalTransport { get; } = originalTransport;
}

public interface ITransportEvidenceStore
{
    Task<string> Protect(ReadOnlyMemory<byte> original, CancellationToken ct = default);
}

public interface IEudamedComposer
{
    (string Xml, EudamedValidation Validation) Compose(EudamedDossierKind kind, string id,
        EudamedDossierContent content, EudamedBinding binding, EudamedReferences references,
        CalendarProfile calendar, EudamedSource[] sources, EudamedAttachment[] attachments);
    string[] Validate(string xml, EudamedDossierKind kind);
}

public interface IEudamedDocumentRenderer
{
    Task<EudamedRenderedDocument> Render(EudamedDossierKind kind, string id, EudamedDossierContent content,
        EudamedSource[] sources, Actor actor, CancellationToken ct = default);
}
