using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public static partial class FabricC1Deployment
{
    public static void WriteHubConsumerHandoff(FabricC1HubBindingsExport export, string directory)
    {
        FabricC1PrivateFiles.RequireAbsoluteNonLink(directory);
        FabricC1PrivateFiles.RequireDirectory(directory);
        if (export.Consumer is null || export.EvidenceFiles is null || export.EvidenceFiles.Count > 128 ||
            export.EvidenceFiles.Count == 0 && export.Consumer["state"]?.GetValue<string>() is not ("reserved" or "peered" or "connected"))
            throw new DomainException("c1_hub_handoff", "An exact own slot and its original bounded evidence files are required.", 409);
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (export.Consumer["dnsRecords"] is JsonArray records)
            foreach (var record in records) references.Add(RequiredDigest(record?["dnsEvidenceSha256"]));
        if (export.Consumer["offboarding"] is JsonObject offboarding)
            foreach (var field in offboarding) references.Add(RequiredDigest(field.Value));
        Guard.Against(!references.SetEquals(export.EvidenceFiles.Keys), "c1_hub_handoff",
            "The exact slot must reference every delivered original evidence file and no missing file.", 409);
        var index = new JsonObject();
        foreach (var (digest, bytes) in export.EvidenceFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Guard.Against(!FabricC1Execution.Hex(digest, 64) || bytes is null || bytes.Length is < 1 or > 262144 ||
                digest != Convert.ToHexStringLower(SHA256.HashData(bytes)),
                "c1_hub_handoff", "The handoff cannot rename, regenerate or omit original evidence bytes.", 409);
            index[digest] = digest + ".json";
        }
        var consumerBytes = JsonSerializer.SerializeToUtf8Bytes(export.Consumer, Canonical.Json);
        Guard.Against(consumerBytes.Length > 262144, "c1_hub_handoff", "The exact own slot exceeds the bounded handoff.", 409);
        var subject = HubPrerequisiteSubject(export.Consumer);
        var destination = Path.Combine(directory, subject);
        FabricC1PrivateFiles.RequireAbsoluteNonLink(destination);
        if (!Directory.Exists(destination))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(destination);
            else Directory.CreateDirectory(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        FabricC1PrivateFiles.RequireDirectory(destination);
        foreach (var (digest, bytes) in export.EvidenceFiles) WriteExactPrivateFile(Path.Combine(destination, digest + ".json"), bytes);
        WriteExactPrivateFile(Path.Combine(destination, "hub-evidence-files.json"), JsonSerializer.SerializeToUtf8Bytes(index, Canonical.Json));
        WriteExactPrivateFile(Path.Combine(destination, "consumer-slot.json"), consumerBytes);
    }

    private static string RequiredDigest(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var digest) && FabricC1Execution.Hex(digest, 64) ? digest :
            throw new DomainException("c1_hub_handoff", "Every evidence reference needs its exact original file digest.", 409);

    private static void WriteExactPrivateFile(string path, byte[] bytes)
    {
        FabricC1PrivateFiles.RequireAbsoluteNonLink(path);
        if (File.Exists(path))
        {
            FabricC1PrivateFiles.RequireFile(path);
            using var existing = File.OpenRead(path);
            Guard.Against(existing.Length != bytes.Length ||
                !SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)),
                "c1_hub_handoff_drift", "An existing private handoff file differs; never overwrite it on resume.", 409);
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            FabricC1PrivateFiles.RequireFile(temporary);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
