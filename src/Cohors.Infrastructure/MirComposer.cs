using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class MirComposer : IMirComposer
{
    private static readonly ConcurrentDictionary<string, XmlSchemaSet> Schemas = new(StringComparer.Ordinal);
    private const string Watermark = "SYNTHETIC COHORS PILOT / SIMULATION ONLY / NOT A REAL AUTHORITY SUBMISSION";
    public MirArtifact Build(SafetyCase c, ReportDraft report, Device device, CalendarProfile calendar, MirFacts facts, DateTimeOffset now)
    {
        Guard.Against(!c.Synthetic || report.Jurisdiction == "GB" || calendar.Id != report.Jurisdiction,
            "mir_scope", "MIR-Mapping ausschliesslich fuer synthetische EU-/NI-Pakete.");
        var schema = MirSchemas.For(report.Kind);
        var errors = new List<string>();
        DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime);
        var reportDate = LocalDate(now);
        if (facts.EventDateFrom is null || facts.EventDateTo is null) errors.Add("Belegter Ereigniszeitraum fehlt; kein Ersatz durch Eingangsdatum.");
        if (facts.EventDateFrom > facts.EventDateTo || facts.EventDateTo > LocalDate(c.FirstAwarenessAt))
            errors.Add("Ereigniszeitraum muss vor oder an der ersten Herstellerkenntnis liegen.");
        if (c.OccurredAt is { } occurred && (facts.EventDateFrom > LocalDate(occurred) || facts.EventDateTo < LocalDate(occurred)))
            errors.Add("Ereigniszeitraum widerspricht dem dokumentierten Ereignisinstant.");
        if (report.Kind != "Final" && (facts.NextReportDate is null || facts.NextReportDate <= reportDate))
            errors.Add("Expliziter geplanter Folgeberichtstermin nach diesem Berichtsdatum fehlt.");
        if (report.Kind != "Final" && facts.NextReportDate is { } nextDate && (nextDate < calendar.ValidFrom || nextDate > calendar.ValidTo))
            errors.Add("Zeit-/Kalenderprofil deckt den geplanten Folgeberichtstermin nicht ab.");
        var followUp = report.Kind != "Final" && facts.NextReportDate is { } date && date >= calendar.ValidFrom && date <= calendar.ValidTo
            ? FollowUpRules.Schedule(date, calendar) : null;
        var classificationMatches = c.Seriousness switch
        {
            Seriousness.PublicHealthThreat => facts.EventClassification == "Serious public health threat",
            Seriousness.DeathOrSeriousDeterioration => facts.EventClassification is "Death" or "Unanticipated serious deterioration in state of health",
            _ => facts.EventClassification == "All other reportable incidents"
        };
        if (!classificationMatches) errors.Add("MIR-Klassifikation passt nicht zur dokumentierten Dringlichkeit; Todesfall nicht automatisch aus Sammelkategorie ableiten.");
        if (facts.EvidenceIds.Length == 0 || facts.EvidenceIds.Any(id => !c.Evidence.Any(e => e.Id == id && e.Approved)))
            errors.Add("Konkrete freigegebene Fallbelege fuer menschliche Pflichtangaben fehlen.");
        if (facts.Rationale.Trim().Length < 40) errors.Add("Medizinisch-technische Begruendung einschliesslich Terminologie-/Kodierquelle fehlt.");
        foreach (var item in new[] { ("DeviceProblemCode", facts.DeviceProblemCode), ("DeviceLocation", facts.DeviceLocation),
            ("PreliminaryAnalysis", facts.PreliminaryAnalysis) })
            if (string.IsNullOrWhiteSpace(item.Item2)) errors.Add($"{item.Item1}: belegte Angabe fehlt.");
        if (facts.UdiDi is not null && c.Udi is not null && facts.UdiDi != c.Udi)
            errors.Add("UDI weicht vom Fallstamm ab; Identitaet zuerst kontrolliert klaeren.");
        var previous = c.Submissions.LastOrDefault(s => s.Status == "SimulatedAccepted" && c.Reports.Any(r =>
            r.ScopeHash == s.ReportScopeHash && r.Jurisdiction == report.Jurisdiction));
        if (report.Kind != "Initial" && previous is null) errors.Add("Abgeglichener Vorbericht derselben Jurisdiktion fehlt.");
        if (report.Kind == "Final")
        {
            foreach (var item in new[] { ("UdiDi", facts.UdiDi ?? c.Udi), ("UdiPi", facts.UdiPi), ("UdiEntity", facts.UdiEntity),
                ("BasicUdiEntity", facts.BasicUdiEntity), ("Distribution", facts.Distribution), ("RootCauses", facts.RootCauses),
                ("RootCauseConfirmed", facts.RootCauseConfirmed), ("RiskAssessmentReviewed", facts.RiskAssessmentReviewed),
                ("InvestigationTypeCode", facts.InvestigationTypeCode), ("InvestigationFindingCode", facts.InvestigationFindingCode),
                ("InvestigationConclusionCode", facts.InvestigationConclusionCode), ("ComponentCode", facts.ComponentCode),
                ("CorrectiveAction", facts.CorrectiveAction), ("FinalComments", facts.FinalComments) })
                if (string.IsNullOrWhiteSpace(item.Item2)) errors.Add($"{item.Item1}: belegte Abschlussangabe fehlt.");
        }
        static XElement? E(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(name, value);
        static string? D(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var obligations = ReportRules.CoveredObligations(c, report);
        var reportability = c.Deadlines.Where(d => obligations.Contains(d.Id)).Min(d => d.TriggerAt);
        var xml = new XDocument(new XComment(Watermark), new XElement("incident",
            new XAttribute("version", "7.3.1"), new XAttribute("sCreateTimeStamp", now.ToString("O", CultureInfo.InvariantCulture)),
            new XAttribute("sFormLanguage", report.Language),
            new XElement("admin_info",
                E("ncaName", report.Recipient), report.Kind == "Initial" ? null : E("ncaReportNo", previous?.ReceiptReference),
                E("reportDate", D(reportDate)), E("adverseEventDateFrom", D(facts.EventDateFrom)), E("adverseEventDateTo", D(facts.EventDateTo)),
                E("mfrAwarenessDate", D(LocalDate(c.FirstAwarenessAt))), E("mfrAwarenessReportDate", D(LocalDate(reportability))),
                E("reportType", report.Kind switch { "Initial" => "Initial", "FollowUp" => "Follow up", _ => "Final (Reportable incident)" }),
                report.Kind == "Final" ? null : E("reportNextDate", D(facts.NextReportDate)), E("eventClassification", facts.EventClassification),
                new XElement("contact_info", new XElement("reporterMfr", E("statusReporter", "Manufacturer"))), E("mfrRef", c.Reference)),
            new XElement("device_info",
                E("udiDI", facts.UdiDi ?? c.Udi), E("udiDI_Entity", facts.UdiEntity), E("udiPI", facts.UdiPi),
                E("udiDIBasic", device.BasicUdi), E("Basic_Entity", facts.BasicUdiEntity),
                E("brandName", device.Name), E("deviceDescription", device.IntendedPurpose), E("modelNum", device.Model),
                E("batchNum", c.Batch), report.Kind == "Final" ? new XElement("distribution", E("otherCountries", facts.Distribution)) : null),
            new XElement("incident_info", new XElement("clinical_event_info",
                E("eventDescription", Watermark + "\n" + c.Description), E("imdrfCodeChoice1", facts.DeviceProblemCode),
                E("currentDeviceLocation", facts.DeviceLocation))),
            new XElement("mfr_invest",
                E("manufacturersPrelimAnalysis", facts.PreliminaryAnalysis),
                E("manufacturersInitialCorrecAction", facts.InitialCorrectiveAction), E("furtherInvestigations", facts.FurtherInvestigation),
                E("rootCauses", facts.RootCauses), E("rootCauseConfirmed", facts.RootCauseConfirmed), E("riskAssReviewed", facts.RiskAssessmentReviewed),
                E("imdrfTypeInvestigationCodeChoice1", facts.InvestigationTypeCode), E("imdrfInvestigationFindingsCodeChoice1", facts.InvestigationFindingCode),
                E("imdrfInvestigationConclusionCodeChoice1", facts.InvestigationConclusionCode), E("imdrfComponentCodeChoice1", facts.ComponentCode),
                E("correctiveAction", facts.CorrectiveAction), E("manufacturersFinalComments", facts.FinalComments)),
            new XElement("other_reporting_info", E("AdditionalComments", Watermark + "\nHuman data rationale: " + facts.Rationale))));
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + xml.ToString(SaveOptions.DisableFormatting);
        errors.AddRange(Validate(text, report.Kind));
        return new(report.Kind, schema.File, schema.Hash, "cohors-mir-map-2026.2", text, Canonical.HashText(text), errors.Count == 0,
            errors.Distinct().ToArray(),
            ["XSD-valid ist keine fachliche oder behoerdliche Annahme; nur simulierter Versand.",
             "XSD prueft Laenge/Struktur, nicht die klinische Gueltigkeit von IMDRF-Codes. Menschliche Kodier-/Sprachpruefung bleibt notwendig.",
             "Synthetisches Minimal-Mapping; optional fehlende Angaben sind nicht nachweislich unanwendbar. Original-PDF bleibt unveraendert und geschuetzt."],
            facts, now, true, followUp);
    }

    public string[] Validate(string xml, string kind)
    {
        Guard.Against(xml.Length > 2_000_000, "mir_size", "MIR-Dokument ueberschreitet das Pilotbudget.");
        var schema = Schemas.GetOrAdd(kind, Load);
        var errors = new List<string>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 2_000_000, ValidationType = ValidationType.Schema, Schemas = schema };
        settings.ValidationEventHandler += (_, e) => errors.Add($"XSD {e.Severity}: {e.Message}");
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex) { errors.Add("XML: " + ex.Message); }
        return errors.ToArray();
    }

    private static XmlSchemaSet Load(string kind)
    {
        var info = MirSchemas.For(kind);
        using var stream = typeof(MirComposer).Assembly.GetManifestResourceStream("Cohors.Mir." + info.File)
            ?? throw new InvalidOperationException("Pinned MIR schema resource missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Guard.Against(Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant() != info.Hash,
            "mir_schema_hash", "Das originale MIR-XSD entspricht nicht dem freigegebenen Pin.", 503);
        bytes.Position = 0;
        using var reader = XmlReader.Create(bytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var schemas = new XmlSchemaSet { XmlResolver = null };
        schemas.Add(null, reader);
        schemas.Compile();
        return schemas;
    }
}
