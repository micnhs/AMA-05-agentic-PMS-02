using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohors.Domain;

namespace Cohors.Application;

public static class ReportPrefill
{
    private static readonly JsonSerializerOptions ContactJson = new(Canonical.Json)
    {
        PropertyNameCaseInsensitive = false, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static (ReportPreparation Preparation, MirFacts Facts) Create(SafetyCase c, ReportDraft report,
        Device device, ProductGovernance? governance, CalendarProfile calendar, VigilanceFields texts, string runId,
        DateTimeOffset now, ReportDraft? completing = null)
    {
        var assessment = c.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
        if (governance is not null) GovernanceRules.RequireValid(governance, device);
        var source = new VersionReference(c.Id, c.Version, Canonical.Hash(c));
        var caseReference = $"case:{source.Id}@{source.Version}; SHA-256 {source.Hash}";
        var origins = new Dictionary<string, PreparedReportField>(StringComparer.Ordinal);
        var problems = new List<ReportFieldIssue>();
        MirFacts facts = new();
        void Field(string key, string? value, string origin, string reference, string rationale,
            string[]? claims = null, Citation[]? citations = null) =>
            origins[key] = new(key, value, value is null ? "Missing" : origin, reference, rationale, claims ?? [], citations ?? []);
        void CaseField(string key, string? value, string rationale) => Field(key, value, "LeadingData", caseReference, rationale);
        void Offer(MirFacts values, string origin, string reference, string rationale, Citation[]? citations = null)
        {
            var before = Values(facts);
            var offered = Values(values).Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToArray();
            foreach (var item in offered)
            {
                if (!string.IsNullOrWhiteSpace(before[item.Key]) && before[item.Key] != item.Value)
                    problems.Add(new(item.Key, "ConflictingSources",
                        "Die vorhandene Angabe und die aktuelle belegte Quelle widersprechen sich; nichts wurde still ueberschrieben.",
                        "Den zutreffenden Wert und seine konkrete Quelle klaeren und begruendet korrigieren.",
                        [origins.GetValueOrDefault(item.Key)?.Source ?? caseReference, reference]));
                else Field(item.Key, item.Value, origin, reference, rationale, citations: citations);
            }
            facts = Overlay(facts, values, offered.Where(f => string.IsNullOrWhiteSpace(before[f.Key])).Select(f => f.Key));
        }
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
        var currentMedical = previous?.Mir is not null && previous.Jurisdiction == report.Jurisdiction &&
            previous.State != ReportState.TransmissionUnknown && ReportRules.HasCurrentMedicalApproval(c);
        facts = currentMedical ? previous!.Mir!.Facts : new MirFacts();
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
            var codingSource = $"Kodierung v{coding.Version}; {coding.ReleaseId}; {coding.ReferenceHash}";
            string? SingleCode(string annex, string field)
            {
                var codes = coding.Codes.Where(code => code.Source.Annex == annex && code.Source.CurrentSelectable).ToArray();
                if (codes.Length > 1 && target.MirSupported && (annex == "A" || report.Kind == "Final"))
                    problems.Add(new(field, "Unsupported",
                        "Mehrere aktuelle belegte Codes sind vorhanden; das einzelne Pilotformularfeld wird nicht durch eine willkuerliche Auswahl befuellt.",
                        "Die fuer dieses Formular erforderliche Kodierung explizit menschlich klaeren; die vollstaendige Fallkodierung bleibt erhalten.",
                        [codingSource]));
                return codes.Length == 1 ? codes[0].Source.Code : null;
            }
            Offer(new MirFacts { DeviceProblemCode = SingleCode("A", "deviceProblemCode"), InvestigationTypeCode = SingleCode("B", "investigationTypeCode"),
                InvestigationFindingCode = SingleCode("C", "investigationFindingCode"), InvestigationConclusionCode = SingleCode("D", "investigationConclusionCode"),
                ComponentCode = SingleCode("G", "componentCode") },
                "HumanCoding", codingSource,
                "Expliziter aktueller menschlicher Code, keine erzeugte Kodierung oder neue Bestaetigung einer Untersuchung.", coding.Evidence);
            if (coding.Codes.Any(code => code.Source.Annex == "F" &&
                code.Source.Code == "F02" && code.Source.CurrentSelectable))
            {
                Offer(new MirFacts { EventClassification = "Death" }, "HumanCoding", codingSource,
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
            Offer(new MirFacts { ManufacturerName = governance.Manufacturer.Name, ManufacturerSrn = governance.Manufacturer.Srn,
                ManufacturerCountry = governance.Manufacturer.Country }, "ProductProfile", reference,
                "Gebundenes synthetisches Herstellerprofil; keine reale Registrierungsbestaetigung oder Gleichsetzung mit dem Ereignisland.");
        }
        var contacts = ContactSources(c, report, governance, now, problems);
        if (contacts.Length > 0)
        {
            var contact = contacts[0].Contact;
            Offer(new MirFacts { ContactFirstName = contact.FirstName, ContactLastName = contact.LastName,
                ContactStreet = contact.Street, ContactCity = contact.City, ContactPostcode = contact.Postcode,
                ContactPhone = contact.Phone, ContactEmail = contact.Email, SubmitterIsManufacturerContact = contact.IsSubmitter },
                "ControlledContactEvidence", string.Join("; ", contacts.Select(s => Source(s.Evidence))),
                "Expliziter freigegebener Hersteller-/Melderkontakt im Produkt- und Zielscope; kein Reviewerkonto und keine PRRC-Ableitung. Neuer Human Review bleibt erforderlich.",
                contacts.Select(s => new Citation(s.Evidence.Id, s.Evidence.Hash, s.Evidence.Locator, s.Evidence.Content)).ToArray());
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
        var humanSource = completing ?? (previous?.Jurisdiction == report.Jurisdiction &&
            previous.AssessmentVersion == assessment.Version && previous.EvidenceHash == assessment.EvidenceHash ? previous : null);
        if (humanSource?.Mir is { } existing)
        {
            var retained = Values(existing.Facts).Where(f => !string.IsNullOrWhiteSpace(f.Value) &&
                (completing is not null || f.Key != "nextReportDate" &&
                    humanSource.Preparation?.Fields.Any(p => p.Field == f.Key && p.Origin is "HumanEntry" or "ExistingReportEntry") == true)).ToArray();
            foreach (var field in retained)
            {
                var previousField = humanSource.Preparation?.Fields.SingleOrDefault(f => f.Field == field.Key);
                var proposed = origins[field.Key];
                if (proposed.Value is not null && proposed.Value != field.Value &&
                    field.Key is not ("eventDateFrom" or "eventDateTo") &&
                    proposed.Origin is "LeadingData" or "ProductProfile" or "HumanCoding" or "ControlledContactEvidence")
                    problems.Add(new(field.Key, "ConflictingSources",
                        "Die gespeicherte Angabe bleibt erhalten, weicht aber vom aktuellen belegten Vorschlag ab.",
                        "Widerspruch anhand der beiden Quellen klaeren; danach explizit korrigieren und neu freigeben.",
                        [previousField?.Source ?? $"Bericht v{humanSource.Version}; Scope {humanSource.ScopeHash}", proposed.Source]));
                origins[field.Key] = previousField ?? new(field.Key, field.Value, "ExistingReportEntry",
                    $"Bericht v{humanSource.Version}; Scope {humanSource.ScopeHash}",
                    "Unveraenderte vorhandene Angabe, keine neue automatische Bestaetigung.", [], []);
            }
            if (retained.Length > 0) facts = Overlay(facts, existing.Facts, retained.Select(f => f.Key)) with
            {
                Rationale = existing.Facts.Rationale,
                EvidenceIds = existing.Facts.EvidenceIds.Concat(facts.EvidenceIds).Concat(origins.Values.SelectMany(f => f.Citations).Select(s => s.EvidenceId))
                    .Distinct(StringComparer.Ordinal).ToArray()
            };
        }
        if (!target.MirSupported)
            problems.Add(new("$target", "Unsupported", "Fuer dieses Zielprofil ist nur der simulierte narrative Bericht implementiert, kein vollstaendiges nationales Meldeformular.",
                "Nationale Pflichtangaben, Meldeweg und Formular ausserhalb dieses Pilotprofils eigenstaendig pruefen.", [target.Version]));
        return (new("cohors-report-prefill-2", c.ProductId, source, device, governance, target,
            assessment.Mode, runId, origins.Values.ToArray(), problems.ToArray()), facts);
    }

    public static ReportPreparation Review(ReportPreparation preparation, MirFacts facts, Actor actor, string reason,
        Evidence[] evidence, bool resolveSourceProblems = false)
    {
        var fields = preparation.Fields.Where(f => f.Field.StartsWith('$')).ToList();
        var problems = preparation.Problems?.ToList();
        var source = $"Menschliche Ergaenzung durch {actor.Id}; " + string.Join("; ", evidence
            .Where(e => facts.EvidenceIds.Contains(e.Id)).Select(e => $"{e.Id}@{e.Version}; SHA-256 {e.Hash}; {e.Locator}"));
        foreach (var field in Values(facts))
        {
            var previous = preparation.Fields.SingleOrDefault(f => f.Field == field.Key);
            if (!resolveSourceProblems && previous?.Value is { } before && field.Value is { } after && before != after &&
                previous.Origin is "ControlledContactEvidence" or "ProductProfile" or "HumanCoding")
                problems?.Add(new(field.Key, "ConflictingSources",
                    "Die menschliche Eingabe weicht von der bisher belegten Quelle ab. Der neue Wert bleibt als unbestaetigte Angabe erhalten.",
                    "Die Abweichung anhand konkreter Belege klaeren und ausdruecklich menschlich begruenden.", [previous.Source, source]));
            var explicitlyResolved = resolveSourceProblems && (preparation.Problems ?? []).Any(p => p.Field == field.Key ||
                p.Field == "$contact" && (field.Key.StartsWith("contact", StringComparison.Ordinal) || field.Key == "submitterIsManufacturerContact"));
            fields.Add(previous is not null && previous.Value == field.Value && !explicitlyResolved ? previous : new(field.Key, field.Value,
                field.Value is null ? "Missing" : "HumanEntry", source, reason, [], []));
        }
        return preparation with { Fields = fields.ToArray(), Problems = resolveSourceProblems && problems is not null ? [] : problems?.ToArray() };
    }

    private static string Source(Evidence e) => $"evidence:{e.Id}@{e.Version}; SHA-256 {e.Hash}; {e.Locator}";

    private static (ReportingContactFacts Contact, Evidence Evidence)[] ContactSources(SafetyCase c, ReportDraft report,
        ProductGovernance? governance, DateTimeOffset now, List<ReportFieldIssue> problems)
    {
        var candidates = new List<(ReportingContactFacts Contact, Evidence Evidence)>();
        foreach (var evidence in c.Evidence.Where(e => e.Kind == "ManufacturerContact"))
        {
            EvidenceRules.RequireIntegrity(evidence);
            if (!evidence.Approved || !evidence.Synthetic || evidence.ProductId != c.ProductId ||
                evidence.ValidFrom > now || evidence.ValidTo <= now)
            {
                problems.Add(new("$contact", "MissingSource", "Eine Kontaktquelle liegt vor, ist aber nicht aktuell freigegeben und eindeutig dem synthetischen Produktscope zugeordnet.",
                    "Aktuelle freigegebene Hersteller-/Melderkontaktquelle im richtigen Produktscope bereitstellen.", [Source(evidence)]));
                continue;
            }
            ReportingContactEvidence? document;
            try { document = JsonSerializer.Deserialize<ReportingContactEvidence>(evidence.Content, ContactJson); }
            catch (JsonException)
            {
                problems.Add(new("$contact", "Unsupported", "Die Kontaktquelle hat kein eindeutig lesbares unterstuetztes strukturiertes Format; es wurden keine Namen abgeleitet.",
                    "Explizite Hersteller-/Melderkontaktangaben gemaess cohors-reporting-contact-1 kontrolliert belegen.", [Source(evidence)]));
                continue;
            }
            if (document is null || document.Contract != "cohors-reporting-contact-1" || document.ProductId != c.ProductId ||
                governance is null || document.ManufacturerSrn != governance.Manufacturer.Srn ||
                document.Role != "ManufacturerReportingContact" || document.Contact is null ||
                string.IsNullOrWhiteSpace(document.Contact.ContactId) || document.Jurisdictions is null ||
                !document.Jurisdictions.Contains(report.Jurisdiction, StringComparer.Ordinal))
            {
                problems.Add(new("$contact", "Unsupported", "Hersteller, Produkt, Melderrolle oder Zielscope der Kontaktquelle sind nicht eindeutig passend. PRRC ist nicht automatisch Melderkontakt.",
                    "Den vorgesehenen Hersteller-/Melderkontakt und seine ausdrueckliche Produkt-/Zielzuordnung klaeren.", [Source(evidence)]));
                continue;
            }
            candidates.Add((document.Contact, evidence));
        }
        if (candidates.Select(c => Canonical.Hash(c.Contact)).Distinct(StringComparer.Ordinal).Count() > 1)
            problems.Add(new("$contact", "ConflictingSources", "Mehrere freigegebene Quellen nennen abweichende Kontakte oder Kontaktdaten. Es wurde kein zusammengesetzter Fantasiekontakt erstellt.",
                "Einen gueltigen Hersteller-/Melderkontakt eindeutig belegen und widerspruechliche Quellen kontrolliert klaeren.",
                candidates.Select(c => Source(c.Evidence)).ToArray()));
        return problems.Any(p => p.Field == "$contact") ? [] : candidates.ToArray();
    }

    private static MirFacts Overlay(MirFacts basis, MirFacts values, IEnumerable<string> fields)
    {
        var result = JsonNode.Parse(Canonical.Serialize(basis))?.AsObject() ?? throw new InvalidDataException("MIR-Faktenobjekt fehlt.");
        var source = JsonNode.Parse(Canonical.Serialize(values))?.AsObject() ?? throw new InvalidDataException("MIR-Quellfaktenobjekt fehlt.");
        foreach (var field in fields) result[field] = source[field]?.DeepClone();
        return Canonical.Read<MirFacts>(result.ToJsonString());
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
