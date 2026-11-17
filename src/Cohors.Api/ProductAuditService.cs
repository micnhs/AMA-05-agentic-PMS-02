using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Api;

public sealed record ProductAuditFile(byte[] Bytes, string ContentType, string FileName);

public sealed class ProductAuditService(SqlStateStore store, IObjectStore objects, PolicyCatalog policies, TimeProvider clock)
{
    private static readonly SemaphoreSlim Exporter = new(1);

    public async Task<ProductAuditFile> Export(string product, Actor actor, CancellationToken ct = default)
    {
        ProductAuditRules.RequireReader(actor, product);
        _ = policies.Device(product);
        await Exporter.WaitAsync(ct);
        try { return await Build(product, actor, ct); }
        finally { Exporter.Release(); }
    }

    private async Task<ProductAuditFile> Build(string product, Actor actor, CancellationToken ct)
    {
        var capture = await store.CaptureProductAudit(product, actor, clock.GetUtcNow(), ct);
        var cutHash = Canonical.Hash(capture);
        var (references, exclusions) = ProductAuditRules.Objects(capture.Aggregates);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var objectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        var index = capture.Aggregates.Select((aggregate, i) => new
        {
            aggregate.Kind, aggregate.Id, aggregate.ProductId, aggregate.Products,
            aggregate.Manifest.CurrentVersion, aggregate.Manifest.LastHash,
            auditPath = $"audit/{i:D5}.json",
            versions = aggregate.Versions.Select(v => new
            {
                v.Version, v.At, v.Hash, path = $"versions/{i:D5}/v{v.Version:D6}.json"
            }).ToArray()
        }).ToArray();
        for (var i = 0; i < capture.Aggregates.Length; i++)
        {
            var aggregate = capture.Aggregates[i];
            AddJson(index[i].auditPath, aggregate.Manifest);
            for (var v = 0; v < aggregate.Versions.Length; v++)
                Add(index[i].versions[v].path, Encoding.UTF8.GetBytes(aggregate.Versions[v].Json));
        }
        foreach (var reference in references)
        {
            byte[] value;
            try { value = await objects.ReadBytes(reference.Hash, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Azure.RequestFailedException)
            {
                throw new DomainException("audit_object_unavailable",
                    $"Gebundene Objektbytes {reference.Hash} konnten nicht vollstaendig gelesen werden; kein vollstaendiges Auditpaket.", 409, ex);
            }
            Guard.Against(Hash(value) != reference.Hash, "audit_object_bytes",
                "Gebundene Objektbytes fehlen oder stimmen nicht; kein als vollstaendig bezeichnetes Auditpaket.", 409);
            var path = "objects/" + reference.Hash + ObjectExtension(value);
            objectPaths.Add(reference.Hash, path);
            Add(path, value);
        }
        AddJson("transport/outbox.json", capture.Transport.Outbox);
        AddJson("transport/inbox.json", capture.Transport.Inbox);
        AddJson("transport/commands.json", capture.Transport.Commands);
        AddJson("transport/case-deadlines.json", capture.Transport.Deadlines);
        AddJson("verification.json", new
        {
            state = "VerifiedLocalReadOnlyCut",
            source = "SQL authoritative state and original text/binary object bytes; no Search-derived originals",
            checks = new[]
            {
                "Complete contiguous historical versions, current payload, original version hashes and audited event times",
                "Local audit hash chains; exact persistent command and outbox version bindings; delivery/inbox consistency",
                "Current and historical product membership, grouped snapshots, encoded model-input hash and all-member reader scope",
                "Every structured version reference resolves uniquely to an exported SQL historical payload; related source aggregate histories are retained",
                "Persistent case deadline projection against current case; other deadlines retained in original PSUR/BS/action versions",
                "Every retained object reference resolved with matching content hash; no blanket exception for missing raw references"
            },
            externalLedgerDigestVerification = "Not performed; deployment gate. A local hash chain is not an independently protected digest.",
            recovery = "Not performed by this read export. No restore, Search rebuild, physical-journal attestation or backup deletion claim.",
            clinicalAndAuthorityAcceptance = "Not performed. Human decisions and simulated receipts are preserved, not newly approved.",
            exclusions,
            dataClasses = new
            {
                regulatory = "All selected original aggregate versions, audit records and retained object bytes; approvals and source references remain in their original payloads.",
                raw = "Only the retention record's own raw binding/deletion metadata is excluded from object retrieval. Independent retained raw references fail closed; raw quarantine contents are not silently promoted to regulatory evidence.",
                contacts = "No separate address book. Synthetic contact fields already bound into retained reports remain part of those original versions; no blanket contact-retention period is inferred.",
                telemetry = "Operational heartbeat aggregates and external operational logs are excluded, not relabelled as regulatory evidence.",
                derived = "Search/cache projections, application settings, credentials and external backups are not exported."
            },
            limits = new { maximumUtf8Bytes = ProductAuditRules.MaximumBytes, maximumSqlVersions = ProductAuditRules.MaximumVersions }
        });
        AddJson("manifest.json", new
        {
            contract = ProductAuditRules.Contract,
            requestedProductId = capture.ProductId,
            products = capture.Products,
            capture.CapturedAt,
            sqlCutHash = cutHash,
            scope = "Full retained history directly concerning the requested product, including historical/non-leading membership, plus explicitly version-referenced source aggregates. All retained member scopes are required; membership alone does not include unrelated co-member records.",
            boundary = "SYNTHETISCHER COHORS-PILOT. Lesekopie, keine reale BS-/Behoerdenuebermittlung, keine neue Freigabe, kein Backup oder produktiver WORM-Nachweis.",
            aggregates = index,
            objects = references.Select(r => new { r.Hash, path = objectPaths[r.Hash], r.Uses }),
            checksums = "checksums.json binds every other archive entry with SHA-256; it cannot hash itself and is not externally signed."
        });
        Add("READ-ME.txt", Encoding.UTF8.GetBytes(
            "COHORS / SYNTHETISCHER PILOT / KEINE REALE UEBERMITTLUNG\n" +
            "manifest.json: Produktumfang, Versionspfade, Freigabe-/Quellen-Originale und Objektverwendungen.\n" +
            "verification.json: Ausgefuehrte lokale Pruefungen und ausdrueckliche Ausschluesse/Grenzen.\n" +
            "versions/: Unveraenderte SQL-Originaltexte als UTF-8; keine Korrektur oder Narrative-Kuerzung.\n" +
            "audit/: Lokale Auditketten. objects/: Exakte gebundene Text-/XML-/JSON-/PDF-/Binaerbytes mit SHA-256, Pfade im Manifest.\n" +
            "transport/: Persistente Command-/Outbox-/Inbox-Zustaende und Fallfristprojektion desselben SQL-Cuts.\n" +
            "checksums.json: SHA-256 aller anderen Dateien. Keine unabhaengige Signatur, externe Ledgerpruefung oder neue fachliche Annahme.\n"));
        AddJson("checksums.json", files.ToDictionary(f => f.Key, f => Hash(f.Value), StringComparer.Ordinal));
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.Value);
            }
        return new(buffer.ToArray(), "application/zip", $"cohors-audit-{Canonical.HashText(product)[..12]}-{cutHash[..12]}.zip");

        void AddJson(string path, object value) => Add(path, Encoding.UTF8.GetBytes(Canonical.Serialize(value)));

        void Add(string path, byte[] value)
        {
            bytes += value.Length;
            Guard.Against(bytes > ProductAuditRules.MaximumBytes, "audit_size",
                "Das vollstaendige Auditpaket ueberschreitet 128 MiB Originalbytes. Keine stille Kuerzung oder Teillieferung.", 409);
            files.Add(path, value);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ObjectExtension(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith("%PDF-"u8)) return ".pdf";
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); return ".txt"; }
        catch (DecoderFallbackException) { return ".bin"; }
    }
}
