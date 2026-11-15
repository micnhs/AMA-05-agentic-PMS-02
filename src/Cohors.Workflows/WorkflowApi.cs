using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using Cohors.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cohors.Workflows;

public sealed record WorkflowOptions(Uri Endpoint, string Scope, string? LocalKey)
{
    public static WorkflowOptions Read(IConfiguration config, IHostEnvironment environment)
    {
        var endpoint = new Uri(config["Cohors:ApiBaseUrl"] ?? throw new InvalidOperationException("Cohors:ApiBaseUrl required."));
        var local = config["Cohors:LocalWorkloadKey"];
        if (local is not null && (!environment.IsDevelopment() || !endpoint.IsLoopback))
            throw new InvalidOperationException("Local workload key is restricted to Development and loopback.");
        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/" || (endpoint.Scheme != "https" && !(local is not null && endpoint.IsLoopback)))
            throw new InvalidOperationException("A fixed HTTPS API origin is required.");
        return new(endpoint, config["Cohors:ApiScope"] ?? (local is not null ? "" :
            throw new InvalidOperationException("Cohors:ApiScope required.")), local);
    }
}

public sealed class WorkflowApi(HttpClient client, WorkflowOptions options)
{
    private readonly DefaultAzureCredential credential = new();

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, CancellationToken ct)
    {
        var token = options.LocalKey ?? (await credential.GetTokenAsync(new TokenRequestContext([options.Scope]), ct)).Token;
        using var request = new HttpRequestMessage(method, new Uri(options.Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("Cohors workload API rejected the operation.", null, status);
        }
        return response;
    }

    public async Task<OutboxDescriptor[]> Pending(CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, "/internal/outbox", ct);
        return await response.Content.ReadFromJsonAsync<OutboxDescriptor[]>(Canonical.Json, ct)
            ?? throw new InvalidDataException("Outbox response missing.");
    }

    public async Task Ready(CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, "/internal/ready", ct);
    }

    public async Task<OutboxDispatchResult> Process(string id, CancellationToken ct)
    {
        Guard.Against(!Guid.TryParseExact(id, "N", out _), "event_id", "Ungueltige Ereignis-ID.");
        using var response = await Send(HttpMethod.Post, "/internal/outbox/" + id + "/process", ct);
        var result = await response.Content.ReadFromJsonAsync<OutboxDispatchResult>(Canonical.Json, ct)
            ?? throw new InvalidDataException("Event result missing.");
        if (result.State != "Handled") throw new InvalidOperationException("Outbox delivery: " + result.State + "/" + result.Error);
        return result;
    }
}
