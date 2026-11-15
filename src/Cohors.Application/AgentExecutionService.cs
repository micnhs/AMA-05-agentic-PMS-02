using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Application;

public sealed class AgentExecutionService(IStateStore store, IAgentRunner runner, TimeProvider clock)
{
    public async Task<AgentOutput> Run(string invocationId, string productId, AgentInput input, CancellationToken ct = default)
    {
        var id = "agent-" + Canonical.HashText(invocationId)[..32];
        Guard.Against(AgentBoundary.Validate(input) != productId, "agent_product", "Auftrag und Produktscope stimmen nicht ueberein.", 403);
        var inputJson = Canonical.Serialize(input);
        var inputHash = Canonical.HashText(inputJson);
        var owner = Guid.NewGuid().ToString("N");
        var context = new CommandContext(id, "agentInvocation", productId, 0, "claim-" + id,
            "AgentInvocationClaimed", "Einmaliger persistenter Modellauftrag; keine Domaenen- oder Freigabetools.",
            CaseService.Workflow, new { inputHash }, clock.GetUtcNow());
        var existing = await store.Get<AgentInvocation>("agentInvocation", id, ct);
        if (existing is not null)
        {
            Guard.Against(existing.InputHash != inputHash, "invocation_conflict", "Auftrags-ID bindet einen anderen unveraenderlichen Modelleingang.", 409);
            if (existing.Status == "Completed") return Canonical.Read<AgentOutput>(existing.OutputJson!);
            throw new DomainException("invocation_unresolved",
                "Modellauftrag ist aktiv, fehlgeschlagen oder unklar. Kein verdeckter erneuter Aufruf; Recovery ausdruecklich pruefen.", 409);
        }
        var claimed = await store.Execute<AgentInvocation>(context, _ =>
            new(id, 1, productId, inputHash, owner, "Running", clock.GetUtcNow(), null, null, null, inputJson), ct);
        Guard.Against(claimed.OwnerId != owner, "invocation_in_progress",
            "Eine andere Activity besitzt diesen Modellauftrag; kein zweiter Aufruf.", 409);
        try
        {
            var frozenInput = Canonical.Read<AgentInput>(claimed.InputJson!);
            var output = await runner.Run(frozenInput, ct);
            AgentBoundary.ValidateResult(frozenInput, output);
            await store.Execute<AgentInvocation>(context with { ExpectedVersion = 1, IdempotencyKey = "result-" + id,
                Action = "AgentInvocationCompleted", Request = new { inputHash, output }, At = clock.GetUtcNow() }, current =>
                current! with { Version = 2, Status = "Completed", FinishedAt = clock.GetUtcNow(), OutputJson = Canonical.Serialize(output) }, ct);
            return output;
        }
        catch (Exception ex) when (ex is DomainException or AgentExecutionException or HttpRequestException or
            TimeoutException or OperationCanceledException or JsonException)
        {
            var latest = await store.Get<AgentInvocation>("agentInvocation", id, CancellationToken.None);
            Guard.Against(latest?.Status == "Abandoned", "invocation_abandoned",
                "Menschlich abgebrochener Modellauftrag; spaetes Ergebnis wird nicht angenommen.", 409);
            var code = ex is DomainException domain ? domain.Code
                : ex is AgentExecutionException agent ? agent.Code : ex.GetType().Name;
            await store.Execute<AgentInvocation>(context with { ExpectedVersion = 1, IdempotencyKey = "failure-" + id,
                Action = "AgentInvocationFailed", Request = new { code }, At = clock.GetUtcNow() }, current =>
                current! with { Version = 2, Status = "Failed", FinishedAt = clock.GetUtcNow(), ErrorCode = code }, CancellationToken.None);
            throw;
        }
    }
}
