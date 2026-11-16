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
        if (facts is null || facts.EvidenceIds is null || facts.Rationale is null)
            throw new DomainException("mir_facts", "MIR-Fakten, Belegliste und Begruendung muessen explizit vorliegen; keine stillen Standardwerte.");
        Guard.Against(!c.Synthetic || report.Jurisdiction == "GB" || calendar.Id != report.Jurisdiction,
            "mir_scope", "MIR-Mapping ausschliesslich fuer synthetische EU-/NI-Pakete.");
        ReportTargets.RequireBound(report);
        var target = report.Preparation?.Target ?? ReportTargets.For(report.Jurisdiction);
        var schema = MirSchemas.For(report.Kind);
        var errors = new List<string>();
        DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime);
        var reportDate = LocalDate(now);
        if (facts.EventDateFrom is null || facts.EventDateTo is null || facts.EventDateFrom == DateOnly.MinValue || facts.EventDateTo == DateOnly.MinValue)
            errors.Add("Belegter Ereigniszeitraum fehlt; kein Ersatz durch Eingangsdatum.");
        if (facts.EventDateFrom > facts.EventDateTo || facts.EventDateTo > LocalDate(c.FirstAwarenessAt))
            errors.Add("Ereigniszeitraum muss vor oder an der ersten Herstellerkenntnis liegen.");
        if (c.OccurredAt is { } occurred && (facts.EventDateFrom > LocalDate(occurred) || facts.EventDateTo < LocalDate(occurred)))
            errors.Add("Ereigniszeitraum widerspricht dem dokumentierten Ereignisinstant.");
        if (c.OccurredAt is null && facts.EventDateFrom is not null && facts.EventDateTo is not null &&
            string.IsNullOrWhiteSpace(facts.EventDateRationale))
            errors.Add("Ersatzweise belegter Ereigniszeitraum braucht eine eigene Begruendung; Systemeingang ist kein Ereignisdatum.");
        if (report.Kind != "Final" && (facts.NextReportDate is null || facts.NextReportDate <= reportDate))
            errors.Add("Expliziter geplanter Folgeberichtstermin nach diesem Berichtsdatum fehlt.");
        if (report.Kind != "Final" && facts.NextReportDate is { } nextDate && (nextDate < calendar.ValidFrom || nextDate > calendar.ValidTo))
            errors.Add("Zeit-/Kalenderprofil deckt den geplanten Folgeberichtstermin nicht ab.");
        var followUp = report.Kind != "Final" && facts.NextReportDate is { } date && date > reportDate && date >= calendar.ValidFrom && date <= calendar.ValidTo
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
        foreach (var item in new[] { ("ManufacturerName", facts.ManufacturerName), ("ManufacturerSrn", facts.ManufacturerSrn),
            ("ManufacturerCountry", facts.ManufacturerCountry), ("ContactFirstName", facts.ContactFirstName),
            ("ContactLastName", facts.ContactLastName), ("ContactStreet", facts.ContactStreet), ("ContactCity", facts.ContactCity),
            ("ContactPostcode", facts.ContactPostcode), ("ContactPhone", facts.ContactPhone), ("ContactEmail", facts.ContactEmail) })
            if (string.IsNullOrWhiteSpace(item.Item2)) errors.Add($"{item.Item1}: belegte Hersteller-/Kontaktangabe fehlt.");
        if (facts.SubmitterIsManufacturerContact != true)
            errors.Add("SubmitterIsManufacturerContact: ausdruecklich bestaetigen, dass dieser belegte Herstellerkontakt auch meldet. Andere Melder-/AR-Konstellationen sind in diesem Pilotprofil nicht implementiert.");
        if (report.Preparation?.Governance is { } governance &&
            (facts.ManufacturerName != governance.Manufacturer.Name || facts.ManufacturerSrn != governance.Manufacturer.Srn ||
                facts.ManufacturerCountry != governance.Manufacturer.Country))
            errors.Add("Herstellerangaben weichen vom gebundenen fuehrenden Produktprofil ab; Quelle klaeren statt Pflichtdaten ueberschreiben.");
        foreach (var item in new[] { ("DeviceProblemCode", facts.DeviceProblemCode), ("DeviceLocation", facts.DeviceLocation),
            ("PreliminaryAnalysis", facts.PreliminaryAnalysis) })
            if (string.IsNullOrWhiteSpace(item.Item2)) errors.Add($"{item.Item1}: belegte Angabe fehlt.");
        if (report.Kind == "FollowUp")
            foreach (var item in new[] { ("InitialCorrectiveAction", facts.InitialCorrectiveAction), ("FurtherInvestigation", facts.FurtherInvestigation) })
                if (string.IsNullOrWhiteSpace(item.Item2))
                    errors.Add($"{item.Item1}: belegter Massnahmen-/Untersuchungsstand der Folgemeldung fehlt; keine Durchfuehrung erfinden.");
        if (facts.UdiDi is not null && c.Udi is not null && facts.UdiDi != c.Udi)
            errors.Add("UDI weicht vom Fallstamm ab; Identitaet zuerst kontrolliert klaeren.");
        var previous = c.Submissions.LastOrDefault(s => s.Status == "SimulatedAccepted" && c.Reports.Any(r =>
            r.ScopeHash == s.ReportScopeHash && r.Jurisdiction == report.Jurisdiction));
        if (report.Kind != "Initial" && previous is null) errors.Add("Abgeglichener Vorbericht derselben Jurisdiktion fehlt.");
        if (report.Kind != "Initial")
        {
            if (string.IsNullOrWhiteSpace(facts.AuthorityReference))
                errors.Add("AuthorityReference: NCA-Aktenzeichen oder ausdruecklich begruendetes Unknown fehlt. Eine Simulatorquittung ist kein Behoerdenaktenzeichen.");
            if (facts.AuthorityReference?.Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true &&
                (facts.AuthorityReferenceRationale?.Trim().Length ?? 0) < 40)
                errors.Add("AuthorityReferenceRationale: Unknown ist nur mit dokumentierter konkreter Begruendung zulaessig.");
            if (facts.AuthorityReference is not null && c.Submissions.Any(s => s.ReceiptReference == facts.AuthorityReference))
                errors.Add("AuthorityReference: interne Simulatorquittung darf nicht als NCA-Aktenzeichen ausgegeben werden.");
        }
        if (report.Kind == "Final")
        {
            foreach (var item in new[] { ("UdiDi", facts.UdiDi ?? c.Udi), ("UdiPi", facts.UdiPi), ("UdiEntity", facts.UdiEntity),
                ("BasicUdiEntity", facts.BasicUdiEntity), ("Distribution", facts.Distribution), ("RootCauses", facts.RootCauses),
                ("RootCauseConfirmed", facts.RootCauseConfirmed), ("RiskAssessmentReviewed", facts.RiskAssessmentReviewed),
                ("InvestigationTypeCode", facts.InvestigationTypeCode), ("InvestigationFindingCode", facts.InvestigationFindingCode),
                ("InvestigationConclusionCode", facts.InvestigationConclusionCode), ("ComponentCode", facts.ComponentCode),
                ("CorrectiveAction", facts.CorrectiveAction), ("FinalComments", facts.FinalComments) })
                if (string.IsNullOrWhiteSpace(item.Item2)) errors.Add($"{item.Item1}: belegte Abschlussangabe fehlt.");
            if (facts.RiskAssessmentReviewed == "No" && string.IsNullOrWhiteSpace(facts.RationaleNoRiskReview))
                errors.Add("RationaleNoRiskReview: eine nicht erfolgte Risikopruefung braucht ihre konkrete Begruendung.");
            if (facts.RiskAssessmentReviewed == "Yes" && (facts.RiskAssessmentAdequate is not ("Yes" or "No") ||
                string.IsNullOrWhiteSpace(facts.RiskAssessmentResults)))
                errors.Add("RiskAssessmentAdequate / RiskAssessmentResults: bestaetigte Risikopruefung braucht Ergebnis und Angemessenheitsbewertung.");
            if (facts.SimilarIncidents is null)
                errors.Add("SimilarIncidents: Auswahlmethode, Perioden, Geografien und Nennerbasis fehlen; unbekannte Zahlen sind nicht Null.");
        }
        CheckSimilar(facts.SimilarIncidents, reportDate, errors);
        static XElement? E(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(name, value);
        static string? D(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var obligations = ReportRules.CoveredObligations(c, report);
        var reportability = c.Deadlines.Where(d => obligations.Contains(d.Id)).Min(d => d.TriggerAt);
        var xml = new XDocument(new XComment(Watermark), new XElement("incident",
            new XAttribute("version", "7.3.1"), new XAttribute("sCreateTimeStamp", now.ToString("O", CultureInfo.InvariantCulture)),
            new XAttribute("sFormLanguage", report.Language),
            new XElement("admin_info",
                E("ncaName", target.Authority), E("ncaReportNo", facts.AuthorityReference),
                E("reportDate", D(reportDate)), E("adverseEventDateFrom", D(facts.EventDateFrom)), E("adverseEventDateTo", D(facts.EventDateTo)),
                E("mfrAwarenessDate", D(LocalDate(c.FirstAwarenessAt))), E("mfrAwarenessReportDate", D(LocalDate(reportability))),
                E("reportType", report.Kind switch { "Initial" => "Initial", "FollowUp" => "Follow up", _ => "Final (Reportable incident)" }),
                report.Kind == "Final" ? null : E("reportNextDate", D(facts.NextReportDate)), E("eventClassification", facts.EventClassification),
                new XElement("contact_info", new XElement("reporterMfr", E("statusReporter", "Manufacturer"),
                    facts.SubmitterIsManufacturerContact == true ? new XElement("reporterDetails",
                        E("reporterOrgName", facts.ManufacturerName), E("reporterContactPersonFirstName", facts.ContactFirstName),
                        E("reporterContactPersonSecondName", facts.ContactLastName), E("reporterOrgStreet", facts.ContactStreet),
                        E("reporterOrgCity", facts.ContactCity), E("reporterOrgCountry", facts.ManufacturerCountry),
                        E("reporterOrgPostcode", facts.ContactPostcode), E("reporterOrgPhone", facts.ContactPhone),
                        E("reporterOrgEmailAddress", facts.ContactEmail)) : null,
                    new XElement("mfrDetails", E("mfrName", facts.ManufacturerName), E("mfrSRN", facts.ManufacturerSrn),
                        E("mfrContactPersonFirstName", facts.ContactFirstName), E("mfrContactPersonSecondName", facts.ContactLastName),
                        E("mfrOrgStreet", facts.ContactStreet), E("mfrCity", facts.ContactCity), E("mfrCountry", facts.ManufacturerCountry),
                        E("mfrPostcode", facts.ContactPostcode), E("mfrPhone", facts.ContactPhone), E("mfrEmailAddress", facts.ContactEmail)))),
                E("mfrRef", c.Reference)),
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
                E("rationaleNoReview", facts.RationaleNoRiskReview), E("riskAssAdequate", facts.RiskAssessmentAdequate),
                E("riskAssResults", facts.RiskAssessmentResults),
                E("imdrfTypeInvestigationCodeChoice1", facts.InvestigationTypeCode), E("imdrfInvestigationFindingsCodeChoice1", facts.InvestigationFindingCode),
                E("imdrfInvestigationConclusionCodeChoice1", facts.InvestigationConclusionCode), E("imdrfComponentCodeChoice1", facts.ComponentCode),
                E("correctiveAction", facts.CorrectiveAction), E("manufacturersFinalComments", facts.FinalComments),
                SimilarXml(facts.SimilarIncidents)),
            new XElement("other_reporting_info", E("AdditionalComments", Watermark + "\nMapping rationale, not approval: " + facts.Rationale +
                (facts.EventDateRationale is null ? "" : "\nEvent date basis: " + facts.EventDateRationale) +
                (facts.AuthorityReferenceRationale is null ? "" : "\nNCA reference basis: " + facts.AuthorityReferenceRationale)))));
        foreach (var item in xml.Descendants().Where(e => new[] { "eventDescription", "manufacturersPrelimAnalysis",
            "manufacturersInitialCorrecAction", "furtherInvestigations", "rootCauses", "rationaleNoReview", "riskAssResults",
            "correctiveAction", "manufacturersFinalComments", "similarVariantDetails", "AdditionalComments" }.Contains(e.Name.LocalName)))
            if (item.Value.Length > 4000) errors.Add($"{item.Name.LocalName}: {item.Value.Length} Zeichen; maximal 4000 in diesem MIR-Feld. Keine stille Kuerzung.");
        foreach (var item in xml.Descendants().Where(e => e.Name.LocalName == "howWereDetermined"))
            if (item.Value.Length > 2000) errors.Add("howWereDetermined: maximal 2000 Zeichen in diesem Schemafeld; keine stille Kuerzung.");
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + xml.ToString(SaveOptions.DisableFormatting);
        var schemaErrors = Validate(text, report.Kind);
        return new(report.Kind, schema.File, schema.Hash, "cohors-mir-map-2026.3", text, Canonical.HashText(text), errors.Count == 0 && schemaErrors.Length == 0,
            errors.Concat(schemaErrors).Distinct().ToArray(),
            ["XSD-valid ist keine fachliche oder behoerdliche Annahme; nur simulierter Versand.",
             "XSD prueft Laenge/Struktur, nicht die klinische Gueltigkeit von IMDRF-Codes. Menschliche Kodier-/Sprachpruefung bleibt notwendig.",
             "Synthetisches Minimal-Mapping; optional fehlende Angaben sind nicht nachweislich unanwendbar. Original-PDF bleibt unveraendert und geschuetzt."],
            facts, now, true, followUp, new(schemaErrors.Length == 0, errors.Distinct().ToArray(),
                "Gepruefte Pilot-Pflichtdaten und implementierte Bedingungen, nicht automatisch klinische Wahrheit oder eine vollstaendige nationale Pflichtfeldmatrix.",
                false, target.ExternalRequirements));
    }

    private static void CheckSimilar(MirSimilarIncidents? data, DateOnly reportDate, List<string> errors)
    {
        if (data is null) return;
        if (string.IsNullOrWhiteSpace(data.Variant) || string.IsNullOrWhiteSpace(data.Details) ||
            string.IsNullOrWhiteSpace(data.DenominatorBasis) || string.IsNullOrWhiteSpace(data.Method) ||
            string.IsNullOrWhiteSpace(data.Geography) ||
            data.DenominatorBasis == "Other" && string.IsNullOrWhiteSpace(data.OtherDenominator))
            errors.Add("SimilarIncidents: Modell-/Auswahlreichweite, Methode, Geografien und konkrete Nennerbasis erforderlich.");
        if (data.Periods is not { Length: > 0 and <= 4 })
        {
            errors.Add("SimilarIncidents: eine bis vier explizite Perioden (N, N-1, N-2, N-3) erforderlich.");
            return;
        }
        for (var i = 0; i < data.Periods.Length; i++)
        {
            var p = data.Periods[i];
            if (p is null)
            {
                errors.Add("SimilarIncidents: leere Periodenangabe ist kein nachvollziehbarer Zeitraum.");
                continue;
            }
            if (p.Start == DateOnly.MinValue || p.End == DateOnly.MinValue || p.Start > p.End || p.End > reportDate ||
                i > 0 && data.Periods[i - 1] is { } later && p.End >= later.Start)
                errors.Add("SimilarIncidents: abgeschlossene, nicht ueberlappende Perioden in absteigender Folge erforderlich.");
            int?[] counts = [p.CountryIncidents, p.RegionalIncidents, p.WorldwideIncidents, p.CountryDevices, p.RegionalDevices, p.WorldwideDevices];
            if (counts.Any(n => n < 0)) errors.Add("SimilarIncidents: negative Zaehler/Nenner sind unzulaessig.");
            if (counts.Any(n => n is null) && (p.MissingDataReason?.Trim().Length ?? 0) < 40)
                errors.Add("SimilarIncidents: fehlende Zahlen brauchen eine ausdrueckliche Datenlueckenbegruendung; kein Null-Ersatz.");
            if (p.WorldwideIncidents < p.CountryIncidents || p.WorldwideIncidents < p.RegionalIncidents ||
                p.WorldwideDevices < p.CountryDevices || p.WorldwideDevices < p.RegionalDevices)
                errors.Add("SimilarIncidents: weltweiter Bestand schliesst die regionalen Teilbestaende ein; nicht addieren.");
        }
    }

    private static XElement? SimilarXml(MirSimilarIncidents? data)
    {
        if (data is null) return null;
        static XElement? E(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new(name, value);
        static XElement? N(string name, int? value) => value is null ? null : new(name, value);
        return new("similarIncidents", E("similarVariant", data.Variant), E("similarVariantDetails", data.Details),
            E("numberBasedOn", data.DenominatorBasis), E("numberBasedOnOther", data.OtherDenominator),
            (data.Periods ?? []).Select((p, i) => p is null ? null : new XElement(i == 0 ? "timePeriodN" : $"timePeriodN-{i}",
                E("startDate", p.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), E("endDate", p.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                N("countrySimInc", p.CountryIncidents), N("eeaChTrSimInc", p.RegionalIncidents), N("worldSimInc", p.WorldwideIncidents),
                N("countrySimDev", p.CountryDevices), N("eeaChTrSimDev", p.RegionalDevices), N("worldSimDev", p.WorldwideDevices))),
            E("howWereDetermined", data.Method + "\nGeographic scope: " + data.Geography + "\n" +
                string.Join("\n", (data.Periods ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.MissingDataReason))
                    .Select(p => $"{p.Start:yyyy-MM-dd}/{p.End:yyyy-MM-dd}: {p.MissingDataReason}"))));
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
