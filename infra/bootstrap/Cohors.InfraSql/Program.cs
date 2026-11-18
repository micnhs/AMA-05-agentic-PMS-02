using Azure.Core;
using Azure.Identity;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args is ["--describe"])
{
    var assembly = typeof(SqlStateStore).Assembly;
    var resources = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var moduleDefinitions = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)))
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The compiled migration resource is unavailable.");
        resources.Add(name, Convert.ToHexStringLower(SHA256.HashData(stream)));
        var module = name.EndsWith(".003a_operations_quote.sql", StringComparison.Ordinal) ? "dbo.CohorsOperationsQuote"
            : name.EndsWith(".003b_operations_write.sql", StringComparison.Ordinal) ? "dbo.CohorsOperationsWrite" : null;
        if (module is not null)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            moduleDefinitions.Add(module, Convert.ToHexStringLower(SHA256.HashData(
                Encoding.Unicode.GetBytes(await reader.ReadToEndAsync()))));
        }
    }
    using var binary = File.OpenRead(assembly.Location);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        assemblySha256 = Convert.ToHexStringLower(SHA256.HashData(binary)),
        schemaVersion = SqlStateStore.SchemaVersion,
        hasRestrictedPrincipalGuard = typeof(SqlStateStore).GetMethod("RequireOperationsReady") is not null,
        migrationResources = resources,
        moduleDefinitionSha256 = moduleDefinitions
    }));
    return;
}

if (args.Length != 3 || !Guid.TryParse(args[2], out _))
    throw new ArgumentException("Expected private SQL server, database and bound bootstrap MI client ID.");
var value = Environment.GetEnvironmentVariable("ConnectionStrings__Cohors")
    ?? throw new InvalidOperationException("Private bootstrap connection string is required.");
var connectionString = new SqlConnectionStringBuilder(value);
if (connectionString.DataSource != $"tcp:{args[0]},1433" || connectionString.InitialCatalog != args[1] ||
    connectionString.UserID.Length != 0 || connectionString.Password.Length != 0 ||
    connectionString.IntegratedSecurity || connectionString.TrustServerCertificate ||
    connectionString.Authentication != SqlAuthenticationMethod.NotSpecified ||
    connectionString.Encrypt == SqlConnectionEncryptOption.Optional)
    throw new InvalidOperationException("SQL target or keyless encrypted connection contract differs.");
var statement = await Console.In.ReadToEndAsync();
if (statement.Length is 0 or > 65536)
    throw new InvalidOperationException("A bounded, source-generated identity/permission statement is required.");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(args[2]));
var token = await credential.GetTokenAsync(new TokenRequestContext(["https://database.windows.net/.default"]), timeout.Token);
await using var connection = new SqlConnection(connectionString.ConnectionString) { AccessToken = token.Token };
await connection.OpenAsync(timeout.Token);
await using (var context = new SqlCommand("SELECT DB_NAME()", connection))
{
    if ((string?)await context.ExecuteScalarAsync(timeout.Token) != args[1])
        throw new InvalidOperationException("Actual SQL database differs from its owned target.");
}
await using var command = new SqlCommand(statement, connection) { CommandTimeout = 120 };
await using var result = await command.ExecuteReaderAsync(timeout.Token);
if (result.FieldCount != 1 || result.GetName(0) != "cohorsPermissionReceipt" ||
    !await result.ReadAsync(timeout.Token))
    throw new InvalidOperationException("The permission operation did not return its exact metadata receipt.");
var receipt = result.GetString(0);
if (await result.ReadAsync(timeout.Token) || await result.NextResultAsync(timeout.Token))
    throw new InvalidOperationException("Unexpected row data or extra result sets in permission operation.");
using var document = JsonDocument.Parse(receipt);
if (document.RootElement.GetProperty("contract").GetString() != "cohors-sql-permission-readback-1")
    throw new InvalidOperationException("Unsupported permission metadata receipt.");
Console.WriteLine(receipt);
