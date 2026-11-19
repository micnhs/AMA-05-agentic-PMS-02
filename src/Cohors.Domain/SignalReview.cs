using System.Text.Json;

namespace Cohors.Domain;

public sealed record SignalDecision(string Action, string Origin, string Reason, DateTimeOffset At,
    string ActorId, string ActorName, string Role, int OwnerVersion, string SourceHash,
    string? AgentRunId = null, string? AgentMode = null, string? Model = null, string? PromptVersion = null);

public sealed record SignalReview(string SourceId, string SourceHash, string ReviewReference,
    string Status, SignalDecision[] History);

public sealed record SignalTriageProposal(string Relevance, string[] SourceIds, string Reason);

public static class SignalReviewRules
{
    public const string Rejected = "Rejected";
    public const string ReviewRequired = "ReviewRequired";
    public const string Relevant = "Relevant";
    public const string Boundary = "Signaltriage ist keine Nichtmeldepflichtentscheidung, Meldungsrueckgabe oder Simulatorantwort. " +
        "Originalquellen, Kenntnis und Fristen bleiben erhalten; normale medizinische und regulatorische Freigaben bleiben erforderlich.";

    private static string Reference(string ownerKind, string ownerId, string sourceId) =>
        "SIG-" + Canonical.Hash(new { ownerKind, ownerId, sourceId })[..20];

    public static SignalReview Pending(string ownerKind, string ownerId, string sourceId, string sourceHash) =>
        new(sourceId, sourceHash, Reference(ownerKind, ownerId, sourceId), ReviewRequired, []);

    public static SignalReview? Find(SignalReview[]? values, string sourceId, string sourceHash)
    {
        var matches = (values ?? []).Where(v => v.SourceId == sourceId).ToArray();
        Guard.Against(matches.Length > 1 || matches.Any(v => v.SourceHash != sourceHash),
            "signal_binding", "Signalentscheidung passt nicht eindeutig zum Originalquellen-Hash.", 409);
        return matches.SingleOrDefault();
    }

    public static SignalReview[] Decide(SignalReview[]? values, string ownerKind, string ownerId,
        string sourceId, string sourceHash, SignalDecision decision)
    {
        var previous = Find(values, sourceId, sourceHash);
        Guard.Against(decision.SourceHash != sourceHash, "signal_binding", "Entscheidung muss die Originalquelle binden.", 409);
        Guard.Text(decision.Reason, "Begruendung der Signalentscheidung", 40, 12000);
        var status = decision.Action switch
        {
            "Reject" => Rejected,
            "Reopen" => ReviewRequired,
            "ConfirmRelevance" => Relevant,
            _ => throw new DomainException("signal_action", "Unbekannte Signalentscheidung.")
        };
        Guard.Against(decision.Action == "Reopen" && previous?.Status != Rejected,
            "signal_not_rejected", "Nur ein aktuell abgelehntes Signal darf wiederaufgenommen werden.", 409);
        Guard.Against(decision.Action == "ConfirmRelevance" && previous?.Status != ReviewRequired,
            "signal_not_in_review", "Die Relevanzbestaetigung braucht einen offenen Review-Vorgang.", 409);
        var reference = previous?.ReviewReference ?? Reference(ownerKind, ownerId, sourceId);
        var next = new SignalReview(sourceId, sourceHash, reference, status, [.. previous?.History ?? [], decision]);
        return [.. (values ?? []).Where(v => v.SourceId != sourceId), next];
    }

    public static SignalReview[]? AgentTriage(SafetyCase c, AgentRun run, SignalTriageProposal? proposal, DateTimeOffset at)
    {
        if (proposal is null) return c.SignalReviews;
        Guard.Against(run.Role != AgentRole.Ingestion || run.Status != "Completed" || run.OutputJson is null ||
            run.OutputHash != Canonical.HashText(run.OutputJson), "signal_agent_provenance", "Belegter abgeschlossener Ingestion-Lauf erforderlich.");
        using var output = JsonDocument.Parse(run.OutputJson ?? throw new DomainException("signal_agent_provenance", "Originalausgabe fehlt."));
        Guard.Against(!output.RootElement.TryGetProperty("triage", out var recorded) || Canonical.Hash(recorded) != Canonical.Hash(proposal),
            "signal_agent_provenance", "Triage muss unveraendert in der gebundenen Agentenausgabe stehen.");
        Guard.Against(proposal.Relevance is not ("Relevant" or "Uncertain" or "NotRelevant") ||
            proposal.SourceIds is null || proposal.SourceIds.Length == 0 || proposal.SourceIds.Distinct().Count() != proposal.SourceIds.Length ||
            proposal.SourceIds.Any(id => !c.Sources.Any(s => s.Id == id)),
            "signal_agent_scope", "Agententriage muss konkrete vorhandene Originalquellen binden.");
        Guard.Text(proposal.Reason, "Begruendung der Agententriage", 40, 12000);
        if (proposal.Relevance != "NotRelevant") return c.SignalReviews;
        var sourceIds = proposal.SourceIds ?? throw new DomainException("signal_agent_scope", "Originalquellen fehlen.");
        var values = c.SignalReviews;
        foreach (var source in c.Sources.Where(s => sourceIds.Contains(s.Id)))
        {
            var previous = Find(values, source.Id, source.Hash);
            // An agent proposal cannot undo an explicit human review or reopening.
            if (previous?.Status == ReviewRequired || previous?.History.Any(d => d.Origin == "Reviewer") == true) continue;
            values = Decide(values, "case", c.Id, source.Id, source.Hash,
                new("Reject", "Agent", proposal.Reason, at, "agent:Ingestion", "Ingestion", "Ingestion",
                    c.Version + 1, source.Hash, run.Id, run.Mode.ToString(), run.Model, run.PromptVersion));
        }
        return values;
    }

    public static SafetyCase ResumeCase(SafetyCase c, Evidence rationale, DateTimeOffset at) => c with
    {
        ClosedAt = null,
        Stage = CaseStage.AwaitingMedicalReview,
        Evidence = [.. c.Evidence, rationale],
        Reports = c.Reports.Select(r => r.State is ReportState.Draft or ReportState.MedicalApproved or ReportState.Frozen
            ? r with { State = ReportState.Superseded } : r).ToArray(),
        AgentRuns = c.AgentRuns.Select(r => r.Status is "Queued" or "Running"
            ? r with { Status = "Abandoned", FinishedAt = at, ErrorCode = "signal_reopened_human_review_only" } : r).ToArray(),
        Alerts = [.. c.Alerts, "Signal unmittelbar zur menschlichen Pruefung wiederaufgenommen. " +
            "Keine KI beauftragt, keine Freigabe reaktiviert; fehlende oder ueberholte Bewertung zuerst pruefen. Kein Fristreset."]
    };
}
