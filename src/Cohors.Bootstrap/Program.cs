using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Bootstrap;

internal static class Program
{
    internal const string Usage = """
        Cohors.Bootstrap sql --expect-server <server.database.windows.net> --expect-database <database> [--apply]
        Cohors.Bootstrap search --expect-endpoint <https://service.search.windows.net> --expect-index <index> [--apply]
        Without --apply: remote read-only checks, not an offline deployment simulation.
        Environment only: ConnectionStrings__Cohors, Cohors__SqlAuthentication=ManagedIdentity, Search__Endpoint, Search__Index.
        Existing Azure resources, private connectivity and a separately authorized bootstrap identity are prerequisites.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            return await Run(args, Environment.GetEnvironmentVariable, Execute, Console.Out, Console.Error, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> Run(string[] args, Func<string, string?> environment,
        Func<BootstrapRequest, CancellationToken, Task<string>> execute, TextWriter output, TextWriter errors,
        CancellationToken ct = default)
    {
        try
        {
            var request = BootstrapRequest.Parse(args, environment);
            var result = await execute(request, ct);
            await output.WriteLineAsync($"{result}; mode={(request.Apply ? "apply" : "read-only")}");
            return 0;
        }
        catch (BootstrapException error) { await errors.WriteLineAsync($"bootstrap_refused: {error.Code}. See --help and docs/azure-bootstrap.md."); return 2; }
        catch (SqlException error) { await errors.WriteLineAsync($"bootstrap_failed: sql_error={error.Number}; no success confirmed."); return 1; }
        catch (RequestFailedException error) { await errors.WriteLineAsync($"bootstrap_failed: search_http_status={error.Status}; no success confirmed."); return 1; }
        catch (AuthenticationFailedException) { await errors.WriteLineAsync("bootstrap_failed: identity_authentication_failed; no success confirmed."); return 1; }
        catch (DomainException) { await errors.WriteLineAsync("bootstrap_failed: sql_schema_not_ready; no success confirmed."); return 1; }
        catch (OperationCanceledException) { await errors.WriteLineAsync("bootstrap_cancelled: writes may already have completed; no success confirmed."); return 130; }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or
            HttpRequestException or IOException or AggregateException or System.Text.Json.JsonException)
        {
            // SDK/configuration messages may contain credentials, SQL or HTTP payloads.
            await errors.WriteLineAsync($"bootstrap_failed: configuration_or_transport_error={error.GetType().Name}; no success confirmed.");
            return 1;
        }
    }

    private static Task<string> Execute(BootstrapRequest request, CancellationToken ct) => request switch
    {
        BootstrapRequest.Sql sql => SqlBootstrap.Run(new SqlSchemaStore(sql.Store), sql.Database, sql.Apply, ct),
        BootstrapRequest.Search search => SearchBootstrap.Run(CreateSearchClient(search.Endpoint), search.Index, search.Apply, ct),
        _ => throw new BootstrapException("command_invalid")
    };

    private static SearchIndexClient CreateSearchClient(Uri endpoint)
    {
        var options = new SearchClientOptions();
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        return new SearchIndexClient(endpoint, new DefaultAzureCredential(), options);
    }
}
