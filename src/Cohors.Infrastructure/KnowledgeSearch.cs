using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class KnowledgeSearch(IStateStore store, IObjectStore objects, SqlStateStore sql,
    SearchClient? azureSearch = null) : IKnowledgeSearch
{
    public async Task<Evidence[]> Retrieve(string productId, DateTimeOffset effectiveAt, string query, Actor actor, CancellationToken ct = default)
    {
        actor.RequireProduct(productId);
        var authoritative = EvidenceRules.Effective(await store.History<Evidence>("evidence", [productId], ct), effectiveAt);
        if (azureSearch is not null)
        {
            var options = new SearchOptions
            {
                Filter = SearchFilter.Create($"productId eq {productId} and approved eq true and validFrom le {effectiveAt}"),
                Size = 20
            };
            options.Select.Add("id");
            var response = await azureSearch.SearchAsync<SearchDocument>(query, options, ct);
            var ids = new HashSet<string>();
            await foreach (var item in response.Value.GetResultsAsync().WithCancellation(ct))
                ids.Add((string)item.Document["id"]);
            authoritative = authoritative.Where(e => ids.Contains(e.Id + "-revision-" + e.Version)).ToArray();
        }
        foreach (var source in authoritative)
        {
            EvidenceRules.RequireIntegrity(source);
            await objects.Read(source.Hash, ct);
        }
        return authoritative;
    }

    public async Task<int> Rebuild(CancellationToken ct = default)
    {
        var documents = await store.History<Evidence>("evidence", ["ortho", "cardio"], ct);
        await using var connection = await sql.Open(ct);
        foreach (var e in documents.Where(e => e.Approved))
        {
            EvidenceRules.RequireIntegrity(e);
            await objects.Read(e.Hash, ct);
            using var command = SqlStateStore.Command(connection, null, """
                UPDATE dbo.SearchProjection SET ProductId=@product,Version=@version,Content=@content,IndexedAt=SYSUTCDATETIME(),Hash=@hash WHERE Id=@id;
                IF @@ROWCOUNT=0 INSERT dbo.SearchProjection VALUES(@id,@product,@version,@content,SYSUTCDATETIME(),@hash);
                """, ("id", e.Id + "-revision-" + e.Version), ("product", e.ProductId), ("version", e.Version), ("content", e.Content), ("hash", e.Hash));
            await command.ExecuteNonQueryAsync(ct);
        }
        if (azureSearch is not null)
        {
            var projection = documents.Select(e => new SearchDocument
            {
                ["id"] = e.Id + "-revision-" + e.Version, ["productId"] = e.ProductId, ["version"] = e.Version, ["approved"] = e.Approved,
                ["validFrom"] = e.ValidFrom, ["validTo"] = e.ValidTo, ["content"] = e.Content, ["hash"] = e.Hash
            });
            var response = await azureSearch.IndexDocumentsAsync(IndexDocumentsBatch.MergeOrUpload(projection), cancellationToken: ct);
            Guard.Against(response.Value.Results.Any(r => !r.Succeeded), "search_index_failed", "Mindestens eine Search-Projektion ist fehlgeschlagen.");
        }
        return documents.Length;
    }
}
