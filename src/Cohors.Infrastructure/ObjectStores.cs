using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class LocalObjectStore(string directory) : IObjectStore
{
    public async Task<string> Put(string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var hash = Canonical.HashText(content);
        var path = Path.Combine(directory, hash);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, ct);
            Guard.Against(Canonical.HashText(existing) != hash, "object_integrity", "Evidenzobjekt wurde veraendert.", 409);
        }
        return hash;
    }

    public async Task<string> Read(string hash, CancellationToken ct = default)
    {
        Guard.Against(hash.Length != 64 || hash.Any(ch => !char.IsAsciiHexDigit(ch)), "object_id", "Ungueltige Objekt-ID.");
        var value = await File.ReadAllTextAsync(Path.Combine(directory, hash), ct);
        Guard.Against(Canonical.HashText(value) != hash, "object_integrity", "Hashpruefung der Evidenz fehlgeschlagen.", 409);
        return value;
    }
}

public sealed class AzureObjectStore(BlobContainerClient container) : IObjectStore
{
    public async Task<string> Put(string content, CancellationToken ct = default)
    {
        var hash = Canonical.HashText(content);
        var blob = container.GetBlobClient(hash);
        try
        {
            await blob.UploadAsync(BinaryData.FromString(content),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException e) when (e.Status == 409 || e.Status == 412)
        {
            await Read(hash, ct);
        }
        return hash;
    }

    public async Task<string> Read(string hash, CancellationToken ct = default)
    {
        Guard.Against(hash.Length != 64 || hash.Any(ch => !char.IsAsciiHexDigit(ch)), "object_id", "Ungueltige Objekt-ID.");
        var response = await container.GetBlobClient(hash).DownloadContentAsync(ct);
        var content = response.Value.Content.ToString();
        Guard.Against(Canonical.HashText(content) != hash, "object_integrity", "Blob-Hashpruefung fehlgeschlagen.", 409);
        return content;
    }
}
