using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cohors.Domain;
using Cohors.Infrastructure;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Cohors.Api;

public sealed record PsurExportData(string Format, DateTimeOffset ExportedAt, Psur Report,
    QualityFinding[] CurrentFindings, string SnapshotCheckState, object Audit, string Boundary);
public sealed record PsurExportFile(byte[] Bytes, string ContentType, string FileName);

public sealed class PsurExportService(SqlStateStore store, TimeProvider clock)
{
    private static readonly SemaphoreSlim Renderer = new(1);
    private static readonly Lazy<bool> Fonts = new(() =>
    {
        GlobalFontSettings.FontResolver = new ReportFonts();
        MigraDoc.PredefinedFontsAndChars.ErrorFontName = "Noto Sans";
        return true;
    });

    public async Task<PsurExportData> Read(string id, int version, Actor actor, CancellationToken ct = default)
    {
        var report = await store.Get<Psur>("psur", id, ct) ?? throw new DomainException("psur_missing", "PSUR fehlt.", 404);
        PsurGroupRules.RequireScope(report.Snapshot, actor);
        Guard.Against(report.Version != version, "version_conflict", "Bericht hat sich geaendert; gewuenschte Fassung neu laden.", 409);
        Guard.Against(!report.Simulated || report.SnapshotHash != Canonical.Hash(report.Snapshot) ||
            report.ScopeHash.Length > 0 && report.ScopeHash != PortfolioRules.PsurScope(report),
            "psur_hash", "Exportquelle stimmt nicht mit dem gebundenen Snapshot/Entwurf ueberein.", 409);
        var audit = await store.Manifest(id, ct, "psur");
        Guard.Against((await store.Get<Psur>("psur", id, ct))?.Version != version, "version_conflict",
            "Bericht wurde waehrend der Exportpruefung geaendert; Fassung neu laden.", 409);
        var findings = PortfolioRules.CheckPsur(report.Snapshot, report.Draft);
        return new("cohors-psur-export-1", clock.GetUtcNow(), report, findings,
            findings.Any(f => f.Severity is "Critical" or "Major") ? "Blocked" : "NoBlockingSnapshotFinding", audit,
            "SYNTHETISCHE DEMO. Lokaler Leseexport, keine Behoerden-/BS-Uebermittlung oder EUDAMED-Webformularabnahme. " +
            "Freigaben binden den angegebenen Quell-Scope; PDF/ZIP sind gekennzeichnete abgeleitete Lesekopien, keine zusaetzliche Signatur oder Formatannahme. " +
            "Aktuelle Nachweisbefunde stehen neben dem unveraenderten Originalbericht; fehlende Altformatdaten werden nicht ergaenzt.");
    }

    public async Task<PsurExportFile> Export(string id, int version, string format, Actor actor, CancellationToken ct = default)
    {
        Guard.Against(format is not ("json" or "pdf" or "zip"), "psur_export_format", "Nur JSON, PDF oder vollstaendiges ZIP-Paket.");
        var data = await Read(id, version, actor, ct);
        var name = $"cohors-psur-{data.Report.SnapshotHash[..12]}-v{version}";
        if (format == "json") return new(Encoding.UTF8.GetBytes(Canonical.Serialize(data)), "application/json", name + ".json");
        await Renderer.WaitAsync(ct);
        byte[] pdf;
        try { pdf = Render(data); }
        finally { Renderer.Release(); }
        if (format == "pdf") return new(pdf, "application/pdf", name + ".pdf");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["report.pdf"] = pdf,
            ["report.json"] = Encoding.UTF8.GetBytes(Canonical.Serialize(data.Report)),
            ["snapshot.json"] = Encoding.UTF8.GetBytes(Canonical.Serialize(data.Report.Snapshot)),
            ["audit.json"] = Encoding.UTF8.GetBytes(Canonical.Serialize(data.Audit)),
            ["metadata.json"] = Encoding.UTF8.GetBytes(Canonical.Serialize(new {
                data.Format, data.ExportedAt, data.Boundary, data.CurrentFindings, data.SnapshotCheckState,
                data.Report.Id, data.Report.Version, data.Report.SnapshotHash, data.Report.ScopeHash,
                data.Report.Status, data.Report.Approvals, data.Report.NotifiedBodyReceipt,
                data.Report.Snapshot.Series, data.Report.Snapshot.DateBasis,
                products = PsurGroupRules.Products(data.Report.Snapshot),
                group = data.Report.Snapshot.Group is { } group ? new {
                    group.Contract, group.DefinitionSource, group.Definition, group.SeparationRule,
                    members = group.Members.Select(m => new { m.Product, m.Governance, snapshotHash = Canonical.Hash(m.Snapshot) })
                } : null
            }))
        };
        var checksums = files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(f.Value)), StringComparer.Ordinal);
        files.Add("checksums.json", Encoding.UTF8.GetBytes(Canonical.Serialize(checksums)));
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.Value);
            }
        return new(buffer.ToArray(), "application/zip", name + ".zip");
    }

    private static byte[] Render(PsurExportData data)
    {
        _ = Fonts.Value;
        var report = data.Report;
        var document = new Document();
        document.Info.Title = $"Cohors {report.Snapshot.Series?.Reference ?? report.Id} / synthetischer PSUR";
        document.Info.Author = "Cohors synthetic pilot";
        document.Info.Subject = "Read-only derivative, no regulatory transmission or clinical sign-off";
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Noto Sans";
        normal.Font.Size = 9;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        foreach (var heading in new[] { StyleNames.Heading1, StyleNames.Heading2 })
        {
            document.Styles[heading]!.Font.Name = "Noto Sans";
            document.Styles[heading]!.Font.Bold = true;
            document.Styles[heading]!.Font.Color = Color.FromRgb(17, 66, 112);
            document.Styles[heading]!.Font.Size = heading == StyleNames.Heading1 ? 15 : 11;
            document.Styles[heading]!.ParagraphFormat.KeepWithNext = true;
        }
        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.Headers.Primary.AddParagraph("COHORS / SYNTHETISCH / KEINE REALE MELDUNG");
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Right;
        footer.AddText("Seite "); footer.AddPageField(); footer.AddText(" / "); footer.AddNumPagesField();
        section.AddParagraph("Cohors - PSUR-Lesekopie", StyleNames.Heading1);
        section.AddParagraph($"Bearbeitungsstand: {report.Status}; technische Version {report.Version}; Agentenmodus {report.Mode}.");
        section.AddParagraph($"Deterministische Snapshot-Pruefung beim Export: {data.SnapshotCheckState}. Keine neue Quellenfreigabe oder Risikoakzeptanz.");
        section.AddParagraph($"Erzeugt {data.ExportedAt:O}. Snapshot {report.Snapshot.FrozenAt:O}.");
        section.AddParagraph("Snapshot SHA-256: " + report.SnapshotHash);
        section.AddParagraph("Gebundener Freigabe-Scope: " + report.ScopeHash);
        section.AddParagraph(data.Boundary);
        section.AddParagraph("Human Reviews und Quittung", StyleNames.Heading2);
        foreach (var approval in report.Approvals)
            section.AddParagraph($"{approval.Role}: {approval.ActorName} ({approval.ActorId}), {approval.At:O}. {approval.Rationale} Scope {approval.ScopeHash}.");
        section.AddParagraph(report.NotifiedBodyReceipt is null ? "Keine simulierte BS-Quittung." :
            "Ausschliesslich simulierte BS-Quittung: " + report.NotifiedBodyReceipt);
        section.AddParagraph("Aktuelle maschinelle Nachweisbefunde", StyleNames.Heading2);
        foreach (var finding in data.CurrentFindings)
            section.AddParagraph($"{finding.Severity} / {finding.Code}: {finding.Description} {finding.Remedy}");
        section.AddParagraph("Gebundener Berichtstext (Lesedarstellung; Original in JSON)", StyleNames.Heading1);
        if (string.IsNullOrWhiteSpace(report.Draft))
            section.AddParagraph("Kein Berichtstext vorhanden. Insbesondere ist ein fehlgeschlagener Agentenlauf kein erfolgreicher Entwurf.");
        foreach (var line in report.Draft.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal)) section.AddParagraph(line[3..].TrimEnd('\r'), StyleNames.Heading2);
            else if (line.StartsWith("# ", StringComparison.Ordinal)) section.AddParagraph(line[2..].TrimEnd('\r'), StyleNames.Heading1);
            else section.AddParagraph(line.TrimEnd('\r'));
        }
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }

    private sealed class ReportFonts : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            familyName == "Noto Sans" ? new(bold ? "bold" : "regular", false, italic) : null;
        public byte[] GetFont(string faceName)
        {
            var (file, expected) = faceName switch
            {
                "regular" => ("NotoSans-Regular.ttf", "b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5"),
                "bold" => ("NotoSans-Bold.ttf", "c976e4b1b99edc88775377fcc21692ca4bfa46b6d6ca6522bfda505b28ff9d6a"),
                _ => throw new InvalidOperationException("Unknown controlled report font.")
            };
            using var resource = typeof(PsurExportService).Assembly.GetManifestResourceStream("Cohors.Api.ReportFonts." + file)
                ?? throw new InvalidOperationException("Controlled report font resource is missing.");
            using var bytes = new MemoryStream();
            resource.CopyTo(bytes);
            var result = bytes.ToArray();
            if (Convert.ToHexStringLower(SHA256.HashData(result)) != expected)
                throw new InvalidOperationException("Controlled report font hash differs.");
            return result;
        }
    }
}
