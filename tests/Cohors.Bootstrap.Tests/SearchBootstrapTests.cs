using Azure;
using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Cohors.Bootstrap;

namespace Cohors.Bootstrap.Tests;

public sealed class SearchBootstrapTests
{
    private const string Index = "cohors-knowledge";

    [Fact]
    public async Task MissingIndexIsOnlyCreatedOnApplyAndThenReadBack()
    {
        var client = new FakeSearch();
        await Assert.ThrowsAsync<BootstrapException>(() => SearchBootstrap.Run(client, Index, false, default));
        Assert.Equal(0, client.Creates);
        Assert.Equal("search_index_created_and_verified", await SearchBootstrap.Run(client, Index, true, default));
        Assert.Equal(1, client.Creates);
        Assert.Equal(3, client.Reads);
        await SearchBootstrap.Run(client, Index, true, default);
        Assert.Equal(1, client.Creates);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("filter")]
    [InlineData("searchable")]
    [InlineData("analyzer")]
    [InlineData("hidden")]
    [InlineData("extra")]
    public async Task IncompatibleExistingIndexIsRefusedWithoutAnyWrite(string difference)
    {
        var schema = SearchBootstrap.Definition(Index);
        var content = schema.Fields.Single(f => f.Name == "content");
        switch (difference)
        {
            case "missing": schema.Fields.Remove(content); break;
            case "type": schema.Fields.Remove(content); schema.Fields.Add(new SearchField("content", SearchFieldDataType.Int32)); break;
            case "key": schema.Fields.Single(f => f.Name == "id").IsKey = false; break;
            case "filter": schema.Fields.Single(f => f.Name == "productId").IsFilterable = false; break;
            case "searchable": content.IsSearchable = false; break;
            case "analyzer": content.AnalyzerName = LexicalAnalyzerName.EnLucene; break;
            case "hidden": schema.Fields.Single(f => f.Name == "id").IsHidden = true; break;
            case "extra": schema.Fields.Add(new SearchField("unexpected", SearchFieldDataType.String)); break;
        }
        var client = new FakeSearch { Index = schema };
        await Assert.ThrowsAsync<BootstrapException>(() => SearchBootstrap.Run(client, Index, true, default));
        Assert.Equal(0, client.Creates);
        Assert.Same(schema, client.Index);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ReadErrorsAreNotTreatedAsAnAbsentIndex(int status)
    {
        var failure = new RequestFailedException(status, "synthetic");
        var client = new FakeSearch { ReadFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => SearchBootstrap.Run(client, Index, true, default)));
        Assert.Equal(0, client.Creates);
    }

    [Fact]
    public async Task ConcurrentCreationConflictAndFailedReadbackAreNotSuccesses()
    {
        var failure = new RequestFailedException(409, "synthetic race");
        var client = new FakeSearch { CreateFailure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<RequestFailedException>(() => SearchBootstrap.Run(client, Index, true, default)));
        Assert.Equal(1, client.Creates);
        Assert.Equal(1, client.Reads);

        client = new FakeSearch { ReadbackMissing = true };
        await Assert.ThrowsAsync<RequestFailedException>(() => SearchBootstrap.Run(client, Index, true, default));
        Assert.Equal(1, client.Creates);
        Assert.Equal(2, client.Reads);
    }

    private sealed class FakeSearch : SearchIndexClient
    {
        internal SearchIndex? Index { get; set; }
        internal int Reads { get; private set; }
        internal int Creates { get; private set; }
        internal RequestFailedException? ReadFailure { get; init; }
        internal RequestFailedException? CreateFailure { get; init; }
        internal bool ReadbackMissing { get; init; }

        public override Task<Response<SearchIndex>> GetIndexAsync(string indexName, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (ReadFailure is not null) throw ReadFailure;
            if (Index is null || ReadbackMissing) throw new RequestFailedException(404, "synthetic missing index");
            return Task.FromResult(Response.FromValue<SearchIndex>(Index, new EmptyResponse()));
        }

        public override Task<Response<SearchIndex>> CreateIndexAsync(SearchIndex index, CancellationToken cancellationToken = default)
        {
            Creates++;
            if (CreateFailure is not null) throw CreateFailure;
            Index = index;
            return Task.FromResult(Response.FromValue(index, new EmptyResponse()));
        }
    }

    private sealed class EmptyResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
