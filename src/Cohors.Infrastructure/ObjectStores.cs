using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class LocalObjectStore(string directory) : IObjectStore, IRawQuarantineStore
{
    public Task<string> Put(string content, CancellationToken ct = default) => PutAt(directory, content, ct);

    private string JournalPath(string hash) => Path.Combine(directory, ".deletion-receipts", hash + ".json");

    private async Task<string> PutAt(string targetDirectory, string content, CancellationToken ct)
    {
        var bytes = StoredObjectText.Encode(content);
        var hash = Canonical.HashText(content);
        Guard.Against(File.Exists(JournalPath(hash)), "object_retired",
            "Ein Loeschbeleg/-versuch bindet diese Bytes; kein unbemerkter Wiederimport.", 409);
        Directory.CreateDirectory(targetDirectory);
        var path = Path.Combine(targetDirectory, hash);
        var created = false;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            created = true;
            await stream.WriteAsync(bytes, ct);
        }
        catch (IOException) when (File.Exists(path))
        {
            StoredObjectText.Decode(await File.ReadAllBytesAsync(path, ct), hash);
        }
        if (File.Exists(JournalPath(hash)))
        {
            if (created) File.Delete(path);
            throw new DomainException("object_retired", "Parallel begonnener Wiederimport wurde verworfen; Loeschbeleg bleibt verbindlich.", 409);
        }
        return hash;
    }

    public async Task<string> Read(string hash, CancellationToken ct = default)
    {
        Guard.Against(hash.Length != 64 || hash.Any(ch => !char.IsAsciiHexDigit(ch)), "object_id", "Ungueltige Objekt-ID.");
        Guard.Against(File.Exists(JournalPath(hash)), "object_retired",
            "Loeschbeleg/-versuch sperrt das erneute Lesen dieser Rohbytes.", 409);
        return StoredObjectText.Decode(await File.ReadAllBytesAsync(Path.Combine(directory, hash), ct), hash);
    }

    public async Task<RetentionObjectBinding> CreateSyntheticRaw(string content, CancellationToken ct = default)
    {
        var hash = await PutAt(Path.Combine(directory, "raw-quarantine"), content, ct);
        return new(RetentionRules.ObjectContract, RetentionRules.Raw, RetentionRules.RawPrefix + hash, hash);
    }

    public async Task<string> ReadRaw(RetentionObjectBinding value, CancellationToken ct = default)
    {
        RetentionRules.RequireObject(value);
        Guard.Against(value.DataClass != RetentionRules.Raw, "retention_data_class", "Nur explizite Rohdatenquarantaene.");
        return StoredObjectText.Decode(await File.ReadAllBytesAsync(Path.Combine(directory, "raw-quarantine", value.Hash), ct), value.Hash);
    }

    public async Task<ObjectDeletionReceipt> DeleteRaw(RetentionRecord value, DateTimeOffset at, CancellationToken ct = default)
    {
        RetentionRules.RequireObject(value.Object);
        Guard.Against(!value.Synthetic || value.Object.DataClass != RetentionRules.Raw || value.LegalHold ||
            value.Authorization is not { ExternalCopiesReviewed: true } ||
            value.Authorization.ScopeHash != RetentionRules.Scope(value, null),
            "retention_authorization", "Keine passende menschliche Loeschfreigabe fuer synthetische Testrohbytes.", 403);
        var hash = value.Object.Hash;
        var path = Path.Combine(directory, "raw-quarantine", hash);
        var journalPath = JournalPath(hash);
        Guard.Against(File.Exists(Path.Combine(directory, hash)), "retention_shared_store_bytes",
            "Gleiche Bytes sind ausserhalb der Rohdatenquarantaene vorhanden; keine vollstaendige Loeschung bestaetigt.", 409);
        ObjectDeletionReceipt receipt;
        if (File.Exists(journalPath))
        {
            var bytes = await File.ReadAllBytesAsync(journalPath, ct);
            try { receipt = Canonical.Read<ObjectDeletionReceipt>(new UTF8Encoding(false, true).GetString(bytes)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or DecoderFallbackException)
            {
                throw new DomainException("retention_deletion_receipt", "Loeschjournal ist kein gueltiger kanonischer UTF-8-Beleg.", 409);
            }
            Guard.Against(receipt is null, "retention_deletion_receipt", "Loeschjournal fehlt inhaltlich.", 409);
            StoredObjectText.Decode(bytes, Canonical.Hash(receipt));
            Guard.Against(receipt!.Contract != RetentionRules.DeletionContract || receipt.ObjectKey != value.Object.ObjectKey ||
                receipt.Hash != hash || receipt.OperationId != value.Authorization!.Id || receipt.Storage != "Local" ||
                receipt.State is not ("Prepared" or "Deleted"), "retention_deletion_receipt", "Loeschjournal bindet einen anderen Vorgang.", 409);
            Guard.Against(receipt.State == "Deleted" && File.Exists(path), "retention_reappeared",
                "Bereits geloeschte Rohbytes sind wieder aufgetaucht; keine automatische Wiederfreigabe.", 409);
            if (receipt.State == "Deleted") return receipt;
        }
        else
        {
            await ReadRaw(value.Object, ct);
            receipt = new(RetentionRules.DeletionContract, value.Authorization!.Id, value.Object.ObjectKey, hash, at, "Prepared", "Local");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            await using var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await journal.WriteAsync(StoredObjectText.Encode(Canonical.Serialize(receipt)), ct);
            journal.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
        {
            await ReadRaw(value.Object, ct);
            File.Delete(path);
        }
        Guard.Against(File.Exists(path) || File.Exists(Path.Combine(directory, hash)), "retention_store_incomplete",
            "Rohobjekt oder parallele Kopie ist noch vorhanden; Loeschung bleibt unbestaetigt.", 409);
        var completed = receipt with { State = "Deleted", At = at };
        var temporary = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var journal = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await journal.WriteAsync(StoredObjectText.Encode(Canonical.Serialize(completed)), ct);
                journal.Flush(flushToDisk: true);
            }
            File.Move(temporary, journalPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return completed;
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
