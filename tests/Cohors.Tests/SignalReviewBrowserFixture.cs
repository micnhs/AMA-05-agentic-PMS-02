using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

public static class SignalReviewBrowserFixture
{
    public static async Task<PublicSourceRun> Create(string connectionString, string policiesDirectory)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (connection.DataSource != "127.0.0.1,14338" ||
            !System.Text.RegularExpressions.Regex.IsMatch(connection.InitialCatalog, "^Cohors_Test_[0-9a-f]{32}$"))
            throw new InvalidOperationException("The signal browser fixture may only write to its explicitly owned private test database.");
        var store = new SqlStateStore(connectionString);
        await store.RequireReady();
        var policies = DemoBaseline.LoadPolicies(policiesDirectory);
        var service = new PublicSourceService(store, new UnusedObjects(), new SyntheticReader(), TimeProvider.System, policies);
        var actor = new Actor("synthetic-browser-fixture", "Synthetic browser fixture", [Roles.Pms, Roles.Quality], ["ortho"], true);
        var run = await service.Read("pubmed", new("ortho"), actor, Guid.NewGuid().ToString("N"), CancellationToken.None);
        var unknown = run.Items.Single(i => i.ExternalId == "synthetic-ui-unknown-history");
        return await store.Execute<PublicSourceRun>(new(run.Id, "source", "ortho", run.Version, Guid.NewGuid().ToString("N"),
            "SyntheticBrowserUnknownHistory", "Expliziter synthetischer Browserbeleg fuer unbekannte historische Ablehnungsherkunft.",
            actor, new { run.Id }, DateTimeOffset.UtcNow),
            current => current! with { Version = current.Version + 1,
                SignalReviews = [new(unknown.ExternalId, unknown.ProvenanceHash, "SIG-SYNTHETIC-UNKNOWN", "Rejected", [])] });
    }

    private sealed class SyntheticReader : IPublicSourceReader
    {
        public Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct = default) =>
            Task.FromResult(new[] { "medical", "regulatory", "unknown-history" }.Select(role =>
            {
                var text = "SYNTHETISCHER BROWSERBELEG: Vergleichsmetadaten ohne Herstellerfall, Ereigniskenntnis oder KI-Analyse. Kein echter Publikationsabruf.";
                return new PublicSourceItem("synthetic-ui-" + role, "SYNTHETISCHER Quellenreview " + role,
                    "https://example.invalid/synthetic-source/" + role, null, text, Canonical.Hash(new { role, text }));
            }).ToArray());
    }

    private sealed class UnusedObjects : IObjectStore
    {
        public Task<string> Put(string content, CancellationToken ct = default) => throw new InvalidOperationException("This metadata-only fixture must not create objects.");
        public Task<string> Read(string hash, CancellationToken ct = default) => throw new InvalidOperationException("This metadata-only fixture must not read objects.");
        public Task<string> PutBytes(ReadOnlyMemory<byte> content, CancellationToken ct = default) => throw new InvalidOperationException("This metadata-only fixture must not create objects.");
        public Task<byte[]> ReadBytes(string hash, CancellationToken ct = default) => throw new InvalidOperationException("This metadata-only fixture must not read objects.");
    }
}
