using Cohors.Domain;

namespace Cohors.Application;

public sealed record AgentEnvelope(string InputHash, string ProductId, AgentInput Input);
public sealed record AgentResultEnvelope(string InputHash, AgentOutput Output);

public static class AgentBoundary
{
    public const int MaximumNarrativeLength = 200000;

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
        var products = input.Snapshot is { } snapshot ? PsurGroupRules.Products(snapshot) : [product];
        Guard.Against(input.Snapshot is { Group: not null } grouped && PsurGroupRules.Check(grouped).Length != 0,
            "agent_group_integrity", "Gruppierter Agentenkontext ist nicht konsistent gebunden.", 403);
        var contextEvidence = input.Case?.Evidence ?? (input.Snapshot is null ? [] : PsurGroupRules.Documents(input.Snapshot));
        Guard.Against(input.Evidence is null || input.Evidence.Length > 100 ||
            input.Evidence.Concat(contextEvidence).Any(e => !e.Approved || !products.Contains(e.ProductId)),
            "retrieval_scope", "Nicht freigegebener oder zu grosser Evidenzkontext.", 403);
        foreach (var evidence in input.Evidence!.Concat(contextEvidence)) EvidenceRules.RequireIntegrity(evidence);
        Guard.Text(input.Instruction, "Agentenauftrag", 1, 4000);
        return product;
    }

    public static void RequireProducts(AgentInput input, string[] allowed)
    {
        var lead = Validate(input);
        var products = input.Snapshot is null ? [lead] : PsurGroupRules.Products(input.Snapshot);
        Guard.Against(products.Any(p => !allowed.Contains(p)), "agent_product",
            "Der Runtime-Scope muss alle eingefrorenen Gruppenmitglieder erlauben.", 403);
    }

    public static void ValidateNarrative(string? text, string field, int minimumLength)
    {
        Guard.Against(text is { Length: > MaximumNarrativeLength }, "invalid_field",
            $"{field}: hoechstens {MaximumNarrativeLength} Zeichen einschliesslich Leerraum; kein automatisches Abschneiden.");
        Guard.Text(text, field, minimumLength, MaximumNarrativeLength);
    }

    public static void ValidateResult(AgentInput input, AgentOutput output)
    {
        ValidateNarrative(output.Narrative, "Agentenentwurf", 1);
        Guard.Against(output.Findings is null || output.Tools is null || output.Findings.Length > 100 ||
            output.Findings.Any(f => f.Severity is not ("Critical" or "Major" or "Minor")),
            "agent_output", "Qualitaetsbefunde sind strukturell ungueltig.");
        Guard.Against(input.Role == AgentRole.CausalityRisk && output.Assessment is null,
            "agent_output", "Tragender Assessment-Entwurf fehlt.");
    }
}
