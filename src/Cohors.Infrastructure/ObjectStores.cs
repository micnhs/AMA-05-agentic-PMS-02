using System.Security.Cryptography;
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
        var bytes = StoredObjectText.Encode(content);
        Directory.CreateDirectory(directory);
        var hash = Canonical.HashText(content);
        var path = Path.Combine(directory, hash);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await stream.WriteAsync(bytes, ct);
        }
        catch (IOException) when (File.Exists(path))
        {
            StoredObjectText.Decode(await File.ReadAllBytesAsync(path, ct), hash);
        }
        return hash;
    }

    public async Task<string> Read(string hash, CancellationToken ct = default)
    {
        Guard.Against(hash.Length != 64 || hash.Any(ch => !char.IsAsciiHexDigit(ch)), "object_id", "Ungueltige Objekt-ID.");
        return StoredObjectText.Decode(await File.ReadAllBytesAsync(Path.Combine(directory, hash), ct), hash);
    }
}

public sealed class AzureObjectStore(BlobContainerClient container) : IObjectStore
{
    public async Task<string> Put(string content, CancellationToken ct = default)
    {
        var bytes = StoredObjectText.Encode(content);
        var hash = Canonical.HashText(content);
        var blob = container.GetBlobClient(hash);
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(bytes),
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
        return StoredObjectText.Decode(response.Value.Content.ToMemory().Span, hash);
    }
}

internal static class StoredObjectText
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(string content)
    {
        try { return Utf8.GetBytes(content); }
        catch (EncoderFallbackException)
        {
            throw new DomainException("object_encoding", "Objektinhalt enthaelt ungueltige Unicode-Zeichen.");
        }
    }

    public static string Decode(ReadOnlySpan<byte> bytes, string hash)
    {
        Guard.Against(Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash, "object_integrity",
            "SHA-256 der gespeicherten Rohbytes stimmt nicht mit der Objekt-ID ueberein.", 409);
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new DomainException("object_encoding", "Gespeichertes Objekt ist kein gueltiger UTF-8-Text.", 409);
        }
    }
}
