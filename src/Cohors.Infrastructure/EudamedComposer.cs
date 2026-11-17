using System.Globalization;
using System.Xml.Linq;
using Cohors.Application;
using Cohors.Domain;
using static Cohors.Infrastructure.EudamedXmlContract;

namespace Cohors.Infrastructure;

public sealed class EudamedComposer : IEudamedComposer
{
    private const string Boundary = "SYNTHETIC COHORS PILOT. No official actor registration, authority acceptance or real submission. ";

    public (string Xml, EudamedValidation Validation) Compose(EudamedDossierKind kind, string id,
        EudamedDossierContent content, EudamedBinding binding, EudamedReferences references,
        CalendarProfile calendar, EudamedSource[] sources, EudamedAttachment[] attachments)
    {
        EudamedRules.RequireContent(kind, content);
        EudamedContracts.RequireBinding(binding);
        Guard.Against(references.RegulatoryDevice.Legacy ||
            references.RegulatoryDevice.RiskClass != "III" || !references.RegulatoryDevice.Implantable,
            "eudamed_unsupported_device_regime",
            "Diese Projektion unterstuetzt nur ausdruecklich bestaetigte MDR-Klasse-III-Implantate. Ein Legacy-Kennzeichen belegt weder MDD noch AIMDD.");
        Guard.Against(calendar.Id != content.Jurisdiction || references.ActorIdentity != binding.ActorIdentity ||
            references.ActorApplication != binding.ActorSource || EudamedContracts.Bind(references) != binding.ReferencesSource,
            "eudamed_mapping_scope", "Kalender, Actor und Referenzen muessen der ausdruecklichen Paketbindung entsprechen.", 409);
        foreach (var source in sources)
            Guard.Against(source.Source.Hash != Canonical.HashText(source.Json), "eudamed_source_hash", "Quelloriginal ist nicht hashkonsistent.", 409);
        var type = TypeName(kind);
        var parents = sources.Where(s => s.Kind == "eudamedDossier")
            .Select(s => Canonical.Read<EudamedDossier>(s.Json)).ToArray();
        var parent = content.ParentId is null ? null : parents.SingleOrDefault(p => p.Id == content.ParentId && p.Version == content.ParentVersion)
            ?? throw new DomainException("eudamed_parent", "Die ausgewaehlte Vorfassung fehlt im Quellmanifest.");
        var data = kind switch
        {
            EudamedDossierKind.MIR => Mir(content, calendar, EudamedRules.Source<SafetyCase>(sources, "case"), references),
            EudamedDossierKind.MTR => Mtr(content, EudamedRules.Source<TrendRun>(sources, "trend"), references),
            EudamedDossierKind.PSR => Psr(content, EudamedRules.Source<SummaryAgreement>(sources, "summaryAgreement"), references, binding),
            EudamedDossierKind.PSRP => Psrp(content, EudamedRules.Source<PeriodicSummaryReport>(sources, "summaryReport")),
            EudamedDossierKind.PSUR => Psur(content, EudamedRules.Source<Psur>(sources, "psur"), references, binding, attachments, sources),
            EudamedDossierKind.FSCA => Fsca(content, references, calendar, attachments),
            EudamedDossierKind.FSN => Fsn(content, parents, attachments),
            _ => throw new DomainException("eudamed_kind", "Unbekannter Dossiertyp.")
        };
        var fields = new XElement(Base + "Fields", new XAttribute(Xsi + "type", "v:" + type + "FieldsType"),
            E("mf_srn", references.ActorIdentity), E("mfr", id));
        if (kind is EudamedDossierKind.MIR or EudamedDossierKind.FSCA)
            fields.Add(E("countries", string.Join(",", content.Countries)));
        if (kind is EudamedDossierKind.MIR or EudamedDossierKind.MTR)
            fields.Add(E("ca_srn", binding.Profile.Recipient));
        if (kind == EudamedDossierKind.FSCA) fields.Add(E("cca_srn", binding.Profile.Recipient));
        if (kind == EudamedDossierKind.PSRP)
        {
            Guard.Against(parent?.Kind != EudamedDossierKind.PSR, "eudamed_psrp_parent", "PSRP braucht eine konkret angenommene PSR-Vereinbarung.");
            fields.Add(E("psr_ref", RemoteCode(parent!)));
        }
        var root = new XElement(Base + "Dossier",
            new XAttribute(XNamespace.Xmlns + "v", V1), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(Xsi + "type", "v:DossierType"), new XAttribute("version", "1"),
            new XElement(Base + "DossierExtId", id));
        if (parent?.Kind == kind) root.Add(new XElement(Base + "DossierCode", RemoteCode(parent)));
        root.Add(fields, new XElement(Base + "Data", new XAttribute(Xsi + "type", "v:" + type + "Type"),
            new XAttribute("version", "1"), data));
        var xml = root.ToString(SaveOptions.DisableFormatting);
        var contract = Snapshot.Value;
        var errors = contract.Validate(xml, kind);
        var warnings = new List<string>
        {
            "SYNTHETISCH. Lokale CDM-/XSD-/Katalogpruefung ist keine EUDAMED-Geschaeftsannahme oder Behoerdenentscheidung.",
            "Versionierte .NET-Keyscope-Kompatibilitaetsprojektion: vier Schluessel werden mit identischen Knotenmengen und unveraenderten Referenzen in ihren gemeinsamen Scope gehoben; offizielle XSD-Originale bleiben byteidentisch.",
            "Alle Originalquellen, medizinischen Begruendungen und unsicheren Angaben bleiben im freizugebenden kanonischen Paket.",
            "Fachliche Serverregeln, Rollen, Modulaktivierung und externe Ende-zu-Ende-Abnahme sind getrennte Aktivierungsvoraussetzungen."
        };
        if (kind == EudamedDossierKind.PSR)
            warnings.Add("Expliziter neuer Kalendermonats-Antrag. Die Legacy-Sammelvereinbarung ist Voraussetzungen-Evidenz; ihre Tagesfrequenz wird weder umgerechnet noch geaendert.");
        return (xml, new(EudamedContracts.Business, EudamedContracts.PlaygroundSchema, type + "/1",
            EudamedContracts.Mapping, contract.SchemaFingerprint, contract.EnumerationFingerprint,
            EudamedContracts.SchemaRuntime, contract.RuntimeSchemaFingerprint, errors,
            warnings.ToArray(), "PinnedXsdWithKeyScopeCompatibilityAndPublicEnumerations"));
    }

    public string[] Validate(string xml, EudamedDossierKind kind) => Snapshot.Value.Validate(xml, kind);

    private static XElement Mir(EudamedDossierContent content, CalendarProfile calendar, SafetyCase value, EudamedReferences references)
    {
        var report = value.CurrentReport ?? throw new DomainException("eudamed_mir_source", "MIR-Quellbericht fehlt.");
        var facts = report.Mir?.Facts ?? throw new DomainException("eudamed_mir_facts", "Menschlich belegte MIR-Fakten fehlen.");
        if (facts.EventDateFrom is not { } eventFrom || facts.EventDateTo is not { } eventTo)
            throw new DomainException("eudamed_mir_dates", "Keine Ersetzung unbekannter Ereignisdaten durch Systemeingang.");
        DateOnly LocalDate(DateTimeOffset date) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(date,
            TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime);
        var reportType = report.Kind switch
        { "Initial" => "INITIAL", "FollowUp" => "FOLLOW_UP", "Final" => "FINAL_REPORTABLE_INCIDENT", _ => throw new DomainException("eudamed_mir_kind", "Nicht unterstuetzte MIR-Quellphase.") };
        var classification = facts.EventClassification switch
        {
            "Serious public health threat" => "SERIOUS_PUBLIC_HEALTH_THREAT",
            "Death" => "DEATH",
            "Unanticipated serious deterioration in state of health" => "UNANTICIPATED_SERIOUS_DETERIORATION_OF_HEALTH",
            "All other reportable incidents" => "ALL_OTHER",
            _ => throw new DomainException("eudamed_mir_classification", "Keine automatische Ableitung des Ereignisausgangs aus Dringlichkeit.")
        };
        var dates = E("dateOfIncidentSection", D("startDateofIncident", eventFrom),
            D("endDateofIncident", eventTo), D("mfAwarenessDate", LocalDate(value.FirstAwarenessAt)),
            value.ReportabilityAwarenessAt is { } reportability ? D("mfAwarenessDateReportability", LocalDate(reportability)) : null,
            facts.NextReportDate is { } next ? D("reportNextDate", next) : null, E("eventClassification", classification));
        var device = E("mirDeviceInformation", E("deviceInformation", E("deviceInformationSubHead",
            E("deviceKnownIdentifier", false),
            E("applicableLegislation", "MDR"),
            E("deviceRiskClass", "CLASS_III"),
            E("deviceIDnotInEUDAMED_MasterUDI", E("deviceIDnotInEUDAMED_UDI_Code", references.RegulatoryDevice.UdiDi),
                E("deviceIDnotInEUDAMED_IssuingEnt", references.UdiIssuingAgency)),
            E("deviceNotinEUDAMED_BUDI", E("deviceNotinEUDAMED_BUDI_Code", references.RegulatoryDevice.BasicUdi),
                E("deviceIDnotInEUDAMED_BUDI_IssuingEnt", references.UdiIssuingAgency)),
            O("deviceType", "IMPLANTABLE", 1), E("implantableDevice", true),
            E("deviceModel", references.Device.Model), E("deviceName", references.Device.Name),
            E("deviceNomenclatureDescription_manual", references.Device.IntendedPurpose))));
        var analysis = E("manufacturerAnalysis",
            E("preliminaryCommentsSection", T("preliminaryConclusions", facts.PreliminaryAnalysis),
                T("initialActions", facts.InitialCorrectiveAction), T("furtherInvestigations", facts.FurtherInvestigation)),
            E("conclusionsSection", T("manufacturerConclusion", facts.RootCauses),
                BooleanFact("rootCauseConfirmed", facts.RootCauseConfirmed), BooleanFact("riskReviewed", facts.RiskAssessmentReviewed),
                T("riskNotReviewedReason", facts.RationaleNoRiskReview), BooleanFact("adequateRisk", facts.RiskAssessmentAdequate),
                T("assessmentResultDescription", facts.RiskAssessmentResults),
                E("causeInvestigationIMDRFSection", Code("imdrfCodeAnnexB", facts.InvestigationTypeCode),
                    Code("imdrfCodeAnnexC", facts.InvestigationFindingCode), Code("imdrfCodeAnnexD", facts.InvestigationConclusionCode),
                    Code("imdrfCodeAnnexG", facts.ComponentCode)),
                T("remedialActions", facts.CorrectiveAction), T("causeInvestigationConclusions", facts.FinalComments)));
        if (facts.SimilarIncidents is { } similar)
            analysis.Add(E("similarIncidentsSection", E("similarIncIdentificationOther", similar.Details),
                E("numberDevicesIncidentsperPeriodSection", E("commentsOnHowIncidentsWereDetermined",
                    SimilarNarrative(similar)))));
        return E("mir_2",
            E("administrativeInformation", E("adminInfoSection", E("reportType", reportType),
                T("ncaReportRef", facts.AuthorityReference)), dates, ContactSection(references.Manufacturer.Contact)),
            E("deviceInformationTab", device),
            E("incidentInformation", E("incidentInformationSection", E("natureOfIncident", content.ClinicalRationale)),
                E("medicalDeviceProblem", Code("imdrfCodeAnnexA", facts.DeviceProblemCode),
                    T("deviceLocation", facts.DeviceLocation?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant()))),
            analysis, E("generalComments", E("generalCommentsDescription", Boundary + content.RegulatoryRationale)));
    }

    private static XElement Mtr(EudamedDossierContent content, TrendRun trend, EudamedReferences references)
    {
        var context = content.Mtr!;
        var result = trend.Result;
        var statistics = string.Create(CultureInfo.InvariantCulture,
            $"Product={trend.ProductId}; region={trend.Input.Region}; period={trend.Input.Start:yyyy-MM-dd}..{trend.Input.End:yyyy-MM-dd}; unique events={result.Events}; approved exposure={result.Exposure} {result.Unit}; rate/1000={result.RatePerThousand}; expected events={result.ExpectedEvents}; upper-tail p={result.UpperTailProbability}; method={trend.Input.MethodVersion}; input SHA256={trend.InputHash}.");
        return E("mtr",
            E("administrativeInformation", E("adminInfoSection", E("reportType", content.ParentId is null ? "INITIAL" : "FOLLOW_UP"),
                E("dateOfIncidentTrendSection", D("dateTrendIdentified", context.IdentifiedAt),
                    D("startDateTrendPeriod", trend.Input.Start), D("endDateTrendPeriod", trend.Input.End)),
                E("deviceScopeType", "MD"), E("trendReportBasis", "INCIDENT"), E("trendReportBasisIncreased", "FREQUENCY"),
                E("hasDetectedInvestigation", context.ClinicalInvestigationRelated),
                T("investigationEudamedID", context.ClinicalInvestigationId),
                E("affectedCountrySection", content.Countries.Select(c => E("affectedEuExtendedCountries", c)))),
                ContactSection(references.Manufacturer.Contact)),
            E("deviceInformation", E("deviceInformationSection", E("deviceCategory", "BASIC_UDI")),
                E("basic_udi_Section", Identifier("deviceNotinEUDAMED_BUDI", references.RegulatoryDevice.BasicUdi, references.UdiIssuingAgency, 1)),
                E("mtr_mdr_Section", E("deviceRiskClass_mdr", "CLASS_III"), E("deviceType", "IMPLANTABLE")),
                O("nbNumber", references.RegulatoryDevice.NotifiedBodyNumber, 1)),
            E("trendDetails", E("trendDetailsSection", E("backGroundInformation", statistics + "\n" + content.ClinicalRationale)),
                T("summaryRootCauseAnalysis", trend.Rationale), E("actionsToReduceRisks", content.RegulatoryRationale),
                E("generalComments", Boundary + context.Rationale + "\n" + string.Join("\n", result.Limitations))));
    }

    private static XElement Psr(EudamedDossierContent content, SummaryAgreement agreement, EudamedReferences references, EudamedBinding binding)
    {
        var terms = content.PsrTerms!;
        return E("psr",
            E("administrativeInformation", E("adminInfoSection", E("competentAuthorityCountry", references.Manufacturer.Country),
                E("competentAuthority", binding.Profile.Recipient)), ContactSection(references.Manufacturer.Contact)),
            E("psrInformation", E("psrInfoSection",
                E("psrType", agreement.Prerequisite switch
                { "RootCauseIdentified" => "ROOT_CAUSE", "CommonAndWellDocumented" => "COMMON_WELL_DOCUMENTED_INCIDENT",
                    _ => throw new DomainException("eudamed_psr_prerequisite", "Kein erfundener FSCA-/DSVG-Vereinbarungsgrund.") }),
                E("frequencyOfReporting", terms.FrequencyMonths.ToString(CultureInfo.InvariantCulture)),
                E("frequencyOfReportingRationale", terms.Rationale),
                E("countryCASection", content.Countries.Select((country, index) => E("countryAndCA", new XAttribute("ordinal", index + 1),
                    E("countryAndCASection", E("euCountry", country), E("competentAuthority", binding.Profile.Recipient))))))),
            E("deviceInformation", E("deviceScopeType", "MD"), E("deviceInformationSection", E("deviceCategory", "BASIC_UDI")),
                E("basic_udi_Section", Identifier("deviceEUDAMED_BUDI", references.RegulatoryDevice.BasicUdi, references.UdiIssuingAgency, 1)),
                E("mtr_mdr_Section", E("deviceRiskClass_mdr", "CLASS_III"), E("deviceType", "IMPLANTABLE"))),
            E("manufacturerPSRAnalysis", E("manufacturerAnalysis", E("manufacturerAnalysisSection",
                E("initialProblemBackground", E("preliminaryResultConclusion", content.ClinicalRationale),
                    E("furtherInvestigations", agreement.ContentRequirements)),
                E("descriptionSimilarIncidentsNumberofDevices", agreement.PrerequisiteRationale)))),
            E("generalComments", E("generalCommentsDescription", Boundary + content.RegulatoryRationale)));
    }

    private static XElement Psrp(EudamedDossierContent content, PeriodicSummaryReport value) =>
        E("psrp", E("manufacturerPSRPAnalysis", E("manufacturerAnalysis", E("manufacturerAnalysisSection",
                E("initialProblemBackground", E("preliminaryResultConclusion", value.Content.InvestigationFindings),
                    E("furtherInvestigations", value.Content.AgreementComplianceAndGaps)),
                E("descriptionSimilarIncidentsNumberofDevices",
                    string.Create(CultureInfo.InvariantCulture, $"Explicit period {value.Start:yyyy-MM-dd}..{value.End:yyyy-MM-dd}; {value.Cases.Length} source-bound cases. ") +
                    value.Content.IncidentOverview + "\n" + value.Content.BenefitRiskAndActions)))),
            E("generalComments", E("generalCommentsDescription", Boundary + content.RegulatoryRationale)));

    private static XElement Psur(EudamedDossierContent content, Psur value, EudamedReferences references,
        EudamedBinding binding, EudamedAttachment[] attachments, EudamedSource[] sources)
    {
        var series = value.Snapshot.Series ?? throw new DomainException("eudamed_psur_series", "Kontrollierte PSUR-Serie fehlt.");
        Guard.Against(series.GovernanceSource != references.GovernanceSource,
            "eudamed_psur_governance", "Fuehrende PSUR-Gerate-/Zertifikatsfassung muss den bestaetigten Referenzen entsprechen.", 409);
        var document = attachments.SingleOrDefault(a => a.MediaType == "application/pdf" &&
            a.Source == new VersionReference(value.Id, value.Version, Canonical.Hash(value)))
            ?? throw new DomainException("eudamed_psur_pdf", "Hashgebundene menschenlesbare Fassung des exakten PSUR fehlt.");
        var others = EudamedRules.PsurMemberReferences(value, references, sources);
        return E("psur", E("mfrVersionNumber", series.ReportVersion.ToString(CultureInfo.InvariantCulture)),
            ContactSection(references.Manufacturer.Contact), E("nca", binding.Profile.Recipient),
            E("nbInformationSection", E("nbActorID", references.EvaluatingNotifiedBody)),
            E("psurLeadingDevice", Identifier("leadingDevice", references.RegulatoryDevice.BasicUdi, references.UdiIssuingAgency),
                E("certificateIdentificationSection", Certificate("certificateDetails", references.RegulatoryDevice))),
            E("otherDeviceApplicable", others.Length != 0),
            others.Length == 0 ? null : E("psurDeviceSection", others.Select((member, index) => E("psurDeviceChild",
                new XAttribute("ordinal", index + 1), E("psurDeviceSection",
                    Identifier("psurDevice", member.RegulatoryDevice.BasicUdi, member.UdiIssuingAgency),
                    E("certificateIdentificationSection", Certificate("certificateInformation", member.RegulatoryDevice)))))),
            E("psurDatesSection", D("psurCollectionStartDate", value.Snapshot.Start), D("psurCollectionEndDate", value.Snapshot.End)),
            E("psurDocumentSection", Document("psurDocument", document)));
    }

    private static XElement Fsca(EudamedDossierContent content, EudamedReferences references,
        CalendarProfile calendar, EudamedAttachment[] attachments)
    {
        var value = content.Fsca!;
        var document = attachments.SingleOrDefault(a => a.MediaType == "application/pdf")
            ?? throw new DomainException("eudamed_fsca_pdf", "Der konkrete medizinisch-technische Massnahmenplan braucht eine menschenlesbare PDF-Fassung.");
        return E("fsca_2",
            E("generalInformation", E("generalInformationSection",
                E("reportType", content.IntendedAction == "FollowUp" ? "FSCA_FOLLOW_UP" : "FSCA_INITIAL"),
                content.Countries.Select((country, index) => O("affectedCountry", country, index + 1)), D("dateFSCADecision", value.DecisionDate)),
                E("fscaDecisionBasisSection", E("fscaDecisionOtherSection",
                    E("fscaOtherReferences", "Source-bound CAPA references: " + string.Join(", ", value.CapaIds)))),
                ContactSection(references.Manufacturer.Contact)),
            E("deviceSection", E("deviceScopeType", "MD"),
                E("deviceSectionChildSection", E("fscaDeviceSubtemplate", new XAttribute("ordinal", 1),
                    E("newDeviceSection", Identifier("deviceIdentification", references.RegulatoryDevice.UdiDi, references.UdiIssuingAgency),
                        E("deviceDescriptionAndPurpose", references.Device.IntendedPurpose))))),
            E("fscaCorrectiveActionDescription", E("correctiveActionParentSection", E("correctiveActionDetailsSection",
                E("fscaBackgroundInformation", content.ClinicalRationale), E("fscaHealthHazard", value.HealthHazard),
                E("fscaArisingHazard", value.HazardProbability), E("fscaRootCauseKnown", value.RootCauseStatus),
                E("fscaRootCause", value.RootCauseRationale),
                value.RootCauseStatus == "NO_INV_POS" ? E("fscaCorrectiveActionNotPossible", value.RootCauseRationale) : null,
                E("fscaAffectedDeviceIdentification", references.RegulatoryDevice.UdiDi + "; batches=" + string.Join(",", value.Batches) +
                    "; serial numbers=" + string.Join(",", value.SerialNumbers)),
                value.ManufacturerActionTypes.Select((action, index) => O("fscaMFActionRequired", action, index + 1)),
                value.ManufacturerActionTypes.Contains("OTHER", StringComparer.Ordinal) ? E("mfActionIfOther", value.ManufacturerAction) : null,
                value.UserActionTypes.Select((action, index) => O("fscaUserActionRequired", action, index + 1)),
                value.UserActionTypes.Contains("OTHER", StringComparer.Ordinal) ? E("userActionIfOther", value.UserAction) : null,
                E("fscaCorrectiveActionJustification", content.RegulatoryRationale),
                E("fscaPatientFollow-upLevel", value.PatientFollowUp), Document("DOC1", document, 1))),
                E("subactionsSection", new[] { value.ManufacturerAction, value.UserAction }.Select((action, index) =>
                    E("generalSubactionDescription", new XAttribute("ordinal", index + 1), E("subactionSection",
                        E("fscaSubactionDescription", action),
                        D("fscaGeneralSubactionToBeFinalisedDate", DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.DueAt,
                            TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone)).DateTime)),
                        E("fscageneralSubactionProgress", "Proposed; no completed implementation asserted. Responsible: " + value.ResponsiblePerson)))))),
            E("fscaComments", E("fscageneralCommentsDescription", Boundary +
                $"Responsible: {value.ResponsiblePerson}; due: {value.DueAt:O}; effectiveness: {value.EffectivenessCriterion}. " +
                "CAPA completion is not FSCA acceptance; FSN publication and distribution require separate evidence.")));
    }

    private static XElement Fsn(EudamedDossierContent content, EudamedDossier[] parents, EudamedAttachment[] attachments)
    {
        var value = content.Fsn!;
        var fsca = parents.SingleOrDefault(p => p.Id == value.FscaId && p.Version == value.FscaVersion && p.Kind == EudamedDossierKind.FSCA)
            ?? throw new DomainException("eudamed_fsn_fsca", "Konkrete FSCA-Quelle fehlt.");
        var document = attachments.SingleOrDefault(a => a.Hash == value.DocumentHash && a.Name == value.DocumentName)
            ?? throw new DomainException("eudamed_fsn_document", "Das tatsaechliche FSN-Dokument fehlt.");
        return E("fsn",
            E("fsnAdministrativeInformationSection", E("fsnInformation", E("urgentFsn", value.Urgent ? "YES" : "NO"),
                E("reportType", value.ReportType), D("reportDate", value.DocumentDate))),
            E("fsnDeviceSection", E("fsnFurtherAdvice", value.Instructions),
                E("refNumbersFSCASection", O("linkedFSCAFormDetails", RemoteCode(fsca), 1)),
                E("deviceSection", content.Countries.Select((country, index) => O("affectedCountry", country, index + 1)))),
            content.Countries.Select((country, index) => E("fsnCountrySpecificChildTemplate", new XAttribute("ordinal", index + 1),
                E("fsnCountrySpecificSection", E("referencedCountry_FSN", country), Document("fsnDocumentTranslation", document, 1)))));
    }

    private static XElement Certificate(string name, DeviceRegulatoryProfile device) =>
        E(name, new XAttribute("ordinal", 1), E("certificateIdentificationSection",
            E("nbNumber", device.NotifiedBodyNumber), E("certificateNumber", device.CertificateId)));

    private static XElement ContactSection(EudamedContact contact) =>
        E("submitterInformationSection", E("mfrInformationSection",
            E("mfrContact", E("firstName", contact.FirstName), E("familyName", contact.FamilyName),
                E("electronicMail", contact.Email), E("telephone", contact.Telephone))));

    private static XElement Identifier(string name, string value, string agency, int? ordinal = null) =>
        E(name, ordinal is null ? null : new XAttribute("ordinal", ordinal.Value),
            new XElement(Base + "udiDiCode", value), new XElement(Base + "issuingAgency", agency));

    private static XElement Document(string name, EudamedAttachment value, int? ordinal = null)
    {
        Guard.Against(value.Hash.Length != 64 || value.Name != Path.GetFileName(value.Name) ||
            value.Name.Contains('/') || value.Name.Contains('\\') || value.Name.Contains("..", StringComparison.Ordinal),
            "eudamed_attachment_path", "Nur eine explizit gebundene Datei ohne Pfad-/Traversalanteile.");
        return E(name, ordinal is null ? null : new XAttribute("ordinal", ordinal.Value),
            new XElement(Base + "FileName", value.Name),
            new XElement(Base + "FileURI", "attachments/" + value.Hash + "/" + value.Name));
    }

    private static string RemoteCode(EudamedDossier value) => value.BusinessResponses
        .Where(r => r.Applied && r.Status is "BusinessAccepted" or "DraftCreated" && !string.IsNullOrWhiteSpace(r.DossierCode))
        .OrderByDescending(r => r.Sequence).FirstOrDefault()?.DossierCode
        ?? throw new DomainException("eudamed_remote_reference", "Kein korreliertes Remote-Dossierkennzeichen; Transport-ACK ist keine Geschaeftsannahme.");

    private static string SimilarNarrative(MirSimilarIncidents value) =>
        value.Method + "\n" + value.Geography + "\n" + value.DenominatorBasis + "\n" +
        string.Join("\n", value.Periods.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Start:yyyy-MM-dd}..{p.End:yyyy-MM-dd}: country/regional/world events={p.CountryIncidents}/{p.RegionalIncidents}/{p.WorldwideIncidents}; devices={p.CountryDevices}/{p.RegionalDevices}/{p.WorldwideDevices}; missing={p.MissingDataReason}")));

    private static XElement? BooleanFact(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null :
        E(name, value.Trim().ToUpperInvariant() switch
        { "TRUE" or "YES" or "1" => true, "FALSE" or "NO" or "0" => false,
            _ => throw new DomainException("eudamed_boolean_fact", "Nicht eindeutig belegte boolesche Aussage: " + name) });
    private static XElement? Code(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : O(name, value, 1);
    private static XElement? T(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : E(name, value);
    private static XElement D(string name, DateOnly value) => E(name, value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    private static XElement O(string name, string value, int ordinal) => E(name, new XAttribute("ordinal", ordinal), value);
    private static XElement E(string name, params object?[] values) => new(V1 + name, values);
}
