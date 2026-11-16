namespace Cohors.Domain;

public sealed record InformationRecipient(string Id, string Organization, string Contact, string Role,
    string IntendedChannel, string Destination, bool Synthetic);
public sealed record InformationRequestLifecycle(string Contract, string CaseId, string ProductId,
    string CorrelationId, InformationRecipient Recipient, DateTimeOffset CreatedAt, string CreatedBy,
    InformationDispatch[] Dispatches);
public sealed record InformationDispatch(string Id, int QueuedCaseVersion, string ScopeHash, string CorrelationId,
    string RecipientHash, string RequestedBy, DateTimeOffset QueuedAt, string Scenario, string Status,
    DateTimeOffset? AttemptedAt, DateTimeOffset? CompletedAt, string? ReceiptReference, string? Error,
    InformationSimulatorReceipt? SimulatorReceipt);
public sealed record InformationSimulatorReceipt(string Reference, string RequestId, string CaseId, string ProductId,
    string CorrelationId, string RecipientHash, string ScopeHash, DateTimeOffset ReceivedAt);
public sealed record InformationResponseEnvelope(string ExternalId, string? CaseId, string? ProductId,
    string? CorrelationId, InformationRecipient Sender, string SourceDescription, bool Synthetic);
public sealed record InformationResponse(string Id, string DeclaredRequestId, InformationResponseEnvelope Envelope,
    string Content, string ContentHash, string OriginalHash, DateTimeOffset ReceivedAt, string ReceivedBy,
    string Status, string[] CandidateRequestIds, string MatchExplanation,
    InformationResponseMapping? Mapping, InformationResponseReview? Review);
public sealed record InformationResponseMapping(string RequestId, string CorrelationId, string RequestScopeHash,
    string RecipientHash, string EvidenceId, string ActorId, string ActorName, DateTimeOffset At, string Reason,
    bool MissingReferencesConfirmed);
public sealed record InformationResponseReview(string ActorId, string ActorName, DateTimeOffset At,
    string Reason, int EvidenceVersion);

public static class InformationRules
{
    public const string Contract = "cohors-information-request-1";

    public static InformationRecipient RequireRecipient(InformationRecipient? recipient)
    {
        var value = recipient ?? throw new DomainException("request_recipient",
            "Konkreten synthetischen Empfaenger, Organisation, Rolle und vorgesehenen Kanal ausdruecklich erfassen.");
        Guard.Against(!value.Synthetic, "synthetic_only", "Nur ausdruecklich synthetische Kontakte; keine echte Klinikkommunikation.");
        Guard.Text(value.Id, "Synthetische Kontaktreferenz", 3, 100);
        Guard.Text(value.Organization, "Organisation", 3, 200);
        Guard.Text(value.Contact, "Benannter Kontakt oder eindeutige Empfangsstelle", 3, 200);
        Guard.Text(value.Role, "Empfaengerrolle", 3, 150);
        Guard.Text(value.Destination, "Synthetische Zielreferenz", 3, 240);
        Guard.Against(value.IntendedChannel is not ("Email" or "Phone" or "Portal"),
            "request_channel", "Vorgesehenen E-Mail-, Telefon- oder Portalkanal ausdruecklich waehlen; Transport bleibt ausschliesslich intern simuliert.");
        return value;
    }

    public static string Scope(InformationRequest request)
    {
        var life = request.Lifecycle ?? throw new DomainException("request_legacy",
            "Altanfrage ohne nachweisbaren Empfaenger-/Korrelationsvertrag: neue vollstaendige Anfrage erstellen, Historie erhalten.");
        return Canonical.Hash(new { life.Contract, life.CaseId, life.ProductId, request.Id, life.CorrelationId,
            life.Recipient, request.Question, request.MinimalData, request.Relevance, request.RecipientRole,
            request.ManufacturerBasis, request.DisclosureBasis, request.DueAt, request.Simulated });
    }

    public static InformationRequestLifecycle RequireBound(SafetyCase c, InformationRequest request)
    {
        var life = request.Lifecycle ?? throw new DomainException("request_legacy",
            "Empfaenger, Korrelation und Versandbeleg wurden fuer diese Altanfrage nicht erfasst. Keine nachtraegliche Versandbehauptung.");
        RequireRecipient(life.Recipient);
        Guard.Against(!request.Simulated || life.Contract != Contract || life.CaseId != c.Id ||
            life.ProductId != c.ProductId || life.Recipient.Role != request.RecipientRole ||
            string.IsNullOrWhiteSpace(life.CorrelationId) || request.ScopeHash != Scope(request),
            "request_scope", "Anfrageinhalt, Fall, Produkt, Empfaenger oder Korrelationsbindung stimmen nicht ueberein.", 409);
        return life;
    }

    public static void RequireApprovals(SafetyCase c, InformationRequest request)
    {
        RequireBound(c, request);
        var medical = request.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == request.ScopeHash);
        var regulatory = request.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == request.ScopeHash);
        Guard.Against(medical is null || regulatory is null || medical.ActorId == regulatory.ActorId,
            "request_gate", "Genau gebundene medizinische und separate RA-Freigabe erforderlich.");
    }

    public static bool ReceiptMatches(InformationRequest request, InformationDispatch dispatch)
    {
        var life = request.Lifecycle;
        var receipt = dispatch.SimulatorReceipt;
        return life is not null && receipt is not null && dispatch.ScopeHash == request.ScopeHash &&
            dispatch.CorrelationId == life.CorrelationId && dispatch.RecipientHash == Canonical.Hash(life.Recipient) &&
            receipt.RequestId == request.Id && receipt.CaseId == life.CaseId && receipt.ProductId == life.ProductId &&
            receipt.ScopeHash == request.ScopeHash && receipt.CorrelationId == life.CorrelationId &&
            receipt.RecipientHash == dispatch.RecipientHash;
    }

    public static bool HasConfirmedDelivery(InformationRequest request) =>
        request.Lifecycle?.Dispatches.Any(d => d.Status == "SimulatedSent" &&
            ReceiptMatches(request, d) && d.ReceiptReference == d.SimulatorReceipt!.Reference) == true;

    public static InformationRequest[] Candidates(SafetyCase c, string requestId, InformationResponseEnvelope envelope) =>
        c.InformationRequests.Where(r => r.Lifecycle is { } life && HasConfirmedDelivery(r) &&
            (string.IsNullOrEmpty(requestId) || r.Id == requestId) &&
            (string.IsNullOrEmpty(envelope.CaseId) || envelope.CaseId == c.Id) &&
            (string.IsNullOrEmpty(envelope.ProductId) || envelope.ProductId == c.ProductId) &&
            (string.IsNullOrEmpty(envelope.CorrelationId) || envelope.CorrelationId == life.CorrelationId) &&
            Canonical.Hash(envelope.Sender) == Canonical.Hash(life.Recipient)).ToArray();

    public static bool CompleteReferences(string requestId, InformationResponseEnvelope envelope) =>
        !string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(envelope.CaseId) &&
        !string.IsNullOrEmpty(envelope.ProductId) && !string.IsNullOrEmpty(envelope.CorrelationId);

    public static int PendingResponses(SafetyCase c) =>
        (c.InformationResponses ?? []).Count(r => r.Mapping is null || r.Review is null);
}
