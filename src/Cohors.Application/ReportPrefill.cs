using System.Globalization;
using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Application;

public static class ReportPrefill
{
    public static (ReportPreparation Preparation, MirFacts Facts) Create(SafetyCase c, ReportDraft report,
        Device device, ProductGovernance? governance, CalendarProfile calendar, VigilanceFields texts, string runId,
        DateTimeOffset now)
    {
        var assessment = c.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
        if (governance is not null) GovernanceRules.RequireValid(governance, device);
        var source = new VersionReference(c.Id, c.Version, Canonical.Hash(c));
        var caseReference = $"case:{source.Id}@{source.Version}; SHA-256 {source.Hash}";
        var origins = new Dictionary<string, PreparedReportField>(StringComparer.Ordinal);
        void Field(string key, string? value, string origin, string reference, string rationale,
            string[]? claims = null, Citation[]? citations = null) =>
            origins[key] = new(key, value, value is null ? "Missing" : origin, reference, rationale, claims ?? [], citations ?? []);
        void CaseField(string key, string? value, string rationale) => Field(key, value, "LeadingData", caseReference, rationale);
        string Day(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value,
            TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = ReportTargets.For(report.Jurisdiction);
        CaseField("$reference", c.Reference, "Fuehrende Fallreferenz, kein Behoerdenaktenzeichen.");
        CaseField("$description", c.Description, "Originaler gespeicherter Ereignistext; kein bestaetigter Kausalitaetsnachweis.");
        CaseField("$country", c.Country, "Dokumentiertes Ereignisland; nicht automatisch Herstellerland oder Meldejurisdiktion.");
        CaseField("$firstAwareness", Day(c.FirstAwarenessAt), "Erste Organisationskenntnis, nicht Systemeingang.");
        var covered = ReportRules.CoveredObligations(c, report);
        CaseField("$reportabilityAwareness", Day(c.Deadlines.Where(d => covered.Contains(d.Id)).Min(d => d.TriggerAt)),
            "Aus den gebundenen Einzelpflichten; kein neuer Fristbeginn durch diesen Entwurf.");
        Field("$reportDate", Day(now), "System", $"Berichtsentwurf v{report.Version}", "Erstellungsdatum des Entwurfs, kein behauptetes reales Versanddatum.");
        Field("$authority", target.Authority, "TargetProfile", target.Version, "Fachlich zustaendige Behoerde; getrennt vom Pilotempfaenger.");
        Field("$simulator", target.Simulator, "TargetProfile", target.Version, "Einziger technischer Empfaenger; kein externer Versand.");
        Field("$device", device.Name + " / " + device.Model, "ProductProfile", "PolicyCatalog; SHA-256 " + Canonical.Hash(device), "Gebundenes synthetisches Produktprofil.");
        Field("$basicUdi", device.BasicUdi, "ProductProfile", "PolicyCatalog; SHA-256 " + Canonical.Hash(device), "Basic-UDI der Produktfamilie, keine erfundene UDI der betroffenen Einheit.");

        var previous = c.CurrentReport;
        var currentMedical = previous is not null && previous.Jurisdiction == report.Jurisdiction &&
            previous.State != ReportState.TransmissionUnknown && ReportRules.HasCurrentMedicalApproval(c);
        var facts = currentMedical ? previous!.Mir!.Facts : new MirFacts();
        if (currentMedical)
            foreach (var field in Values(facts))
                if (field.Value is not null)
                    Field(field.Key, field.Value, "PriorMedicalReport", $"Bericht v{previous!.Version}; Scope {previous.ScopeHash}",
                        "Explizite Angabe der bisherigen medizinisch freigegebenen Fassung. Neue Fassung braucht neue Freigaben.");
        var eventDay = c.OccurredAt is { } occurred ? DateOnly.ParseExact(Day(occurred), "yyyy-MM-dd", CultureInfo.InvariantCulture) : (DateOnly?)null;
        facts = facts with
        {
            EventDateFrom = eventDay ?? facts.EventDateFrom,
            EventDateTo = eventDay ?? facts.EventDateTo,
            NextReportDate = null,
            UdiDi = c.Udi ?? facts.UdiDi,
            PreliminaryAnalysis = texts.PreliminaryAnalysis.Text,
            FurtherInvestigation = texts.FurtherInvestigation.Text,
            EvidenceIds = c.Evidence.Select(e => e.Id).ToArray(),
            Rationale = "Vorbefuellter synthetischer Entwurf: fuehrende Fall-/Produktangaben und begruendete " +
                "Assessment-Texte sind getrennt mit Herkunft gebunden. Fehlende Fakten, bestaetigte Ursachen, " +
                "durchgefuehrte Massnahmen und menschliche Termine werden nicht erfunden. Medical Safety und RA pruefen das konkrete Paket."
        };
        if (eventDay is not null)
        {
            CaseField("eventDateFrom", eventDay.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Dokumentierter Ereignisinstant im gebundenen Zielkalender.");
            CaseField("eventDateTo", eventDay.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Gleicher belegter Ereignistag; keine aus Eingang oder Freitext geschaetzte Spanne.");
        }
        if (c.Udi is not null) CaseField("udiDi", c.Udi, "Gespeicherte Fall-UDI; Produktzuordnung und Format bleiben menschlich zu pruefen.");
        CaseField("$batch", c.Batch, "Dokumentierte Charge, nicht automatisch eine vollstaendige UDI-PI.");
        origins.Remove("nextReportDate");
        Field("nextReportDate", null, "Missing", caseReference, "Kein neuer menschlich zugesagter Folgetermin. Die alte Terminzusage wird nicht als neue wiederverwendet.");
        var coding = c.Coding;
        if (coding is { HistoricalAsOf: null } && coding.Evidence.Length > 0 &&
            coding.Evidence.All(citation => EvidenceRules.CitationMatches(citation, c.Evidence)) &&
            !(coding.Findings ?? []).Any(f => f.Severity is "Major" or "Critical"))
        {
            var problems = coding.Codes.Where(code => code.Source.Annex == "A" && code.Source.CurrentSelectable).ToArray();
            if (facts.DeviceProblemCode is null && problems.Length == 1)
            {
                facts = facts with { DeviceProblemCode = problems[0].Source.Code };
                Field("deviceProblemCode", facts.DeviceProblemCode, "HumanCoding", $"Kodierung v{coding.Version}; {coding.ReleaseId}; {coding.ReferenceHash}",
                    "Expliziter aktueller menschlicher Code; kein erzeugter Unknown-/Testcode.", citations: coding.Evidence);
            }
            if (facts.EventClassification is null && coding.Codes.Any(code => code.Source.Annex == "F" &&
                code.Source.Code == "F02" && code.Source.CurrentSelectable))
            {
                facts = facts with { EventClassification = "Death" };
                Field("eventClassification", facts.EventClassification, "HumanCoding", $"Kodierung v{coding.Version}; {coding.ReferenceHash}",
                    "Explizite aktuelle F02-Angabe. Noch keine medizinische Freigabe dieses Meldungspakets.", citations: coding.Evidence);
            }
        }
        if (facts.EventClassification is null && c.Seriousness is not Seriousness.DeathOrSeriousDeterioration)
        {
            facts = facts with { EventClassification = c.Seriousness == Seriousness.PublicHealthThreat
                ? "Serious public health threat" : "All other reportable incidents" };
            CaseField("eventClassification", facts.EventClassification,
                "Vorschlag aus dokumentierter regulatorischer Dringlichkeit, kein Patientenausgang und keine autonome Meldeentscheidung.");
        }
        if (governance is not null)
        {
            var reference = $"productGovernance:{governance.Id}@{governance.Version}; SHA-256 {Canonical.Hash(governance)}";
            facts = facts with { ManufacturerName = governance.Manufacturer.Name, ManufacturerSrn = governance.Manufacturer.Srn,
                ManufacturerCountry = governance.Manufacturer.Country };
            Field("manufacturerName", facts.ManufacturerName, "ProductProfile", reference, "Fuehrendes synthetisches Herstellerprofil.");
            Field("manufacturerSrn", facts.ManufacturerSrn, "ProductProfile", reference, "Gespeicherte synthetische SRN, keine reale Registrierungsbestaetigung.");
            Field("manufacturerCountry", facts.ManufacturerCountry, "ProductProfile", reference, "Herstellerland, nicht automatisch Ereignisland.");
        }
        foreach (var item in new[] { ("preliminaryAnalysis", texts.PreliminaryAnalysis), ("furtherInvestigation", texts.FurtherInvestigation) })
        {
            var claims = assessment.Content.Claims.Where(claim => item.Item2.ClaimIds.Contains(claim.Id)).ToArray();
            Field(item.Item1, item.Item2.Text, assessment.Mode == "Human" ? "AssessmentDraft" : "AgentDraft",
                $"Assessment v{assessment.Version}; Autorenlauf {runId}; Modus {assessment.Mode}",
                "Begruendeter Vorschlag mit Originalfundstellen, kein bestaetigter Befund oder ausgefuehrte Massnahme.",
                item.Item2.ClaimIds, claims.SelectMany(claim => claim.Citations).Distinct().ToArray());
        }
        foreach (var field in Values(facts))
            if (!origins.ContainsKey(field.Key))
                Field(field.Key, field.Value, "Missing", caseReference, "Kein eigenstaendiger strukturierter Beleg. Nicht aus Freitext oder Dringlichkeit ergaenzt.");
        return (new("cohors-report-prefill-1", c.ProductId, source, device, governance, target,
            assessment.Mode, runId, origins.Values.ToArray()), facts);
    }

    public static ReportPreparation Review(ReportPreparation preparation, MirFacts facts, Actor actor, string reason,
        Evidence[] evidence)
    {
        var fields = preparation.Fields.Where(f => f.Field.StartsWith('$')).ToList();
        var source = $"Menschliche Ergaenzung durch {actor.Id}; " + string.Join("; ", evidence
            .Where(e => facts.EvidenceIds.Contains(e.Id)).Select(e => $"{e.Id}@{e.Version}; SHA-256 {e.Hash}; {e.Locator}"));
        foreach (var field in Values(facts))
        {
            var previous = preparation.Fields.SingleOrDefault(f => f.Field == field.Key);
            fields.Add(previous is not null && previous.Value == field.Value ? previous : new(field.Key, field.Value,
                field.Value is null ? "Missing" : "HumanEntry", source, reason, [], []));
        }
        return preparation with { Fields = fields.ToArray() };
    }

    private static Dictionary<string, string?> Values(MirFacts facts)
    {
        using var document = JsonDocument.Parse(Canonical.Serialize(facts));
        return typeof(MirFacts).GetProperties().Where(p => p.Name is not ("EvidenceIds" or "Rationale"))
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p =>
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }, StringComparer.Ordinal);
    }
}
