using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Bootstrap;

internal sealed record SqlSchemaState(string Database, bool HasVersionTable, bool HasUserObjects, int[] Versions);

internal interface ISqlSchemaStore
{
    Task<SqlSchemaState> Inspect(CancellationToken ct);
    Task Initialize(CancellationToken ct);
    Task RequireReady(CancellationToken ct);
}

internal sealed class SqlSchemaStore(SqlStateStore store) : ISqlSchemaStore
{
    public async Task<SqlSchemaState> Inspect(CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        using var metadata = new SqlCommand("""
            SELECT DB_NAME(),
                COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0),
                CAST(CASE WHEN OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL THEN 0 ELSE 1 END AS bit),
                CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped=0) THEN 1 ELSE 0 END AS bit);
            """, connection);
        string database;
        bool hasVersionTable, hasUserObjects;
        await using (var reader = await metadata.ExecuteReaderAsync(ct))
        {
            BootstrapRequest.Require(await reader.ReadAsync(ct), "sql_metadata_missing");
            database = reader.GetString(0);
            BootstrapRequest.Require(reader.GetInt32(1) == 1, "sql_metadata_permission_required");
            hasVersionTable = reader.GetBoolean(2);
            hasUserObjects = reader.GetBoolean(3);
        }
        var versions = new List<int>();
        if (hasVersionTable)
        {
            using var query = new SqlCommand("SELECT Version FROM dbo.SchemaVersions ORDER BY Version", connection);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                versions.Add(reader.GetInt32(0));
                BootstrapRequest.Require(versions.Count <= SqlStateStore.SchemaVersion, "sql_schema_unrecognized");
            }
        }
        return new(database, hasVersionTable, hasUserObjects, versions.ToArray());
    }

    public Task Initialize(CancellationToken ct) => store.Initialize(ct);
    public Task RequireReady(CancellationToken ct) => store.RequireReady(ct);
}

internal static class SqlBootstrap
{
    internal static async Task<string> Run(ISqlSchemaStore store, string expectedDatabase, bool apply, CancellationToken ct)
    {
        var state = await store.Inspect(ct);
        BootstrapRequest.Require(state.Database == expectedDatabase, "sql_database_context_mismatch");
        if (!state.HasVersionTable)
            BootstrapRequest.Require(!state.HasUserObjects && state.Versions.Length == 0, "sql_unversioned_database_not_empty");
        else
            BootstrapRequest.Require(state.Versions.Length > 0 && state.Versions.Length <= SqlStateStore.SchemaVersion &&
                state.Versions.SequenceEqual(Enumerable.Range(1, state.Versions.Length)), "sql_schema_unrecognized");

        var needsMigration = state.Versions.Length < SqlStateStore.SchemaVersion;
        BootstrapRequest.Require(apply || !needsMigration, "sql_schema_initialization_required");
        if (needsMigration) await store.Initialize(ct);
        await store.RequireReady(ct);
        return needsMigration ? "sql_schema_initialized_and_verified" : "sql_schema_version_verified";
    }
}
