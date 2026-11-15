using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record RemoteAgentOptions(Uri Endpoint, string Scope, string? LocalKey);

public sealed class RemoteAgentRunner(HttpClient client, RemoteAgentOptions options, TokenCredential credential) : IAgentRunner
{
    public async Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default)
    {
        var product = AgentBoundary.Validate(input);
        var hash = Canonical.Hash(input);
        var envelope = new AgentEnvelope(hash, product, input);
        var token = options.LocalKey ?? (await credential.GetTokenAsync(new TokenRequestContext([options.Scope]), ct)).Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint, "/internal/run"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(envelope, options: Canonical.Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new AgentExecutionException("agent_runtime_rejected",
                new HttpRequestException("Internal agent runtime did not confirm a result.", null, response.StatusCode));
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
        var result = await response.Content.ReadFromJsonAsync<AgentResultEnvelope>(Canonical.Json, ct)
            ?? throw new DomainException("agent_output", "Agentenruntime lieferte kein Ergebnis.");
        Guard.Against(result.InputHash != hash, "agent_snapshot_mismatch", "Agentenergebnis gehoert nicht zum eingefrorenen Input.", 409);
        AgentBoundary.ValidateResult(input, result.Output);
        return result.Output;
    }
}
