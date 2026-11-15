using Cohors.Domain;

namespace Cohors.Application;

public sealed record AgentEnvelope(string InputHash, string ProductId, AgentInput Input);
public sealed record AgentResultEnvelope(string InputHash, AgentOutput Output);

public static class AgentBoundary
{
    public static string Validate(AgentInput input)
    {
        Guard.Against(!Enum.IsDefined(input.Role) || !Enum.IsDefined(input.Mode) || input.Revision is < 0 or > 2,
            "agent_contract", "Agentenrolle, Modus oder Recherchebudget ungueltig.");
        Guard.Against(new object?[] { input.Case, input.Trend, input.Snapshot }.Count(x => x is not null) != 1,
            "agent_context", "Genau ein gebundener Fall-/Trend-/PSUR-Kontext ist erforderlich.");
        var product = input.Case?.ProductId ?? input.Trend?.ProductId ?? input.Snapshot!.ProductId;
        Guard.Against(input.Case is { Synthetic: false }, "synthetic_only", "Nur synthetische Agentenkontexte.");
        Guard.Against(input.Role == AgentRole.TrendSignal ? input.Trend is null :
            input.Role == AgentRole.PsurAuthoring ? input.Snapshot is null : input.Case is null,
            "agent_context", "Die Rolle passt nicht zum Auftragskontext.");
        var contextEvidence = input.Case?.Evidence ?? input.Snapshot?.Documents ?? [];
        Guard.Against(input.Evidence is null || input.Evidence.Length > 100 ||
            input.Evidence.Concat(contextEvidence).Any(e => !e.Approved || e.ProductId != product),
            "retrieval_scope", "Nicht freigegebener oder zu grosser Evidenzkontext.", 403);
        foreach (var evidence in input.Evidence!.Concat(contextEvidence)) EvidenceRules.RequireIntegrity(evidence);
        Guard.Text(input.Instruction, "Agentenauftrag", 1, 4000);
        return product;
    }

    public static void ValidateResult(AgentInput input, AgentOutput output)
    {
        Guard.Text(output.Narrative, "Agentenentwurf", 1, 200000);
        Guard.Against(output.Findings is null || output.Tools is null || output.Findings.Length > 100 ||
            output.Findings.Any(f => f.Severity is not ("Critical" or "Major" or "Minor")),
            "agent_output", "Qualitaetsbefunde sind strukturell ungueltig.");
        Guard.Against(input.Role == AgentRole.CausalityRisk && output.Assessment is null,
            "agent_output", "Tragender Assessment-Entwurf fehlt.");
    }
}
