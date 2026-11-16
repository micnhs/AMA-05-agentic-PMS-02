using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Bootstrap;

internal abstract record BootstrapRequest(bool Apply)
{
    internal sealed record Sql(bool Apply, SqlStateStore Store, string Database) : BootstrapRequest(Apply);
    internal sealed record Search(bool Apply, Uri Endpoint, string Index) : BootstrapRequest(Apply);

    internal static BootstrapRequest Parse(string[] args, Func<string, string?> environment)
    {
        Require(args.Length > 0 && args[0] is "sql" or "search", "usage");
        var sql = args[0] == "sql";
        string[] names = sql ? ["--expect-server", "--expect-database"] : ["--expect-endpoint", "--expect-index"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--apply")
            {
                Require(!apply, "duplicate_apply");
                apply = true;
                continue;
            }
            Require(names.Contains(args[i]) && i + 1 < args.Length, "usage");
            var name = args[i++];
            Require(!args[i].StartsWith("--", StringComparison.Ordinal) && values.TryAdd(name, args[i]), "usage");
        }
        Require(names.All(values.ContainsKey), "expected_target_required");

        if (sql)
        {
            var expectedServer = values["--expect-server"];
            var expectedDatabase = values["--expect-database"];
            Require(Regex.IsMatch(expectedServer, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.database\.windows\.net\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "sql_server_invalid");
            Require(Regex.IsMatch(expectedDatabase, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z") &&
                !new[] { "master", "model", "msdb", "tempdb" }.Contains(expectedDatabase, StringComparer.OrdinalIgnoreCase),
                "sql_database_invalid");
            var authentication = environment("Cohors__SqlAuthentication");
            Require(authentication is null or "ManagedIdentity", "sql_identity_required");
            var connection = Required(environment("ConnectionStrings__Cohors"));
            var settings = new SqlConnectionStringBuilder(connection);
            var server = settings.DataSource;
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
            if (server.EndsWith(",1433", StringComparison.Ordinal)) server = server[..^5];
            Require(string.Equals(server, expectedServer, StringComparison.OrdinalIgnoreCase) &&
                settings.InitialCatalog == expectedDatabase, "sql_target_mismatch");
            Require(settings.FailoverPartner.Length == 0 && settings.AttachDBFilename.Length == 0 &&
                !settings.UserInstance && settings.HostNameInCertificate.Length == 0 &&
                settings.ServerCertificate.Length == 0 && settings.ApplicationIntent == ApplicationIntent.ReadWrite,
                "sql_target_override_forbidden");
            return new Sql(apply, SqlStateStore.Configure(connection, "ManagedIdentity", localAllowed: false), expectedDatabase);
        }

        var endpoint = SearchEndpoint(Required(environment("Search__Endpoint")));
        var expectedEndpoint = SearchEndpoint(values["--expect-endpoint"]);
        var index = Required(environment("Search__Index"));
        Require(Regex.IsMatch(index, @"\A[a-z0-9][a-z0-9_-]{1,127}\z") &&
            !index.Contains("--", StringComparison.Ordinal) && !index.Contains("__", StringComparison.Ordinal),
            "search_index_invalid");
        Require(endpoint == expectedEndpoint && index == values["--expect-index"], "search_target_mismatch");
        return new Search(apply, endpoint, index);
    }

    private static Uri SearchEndpoint(string value)
    {
        Require(Regex.IsMatch(value, @"\Ahttps://[a-z0-9]{2}(?:[a-z0-9-]{0,57}[a-z0-9])?\.search\.windows\.net/?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !value.Contains("--", StringComparison.Ordinal),
            "search_endpoint_invalid");
        return new Uri(value, UriKind.Absolute);
    }

    private static string Required(string? value)
    {
        Require(!string.IsNullOrWhiteSpace(value), "configuration_missing");
        return value;
    }

    internal static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new BootstrapException(code);
    }
}

internal sealed class BootstrapException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
