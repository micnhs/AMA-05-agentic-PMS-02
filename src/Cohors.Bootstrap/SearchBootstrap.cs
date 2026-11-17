using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace Cohors.Bootstrap;

internal static class SearchBootstrap
{
    internal static SearchIndex Definition(string name) => new(name,
    [
        Field("id", SearchFieldDataType.String, key: true),
        Field("productId", SearchFieldDataType.String, filterable: true),
        Field("version", SearchFieldDataType.Int32),
        Field("approved", SearchFieldDataType.Boolean, filterable: true),
        Field("validFrom", SearchFieldDataType.DateTimeOffset, filterable: true),
        Field("validTo", SearchFieldDataType.DateTimeOffset, filterable: true),
        Field("content", SearchFieldDataType.String, searchable: true),
        Field("hash", SearchFieldDataType.String)
    ]);

    private static SearchField Field(string name, SearchFieldDataType type, bool key = false,
        bool filterable = false, bool searchable = false) => new(name, type)
    {
        IsKey = key, IsHidden = false, IsSearchable = searchable, IsFilterable = filterable,
        IsSortable = false, IsFacetable = false,
        AnalyzerName = searchable ? LexicalAnalyzerName.StandardLucene : null
    };

    internal static void RequireCompatible(SearchIndex actual, string expectedName)
    {
        var expected = Definition(expectedName);
        BootstrapRequest.Require(actual.Name == expectedName, "search_index_identity_mismatch");
        BootstrapRequest.Require(actual.Fields.Count == expected.Fields.Count &&
            actual.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() == expected.Fields.Count,
            "search_schema_field_set_mismatch");
        BootstrapRequest.Require(actual.DefaultScoringProfile is null, "search_default_scoring_not_supported");
        foreach (var wanted in expected.Fields)
        {
            var field = actual.Fields.SingleOrDefault(f => f.Name == wanted.Name);
            BootstrapRequest.Require(field is not null, "search_schema_missing_" + wanted.Name);
            BootstrapRequest.Require(field.Type == wanted.Type &&
                (field.IsKey ?? false) == wanted.IsKey && (field.IsHidden ?? false) == wanted.IsHidden &&
                (field.IsSearchable ?? field.Type == SearchFieldDataType.String) == wanted.IsSearchable &&
                (field.IsFilterable ?? true) == wanted.IsFilterable &&
                (field.IsSortable ?? true) == wanted.IsSortable && (field.IsFacetable ?? true) == wanted.IsFacetable &&
                field.IsStored != false && field.Fields.Count == 0 &&
                field.NormalizerName is null && field.SearchAnalyzerName is null && field.IndexAnalyzerName is null &&
                (field.AnalyzerName == wanted.AnalyzerName || wanted.IsSearchable == true && field.AnalyzerName is null) &&
                field.SynonymMapNames.Count == 0 && field.VectorSearchDimensions is null && field.VectorSearchProfileName is null,
                "search_schema_incompatible_" + wanted.Name);
        }
    }

    internal static async Task<string> Run(SearchIndexClient client, string index, bool apply, CancellationToken ct)
    {
        SearchIndex existing;
        try { existing = (await client.GetIndexAsync(index, ct)).Value; }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            BootstrapRequest.Require(apply, "search_index_creation_required");
            // POST only: a concurrent creator must fail, never turn this into an update.
            await client.CreateIndexAsync(Definition(index), ct);
            RequireCompatible((await client.GetIndexAsync(index, ct)).Value, index);
            return "search_index_created_and_verified";
        }
        RequireCompatible(existing, index);
        return "search_index_schema_verified";
    }
}
