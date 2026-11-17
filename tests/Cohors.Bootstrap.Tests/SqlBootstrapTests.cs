using Cohors.Bootstrap;
using Cohors.Infrastructure;

namespace Cohors.Bootstrap.Tests;

public sealed class SqlBootstrapTests
{
    [Fact]
    public async Task MissingSchemaNeedsExplicitApplyAndReadySchemaIsNeverReinitialized()
    {
        var store = new FakeSql(new("cohors", false, false, []));
        await Assert.ThrowsAsync<BootstrapException>(() => SqlBootstrap.Run(store, "cohors", false, default));
        Assert.Equal(0, store.Initializations);
        Assert.Equal(0, store.ReadinessChecks);
        await SqlBootstrap.Run(store, "cohors", true, default);
        Assert.Equal(1, store.Initializations);
        Assert.Equal(1, store.ReadinessChecks);

        var ready = new FakeSql(new("cohors", true, true, Enumerable.Range(1, SqlStateStore.SchemaVersion).ToArray()));
        await SqlBootstrap.Run(ready, "cohors", false, default);
        await SqlBootstrap.Run(ready, "cohors", true, default);
        Assert.Equal(0, ready.Initializations);
        Assert.Equal(2, ready.ReadinessChecks);
    }

    [Fact]
    public async Task WrongDatabaseAndUnrecognizedOrPartialSchemasAreNotMigrated()
    {
        SqlSchemaState[] rejected =
        [
            new("other", false, false, []),
            new("cohors", false, true, []),
            new("cohors", true, true, []),
            new("cohors", true, true, [2]),
            new("cohors", true, true, Enumerable.Range(1, SqlStateStore.SchemaVersion + 1).ToArray())
        ];
        foreach (var state in rejected)
        {
            var store = new FakeSql(state);
            await Assert.ThrowsAsync<BootstrapException>(() => SqlBootstrap.Run(store, "cohors", true, default));
            Assert.Equal(0, store.Initializations);
            Assert.Equal(0, store.ReadinessChecks);
        }
    }

    [Fact]
    public async Task MigrationAndPostMigrationReadinessFailuresPropagate()
    {
        var failure = new InvalidOperationException("synthetic migration failure");
        var store = new FakeSql(new("cohors", true, true, [1])) { MigrationFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => SqlBootstrap.Run(store, "cohors", true, default)));
        Assert.Equal(1, store.Initializations);
        Assert.Equal(0, store.ReadinessChecks);

        store = new(new("cohors", true, true, [1])) { ReadinessFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => SqlBootstrap.Run(store, "cohors", true, default)));
        Assert.Equal(1, store.Initializations);
        Assert.Equal(1, store.ReadinessChecks);
    }

    private sealed class FakeSql(SqlSchemaState state) : ISqlSchemaStore
    {
        internal int Initializations { get; private set; }
        internal int ReadinessChecks { get; private set; }
        internal Exception? MigrationFailure { get; init; }
        internal Exception? ReadinessFailure { get; init; }
        public Task<SqlSchemaState> Inspect(CancellationToken ct) => Task.FromResult(state);
        public Task Initialize(CancellationToken ct)
        {
            Initializations++;
            return MigrationFailure is null ? Task.CompletedTask : Task.FromException(MigrationFailure);
        }
        public Task RequireReady(CancellationToken ct)
        {
            ReadinessChecks++;
            return ReadinessFailure is null ? Task.CompletedTask : Task.FromException(ReadinessFailure);
        }
    }
}
