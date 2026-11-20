using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Cohors.Agents;

public interface IFoundryClientFactory
{
    TokenCredential CreateCredential(FoundryTaskBinding binding);
    IFoundryDraftClient CreateClient(Uri endpoint, FoundryTaskBinding binding, TokenCredential credential, string instructions);
}

public interface IFoundryDraftClient
{
    Task<FoundryDraftResponse> Run(string prompt, CancellationToken ct);
}

public sealed record FoundryDraftResponse(string Text, AgentModelObservation? Observation)
{
    public static FoundryDraftResponse FromAgentResponse(AgentResponse response)
    {
        // AsChatResponse can synthesize a wrapper; only the original SDK chain is evidence.
        if (response.RawRepresentation is not ChatResponse chat || chat.RawRepresentation is not ChatCompletion completion)
            throw new DomainException("model_response_identity_missing",
                "Die originale Provider-Antwort fehlt; keine Modellidentitaet aus Wrapper, Konfiguration oder Modelltext.", 409);
        Guard.Against(string.IsNullOrWhiteSpace(completion.Model) || string.IsNullOrWhiteSpace(completion.Id) ||
            string.IsNullOrWhiteSpace(chat.ModelId) || string.IsNullOrWhiteSpace(chat.ResponseId) ||
            string.IsNullOrWhiteSpace(response.ResponseId),
            "model_response_identity_missing", "Die originale Provider-Antwort enthaelt keine vollstaendige Modell-/Antwortkennung.", 409);
        Guard.Against(chat.ModelId != completion.Model || chat.ResponseId != completion.Id || response.ResponseId != completion.Id ||
            response.Messages.Count != 1 || chat.Messages.Count != 1 || response.Text != chat.Text ||
            chat.Text != string.Concat(completion.Content.Select(part => part.Text)),
            "model_response_identity_ambiguous", "SDK-Antwort und Provideroriginal sind nicht eindeutig derselben Ausgabe zugeordnet.", 409);
        var version = AgentModelIdentity.RequireDatedSnapshotVersion(completion.Model, "model_response_identity_insufficient");
        return new(response.Text, new(AgentModelIdentity.MetadataSource, completion.Id, completion.Model, version));
    }
}

public sealed class FoundryClientFactory : IFoundryClientFactory
{
    public TokenCredential CreateCredential(FoundryTaskBinding binding)
    {
        TokenCredential credential = binding.Authentication?.Kind switch
        {
            "ManagedIdentity" => new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(binding.WorkloadClientId.ToString())),
            "WorkloadIdentity" => new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
            {
                TenantId = binding.WorkloadTenantId.ToString(),
                ClientId = binding.WorkloadClientId.ToString(),
                TokenFilePath = binding.Authentication.FederatedTokenFile,
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                DisableInstanceDiscovery = true
            }),
            _ => throw new DomainException("model_workload_authentication", "Keine freigegebene Workloadauthentifizierung.", 409)
        };
        return new FoundryBoundCredential(credential, binding);
    }

    public IFoundryDraftClient CreateClient(Uri endpoint, FoundryTaskBinding binding, TokenCredential credential, string instructions)
    {
        var agent = new AzureOpenAIClient(endpoint, credential,
                new AzureOpenAIClientOptions { RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(AgentRunner.MaximumRetries) })
            .GetChatClient(binding.Model.Deployment).AsAIAgent(instructions: instructions, name: binding.Role.ToString());
        return new DraftClient(agent);
    }

    private sealed class DraftClient(AIAgent agent) : IFoundryDraftClient
    {
        public async Task<FoundryDraftResponse> Run(string prompt, CancellationToken ct) =>
            FoundryDraftResponse.FromAgentResponse(await agent.RunAsync(prompt, cancellationToken: ct));
    }
}

public sealed class FoundryBoundCredential(TokenCredential credential, FoundryTaskBinding binding) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequireScope(requestContext);
        return RequireIdentity(credential.GetToken(requestContext, cancellationToken));
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequireScope(requestContext);
        return RequireIdentity(await credential.GetTokenAsync(requestContext, cancellationToken));
    }

    private static void RequireScope(TokenRequestContext request) =>
        Guard.Against(request.Scopes is not ["https://cognitiveservices.azure.com/.default"],
            "model_workload_scope", "Die Workloadcredential ist ausschliesslich an den Modellscope gebunden.", 403);

    private AccessToken RequireIdentity(AccessToken token)
    {
        try
        {
            var parts = token.Token.Split('.');
            Guard.Against(parts.Length != 3, "model_workload_token", "Die Modellcredential lieferte keinen pruefbaren Workloadtoken.", 403);
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            string? Text(string name) => json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            bool Id(string? value, Guid expected) => Guid.TryParse(value, out var actual) && actual == expected;
            Guard.Against(!Id(Text("tid"), binding.WorkloadTenantId) || !Id(Text("oid"), binding.WorkloadPrincipalId) ||
                !Id(Text("azp") ?? Text("appid"), binding.WorkloadClientId) ||
                Text("azp") is { } azp && !Id(azp, binding.WorkloadClientId) ||
                Text("appid") is { } appid && !Id(appid, binding.WorkloadClientId) ||
                json.RootElement.TryGetProperty("scp", out _) ||
                Text("idtyp") is { } idtyp && idtyp != "app" ||
                Text("aud") is not ("https://cognitiveservices.azure.com" or "https://cognitiveservices.azure.com/"),
                "model_workload_identity", "Der Token gehoert nicht zur geprueften app-only Rollenidentitaet.", 403);
            return token;
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
        {
            throw new DomainException("model_workload_token", "Die Modellcredential lieferte keinen pruefbaren Workloadtoken.", 403);
        }
    }
}
