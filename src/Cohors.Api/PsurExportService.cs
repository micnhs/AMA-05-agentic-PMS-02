using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace Cohors.Api;

public sealed record PsurExportData(string Format, DateTimeOffset ExportedAt, Psur Report,
    QualityFinding[] CurrentFindings, string SnapshotCheckState, object Audit, string Boundary, PsurMonthlyView[] MonthlyCounts);
public sealed record PsurExportFile(byte[] Bytes, string ContentType, string FileName);

public sealed class PsurExportService(SqlStateStore store, TimeProvider clock) : IEudamedDocumentRenderer
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
        var regulatory = PsurRegulatoryRules.For(report.Snapshot);
        if (regulatory is not null)
            PsurRegulatoryRules.RequireSources(regulatory,
                (await store.Capture(PsurGroupRules.Products(report.Snapshot), ct)).History ?? [], report.Snapshot.Start, report.Snapshot.End);
        var audit = await store.Manifest(id, ct, "psur");
        Guard.Against((await store.Get<Psur>("psur", id, ct))?.Version != version, "version_conflict",
            "Bericht wurde waehrend der Exportpruefung geaendert; Fassung neu laden.", 409);
        var findings = PortfolioRules.CheckPsur(report.Snapshot, report.Draft);
        return new("cohors-psur-export-2", clock.GetUtcNow(), report, findings,
            findings.Any(f => f.Severity is "Critical" or "Major") ? "Blocked" : "NoBlockingSnapshotFinding", audit,
            "SYNTHETISCHE DEMO. Lokaler Leseexport, keine Behoerden-/BS-Uebermittlung oder EUDAMED-Webformularabnahme. " +
            "Freigaben binden den angegebenen Quell-Scope; PDF/ZIP sind gekennzeichnete abgeleitete Lesekopien, keine zusaetzliche Signatur oder Formatannahme. " +
            "Aktuelle Nachweisbefunde und abgeleitete Monatszahlen stehen neben dem unveraenderten Originalbericht; fehlende Altformatdaten werden nicht ergaenzt.",
            PsurMonthlyCounts.All(report));
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
                data.Format, data.ExportedAt, data.Boundary, data.CurrentFindings, data.SnapshotCheckState, data.MonthlyCounts,
                data.Report.Id, data.Report.Version, data.Report.SnapshotHash, data.Report.ScopeHash,
                data.Report.Status, data.Report.Approvals, data.Report.NotifiedBodyReceipt,
                data.Report.Snapshot.Series, data.Report.Snapshot.DateBasis,
                regulatoryHistory = PsurRegulatoryRules.For(data.Report.Snapshot) is { } history ? new {
                    history.Contract, history.Products, history.CutoffExclusiveUtc, history.Coverage,
                    sources = PsurRegulatoryRules.References(history)
                } : null,
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

    public async Task<EudamedRenderedDocument> Render(EudamedDossierKind kind, string id, EudamedDossierContent content,
        EudamedSource[] sources, Actor actor, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        if (kind == EudamedDossierKind.PSUR)
        {
            var source = EudamedRules.Source<Psur>(sources, "psur");
            var file = await Export(source.Id, source.Version, "pdf", actor, ct);
            return new(file.Bytes, file.ContentType, file.FileName);
        }
        Guard.Against(kind != EudamedDossierKind.FSCA || content.Fsca is null,
            "eudamed_document_kind", "Diese Projektion erstellt nur genaue PSUR- oder FSCA-PDF-Lesekopien.");
        EudamedRules.RequireContent(kind, content);
        foreach (var source in sources.Where(s => s.Kind == "capa"))
            actor.RequireProduct(Canonical.Read<Capa>(source.Json).ProductId);
        await Renderer.WaitAsync(ct);
        try
        {
            _ = Fonts.Value;
            var document = new Document();
            document.Info.Title = "Cohors FSCA / " + content.Title;
            document.Info.Author = "Cohors synthetic pilot";
            document.Info.Subject = "Synthetic human-authored FSCA draft; no approval or authority transmission";
            var normal = document.Styles[StyleNames.Normal]!;
            normal.Font.Name = "Noto Sans";
            normal.Font.Size = 9;
            normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
            var section = document.AddSection();
            section.AddParagraph("SYNTHETISCHE DEMO - FSCA-Entwurf, keine reale Sicherheitsmassnahme.");
            section.AddParagraph(content.Title, StyleNames.Heading1);
            section.AddParagraph($"Dossier {id}; Sprache {content.Language}; Jurisdiktion {content.Jurisdiction}; Laender {string.Join(", ", content.Countries)}.");
            var fsca = content.Fsca!;
            foreach (var (heading, text) in new[]
            {
                ("Medizinisch-technische Einordnung", content.ClinicalRationale),
                ("Regulatorische Einordnung", content.RegulatoryRationale),
                ("Betroffener Umfang", $"Chargen: {string.Join(", ", fsca.Batches)}. Seriennummern: {string.Join(", ", fsca.SerialNumbers)}."),
                ("Gesundheitsgefaehrdung und Unsicherheit", fsca.HealthHazard + "\n" + fsca.HazardProbability),
                ("Ursachenstand", fsca.RootCauseStatus + "\n" + fsca.RootCauseRationale),
                ("Herstellermassnahme", fsca.ManufacturerAction),
                ("Anwenderhandlung", fsca.UserAction),
                ("Patienten-Nachbeobachtung", fsca.PatientFollowUp),
                ("Verantwortung und Wirksamkeit", $"{fsca.ResponsiblePerson}; faellig {fsca.DueAt:O}.\n{fsca.EffectivenessCriterion}")
            })
            {
                section.AddParagraph(heading, StyleNames.Heading2);
                section.AddParagraph(text);
            }
            section.AddParagraph("Unveraenderte Quellen", StyleNames.Heading2);
            foreach (var source in sources)
                section.AddParagraph($"{source.Kind}:{source.Source.Id}@{source.Source.Version}; SHA-256 {source.Source.Hash}.");
            section.AddParagraph("Diese Lesekopie erteilt keine Freigabe. Erst zwei getrennte menschliche Signaturen binden den gesamten Dossierinhalt und die konkreten PDF-Bytes.");
            return new(RenderDocument(document), "application/pdf", $"cohors-fsca-{Canonical.Hash(new { id, content, sources })[..16]}.pdf");
        }
        finally { Renderer.Release(); }
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
        section.AddParagraph("Abgeleitete Monatsfallzahlen aus dem gebundenen Snapshot", StyleNames.Heading1);
        foreach (var monthly in data.MonthlyCounts)
        {
            section.AddParagraph($"{monthly.ProductId} / Basic-UDI {monthly.BasicUdi ?? "historisch nicht gebunden"} / {monthly.Region}", StyleNames.Heading2);
            section.AddParagraph($"Periode {monthly.Start:yyyy-MM-dd} bis {monthly.End:yyyy-MM-dd}; Kenntnisstichtag exklusiv {monthly.KnowledgeCutoffExclusiveUtc:O}; Freeze {monthly.FrozenAt:O}.");
            section.AddParagraph($"Mitglieds-Snapshot SHA-256 {monthly.MemberSnapshotHash ?? "nicht ableitbar"}; Projektion {monthly.Contract}.");
            section.AddParagraph(monthly.Boundary);
            if (monthly.Status != "Available")
                section.AddParagraph("Monatssicht nicht aus dieser historischen Quelle ableitbar: " + string.Join(" ", monthly.Issues));
            foreach (var month in monthly.Months)
                section.AddParagraph($"{month.Month} / {month.From:yyyy-MM-dd} bis {month.Through:yyyy-MM-dd}: {month.Cases.Length} eindeutige Faelle; " +
                    $"davon {month.OpenAtCutoff.Length} am Stichtag nachweislich offen, {month.UnknownStatusAtCutoff.Length} mit unbekanntem Stichtagsstatus. " +
                    $"Quellversionen: {string.Join(", ", month.Cases.Select(c => $"{c.Id}@{c.Version} / SHA-256 {c.Hash}"))}.");
            foreach (var excluded in monthly.Exclusions)
                section.AddParagraph($"{excluded.Kind}: {excluded.Cases.Length}. {excluded.Reason} " +
                    string.Join(", ", excluded.Cases.Select(c => $"{c.Id}@{c.Version} / SHA-256 {c.Hash}")));
            foreach (var source in monthly.Sources)
                section.AddParagraph($"{source.Reference.Id}@{source.Reference.Version}: Ereignis {source.OccurredAt?.ToString("O") ?? "unbekannt"}; " +
                    $"erste Kenntnis {source.FirstAwarenessAt:O}; Systemeingang {source.SystemReceivedAt:O}; Stichtagsstatus {source.StatusAtCutoff} " +
                    $"/ v{source.CutoffVersion?.Version.ToString() ?? "unbekannt"}. Alle Zeitpunkte aus dem eingefrorenen Quellenmanifest.");
        }
        if (PsurRegulatoryRules.For(report.Snapshot) is { } regulatory)
        {
            section.AddParagraph("Gebundener BS-Korrespondenzanhang / ausschliesslich synthetisch", StyleNames.Heading1);
            section.AddParagraph(regulatory.Coverage);
            foreach (var state in regulatory.Reviews)
            {
                var review = state.CurrentAtFreeze;
                section.AddParagraph($"{review.Reference} / SQL v{review.Version}", StyleNames.Heading2);
                section.AddParagraph($"Stichtagsstatus {state.StatusAtCutoff} / v{state.CutoffSource?.Version.ToString() ?? "unbekannt"}; Freeze-Status {review.Status}. {state.Reason}");
                section.AddParagraph("Quellhash: " + state.CurrentSource.Hash);
                foreach (var question in review.Questions)
                    section.AddParagraph($"Frage {question.Round}, erfasst {question.RecordedAt:O}, vereinbart bis {question.DueAt:O}: {question.Text} {question.ScheduleBasis}");
                foreach (var reply in review.Submissions)
                {
                    section.AddParagraph($"Eingefrorene Antwort {reply.Round} / {reply.SentAt:O} / simulierte Quittung {reply.SimulatedReceipt}. Scope {reply.ScopeHash}.");
                    section.AddParagraph(reply.Reply.Response);
                    section.AddParagraph(reply.Reply.NoActionRationale);
                    foreach (var source in reply.Reply.Sources)
                        section.AddParagraph($"{source.Kind}:{source.Source.Id}@{source.Source.Version} / SHA-256 {source.Source.Hash}. {source.Reason}");
                }
                foreach (var assessment in review.Assessments)
                {
                    section.AddParagraph($"Explizite Bewertung {assessment.Round} / {assessment.RecordedAt:O}: {assessment.Outcome}. {assessment.Rationale}");
                    foreach (var action in assessment.ActionEvidence)
                        section.AddParagraph($"Damals gepruefte Massnahme {action.Kind}:{action.Source.Id}@{action.Source.Version} / {action.Status} / SHA-256 {action.Source.Hash}.");
                }
                if (review.Draft is not null && review.Status is not ("ReplySubmitted" or "QuestionClosed"))
                {
                    section.AddParagraph("Offener Antwortentwurf / nicht eingereicht");
                    section.AddParagraph(review.Draft.Response);
                    section.AddParagraph(review.Draft.NoActionRationale);
                }
            }
        }
        return RenderDocument(document);
    }

    private static byte[] RenderDocument(Document document)
    {
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
