using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class ObjectStoreIntegrityTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("cohors-object-integrity-");
    public void Dispose() => directory.Delete(true);

    [Theory]
    [InlineData("Synthetische Evidenz: \u00c4tiologie \u03b2; XML bleibt unveraendert.\r\n")]
    [InlineData("\ufeffEin ausdrueckliches U+FEFF ist Teil des Inhalts, kein abzuschneidender Header.")]
    public async Task LocalValidUtf8KeepsItsOriginalIdAndBytes(string content)
    {
        var store = new LocalObjectStore(directory.FullName);
        var hash = Canonical.HashText(content);
        Assert.Equal(hash, await store.Put(content));
        Assert.Equal(content, await store.Read(hash));
        var original = await File.ReadAllBytesAsync(Path.Combine(directory.FullName, hash));
        Assert.Equal(Encoding.UTF8.GetBytes(content), original);
        Assert.Equal(hash, await store.Put(content));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory.FullName, hash)));
    }

    public static IEnumerable<object[]> AlteredBytes()
    {
        const string content = "synthetic object";
        yield return ["UTF-8 preamble", content, new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(content)).ToArray()];
        yield return ["UTF-16 re-encoding", content, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(content)).ToArray()];
        yield return ["UTF-8 replacement collision", "\ufffd", new byte[] { 0xff }];
    }

    [Theory]
    [MemberData(nameof(AlteredBytes))]
    public async Task LocalReadAndExistingPutRejectAlteredPhysicalBytes(string alteration, string content, byte[] bytes)
    {
        var hash = Canonical.HashText(content);
        Assert.NotEqual(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var path = Path.Combine(directory.FullName, hash);
        await File.WriteAllBytesAsync(path, bytes);
        var store = new LocalObjectStore(directory.FullName);
        var read = await Assert.ThrowsAsync<DomainException>(() => store.Read(hash));
        var put = await Assert.ThrowsAsync<DomainException>(() => store.Put(content));
        Assert.True(read.Code == "object_integrity" && put.Code == "object_integrity", alteration);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AzureReadAndConflictingPutRejectReplacementCollision()
    {
        using var handler = new BlobResponseHandler { Stored = [0xff] };
        using var http = new HttpClient(handler);
        var store = AzureStore(http);
        var hash = Canonical.HashText("\ufffd");
        Assert.Equal("object_integrity", (await Assert.ThrowsAsync<DomainException>(() => store.Read(hash))).Code);
        Assert.Equal("object_integrity", (await Assert.ThrowsAsync<DomainException>(() => store.Put("\ufffd"))).Code);
        Assert.Equal(2, handler.Reads);
        Assert.Equal(1, handler.Conflicts);
        Assert.Equal(new byte[] { 0xff }, handler.Stored);
    }

    [Fact]
    public async Task BothReadersRejectInvalidUtf8EvenWhenItsRawHashMatches()
    {
        byte[] malformed = [0xff];
        var hash = Convert.ToHexStringLower(SHA256.HashData(malformed));
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, hash), malformed);
        var local = new LocalObjectStore(directory.FullName);
        Assert.Equal("object_encoding", (await Assert.ThrowsAsync<DomainException>(() => local.Read(hash))).Code);
        using var handler = new BlobResponseHandler { Stored = malformed };
        using var http = new HttpClient(handler);
        Assert.Equal("object_encoding", (await Assert.ThrowsAsync<DomainException>(() => AzureStore(http).Read(hash))).Code);
    }

    [Fact]
    public async Task AzureValidUtf8AndExistingPutPreserveLiteralLeadingFeff()
    {
        const string content = "\ufeffSynthetischer Bericht: \u00c4tiologie \u03b2\r\n";
        using var handler = new BlobResponseHandler();
        using var http = new HttpClient(handler);
        var store = AzureStore(http);
        var hash = Canonical.HashText(content);
        Assert.Equal(hash, await store.Put(content));
        Assert.Equal(content, await store.Read(hash));
        Assert.Equal(hash, await store.Put(content));
        Assert.Equal(Encoding.UTF8.GetBytes(content), handler.Stored);
        Assert.Equal(1, handler.Conflicts);
    }

    [Fact]
    public async Task BothWritersRejectUnpairedSurrogatesBeforeWriting()
    {
        var local = new LocalObjectStore(directory.FullName);
        Assert.Equal("object_encoding", (await Assert.ThrowsAsync<DomainException>(() => local.Put("\ud800"))).Code);
        Assert.Empty(directory.GetFiles());
        using var handler = new BlobResponseHandler();
        using var http = new HttpClient(handler);
        Assert.Equal("object_encoding", (await Assert.ThrowsAsync<DomainException>(() => AzureStore(http).Put("\ud800"))).Code);
        Assert.Equal(0, handler.Requests);
        Assert.Null(handler.Stored);
    }

    private static AzureObjectStore AzureStore(HttpClient http) => new(new BlobContainerClient(
        new Uri("https://cohors.invalid/evidence"), new BlobClientOptions { Transport = new HttpClientTransport(http) }));

    private sealed class BlobResponseHandler : HttpMessageHandler
    {
        public byte[]? Stored { get; set; }
        public int Reads { get; private set; }
        public int Conflicts { get; private set; }
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("*", Assert.Single(request.Headers.IfNoneMatch).Tag);
                if (Stored is not null)
                {
                    Conflicts++;
                    var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                    conflict.Headers.Add("x-ms-error-code", "BlobAlreadyExists");
                    return conflict;
                }
                Stored = await request.Content!.ReadAsByteArrayAsync(ct);
                return new(HttpStatusCode.Created);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Reads++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Stored ?? throw new InvalidOperationException("Test object missing.")) };
        }
    }
}
