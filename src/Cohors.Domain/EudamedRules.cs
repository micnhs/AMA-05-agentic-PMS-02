namespace Cohors.Domain;

public static class EudamedRules
{
    public static string PrimaryKind(EudamedDossierKind kind) => kind switch
    {
        EudamedDossierKind.MIR => "case",
        EudamedDossierKind.MTR => "trend",
        EudamedDossierKind.PSR => "summaryAgreement",
        EudamedDossierKind.PSRP => "summaryReport",
        EudamedDossierKind.PSUR => "psur",
        EudamedDossierKind.FSCA => "capa",
        EudamedDossierKind.FSN => "eudamedDossier",
        _ => throw new DomainException("eudamed_kind", "Unbekannter Dossiertyp.")
    };

    public static T Source<T>(EudamedSource[] sources, string kind)
    {
        var matches = sources.Where(s => s.Kind == kind).ToArray();
        Guard.Against(matches.Length != 1, "eudamed_source", "Genau eine primaere Quelle des Typs " + kind + " ist erforderlich.");
        var source = matches[0];
        Guard.Against(source.Source.Hash != Canonical.HashText(source.Json), "eudamed_source_hash",
            "Quelloriginal und gebundener Hash stimmen nicht ueberein.", 409);
        return Canonical.Read<T>(source.Json);
    }

    public static void RequireContent(EudamedDossierKind kind, EudamedDossierContent value)
    {
        Guard.Against(!Enum.IsDefined(kind), "eudamed_kind", "Unbekannter Dossiertyp.");
        Guard.Text(value.Title, "Dossiertitel", 10, 250);
        Guard.Text(value.ClinicalRationale, "Medizinisch-technische Einordnung mit Quellen und Unsicherheit", 100, 3999);
        Guard.Text(value.RegulatoryRationale, "Regulatorische Einordnung und konkrete Handlung", 80, 3999);
        Guard.Against(value.Language is not ("de" or "en") ||
            !(value.Jurisdiction?.StartsWith("EU-", StringComparison.Ordinal) == true || value.Jurisdiction == "NI"),
            "eudamed_scope", "Explizite Pilot-Sprache und MDR-Jurisdiktion erforderlich; kein FDA-/GB-Kanal.");
        Guard.Against(value.Countries is not { Length: > 0 and <= 32 } ||
            value.Countries.Any(c => c is not { Length: 2 } || c.Any(ch => ch is < 'A' or > 'Z')) ||
            value.Countries.Distinct(StringComparer.Ordinal).Count() != value.Countries.Length,
            "eudamed_countries", "Eindeutige ausdrueckliche zweistellige Laenderkennungen erforderlich.");
        Guard.Against(value.IntendedAction is not ("Submit" or "DraftOnly" or "FollowUp" or "Reply" or "Revision"),
            "eudamed_action", "Explizite Dossierhandlung erforderlich.");
        Guard.Against((value.ParentId is null) != (value.ParentVersion is null) ||
            value.ParentVersion is < 1, "eudamed_parent", "Eine Vorfassung braucht ID und konkrete Version.");
        if (value.IntendedAction is "FollowUp" or "Reply" or "Revision" || kind == EudamedDossierKind.PSRP)
            Guard.Against(string.IsNullOrWhiteSpace(value.ParentId), "eudamed_parent", "Diese Handlung braucht eine ausdruecklich gebundene Vorfassung.");
        if (value.IntendedAction == "Reply")
            Guard.Text(value.ReplyToCorrespondenceId ?? "", "Originalkorrespondenz", 8, 120);
        else Guard.Against(value.ReplyToCorrespondenceId is not null, "eudamed_reply", "Antwortbezug nur bei einer ausdruecklichen Antwort.");
        if (kind is EudamedDossierKind.MTR or EudamedDossierKind.PSRP or EudamedDossierKind.PSUR)
        {
            Guard.Against(value.Period is null || value.Period.Start > value.Period.End, "eudamed_period", "Konkreter Berichtszeitraum fehlt.");
            Guard.Text(value.Period!.Basis, "Belegte Periodenbasis", 20, 2000);
        }
        if (kind == EudamedDossierKind.PSR)
        {
            var terms = value.PsrTerms ?? throw new DomainException("eudamed_psr_terms",
                "Ausdruecklicher Kalendermonats-Antrag erforderlich; keine Umrechnung der Legacy-Tagesfrequenz.");
            Guard.Against(terms.FrequencyMonths is < 2 or > 12 || terms.FirstPeriodStart == default,
                "eudamed_psr_frequency", "Zwei bis zwoelf ausdruecklich beantragte Kalendermonate und ein Periodenanker erforderlich.");
            Guard.Text(terms.Rationale, "Begruendung des gesonderten PSR-Periodizitaetsantrags", 80, 3999);
        }
        else Guard.Against(value.PsrTerms is not null, "eudamed_content_kind", "PSR-Antragsbedingungen gehoeren nur zu PSR.");
        if (kind == EudamedDossierKind.MTR)
        {
            var mtr = value.Mtr ?? throw new DomainException("eudamed_mtr_context", "Menschlich belegter Trenderkennungs-/Untersuchungskontext fehlt.");
            Guard.Against(mtr.IdentifiedAt == default ||
                mtr.ClinicalInvestigationRelated != !string.IsNullOrWhiteSpace(mtr.ClinicalInvestigationId),
                "eudamed_mtr_context", "Erkennungsdatum und eindeutiger klinischer Untersuchungsbezug erforderlich; keine abgeleitete Negativannahme.");
            Guard.Text(mtr.Rationale, "Beleg des Erkennungszeitpunkts und Untersuchungsbezugs", 80, 3999);
        }
        else Guard.Against(value.Mtr is not null, "eudamed_content_kind", "MTR-Kontext gehoert nur zu MTR.");
        if (kind == EudamedDossierKind.FSCA)
        {
            var fsca = value.Fsca ?? throw new DomainException("eudamed_fsca", "FSCA-Massnahmeninhalt fehlt.");
            Guard.Against(fsca.Batches is null || fsca.SerialNumbers is null ||
                fsca.Batches.Length + fsca.SerialNumbers.Length == 0 ||
                fsca.Batches.Length + fsca.SerialNumbers.Length > 100 ||
                fsca.Batches.Concat(fsca.SerialNumbers).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 100),
                "eudamed_fsca_scope", "Betroffene Chargen/Seriennummern ausdruecklich abgrenzen.");
            Guard.Text(fsca.MeasureType, "FSCA-Massnahmenart", 5, 200);
            Guard.Text(fsca.ManufacturerAction, "Herstellermassnahme und medizinisch-technische Begruendung", 80, 3999);
            Guard.Text(fsca.UserAction, "Konkrete Anwenderhandlung", 80, 3999);
            Guard.Text(fsca.ResponsiblePerson, "Verantwortliche Person/Rolle", 5, 200);
            Guard.Text(fsca.EffectivenessCriterion, "Nachpruefbares Wirksamkeitskriterium", 50, 2000);
            Guard.Text(fsca.HealthHazard, "Gesundheitsgefaehrdung mit klinischer Einordnung", 50, 3999);
            Guard.Text(fsca.HazardProbability, "Auftretenswahrscheinlichkeit einschliesslich Unsicherheit", 50, 3999);
            Guard.Text(fsca.RootCauseRationale, "Quellengestuetzte Ursachen-/Untersuchungseinordnung", 80, 3999);
            Guard.Text(fsca.PatientFollowUp, "Konkreter Patienten-Nachbeobachtungsplan", 80, 3999);
            Guard.Against(fsca.RootCauseStatus is not ("YES" or "NO_INV_ONGOING" or "NO_INV_POS") ||
                fsca.DecisionDate == default || fsca.ManufacturerActionTypes is not { Length: > 0 and <= 7 } ||
                fsca.UserActionTypes is not { Length: > 0 and <= 11 },
                "eudamed_fsca_action_types", "Belegter Entscheidungszeitpunkt, Ursachenstatus und ausdrueckliche Massnahmenkategorien fehlen.");
            Guard.Against(fsca.DueAt == default || fsca.CapaIds is not { Length: > 0 and <= 32 } ||
                fsca.CapaIds.Distinct(StringComparer.Ordinal).Count() != fsca.CapaIds.Length,
                "eudamed_fsca_actions", "Termin und eindeutige explizite CAPA-Verknuepfungen erforderlich; CAPA ist keine FSCA-Freigabe.");
        }
        else Guard.Against(value.Fsca is not null, "eudamed_content_kind", "FSCA-Inhalt gehoert nur zu FSCA.");
        if (kind == EudamedDossierKind.FSN)
        {
            var fsn = value.Fsn ?? throw new DomainException("eudamed_fsn", "FSN-Dokument und Adressaten fehlen.");
            Guard.Text(fsn.FscaId, "Gebundene FSCA", 8, 120);
            Guard.Against(fsn.FscaVersion < 1, "eudamed_fsn_fsca", "Konkrete FSCA-Version erforderlich; FSN-Revisionslinie bleibt davon getrennt.");
            Guard.Text(fsn.Audience, "Betroffener Adressatenkreis", 20, 1000);
            Guard.Text(fsn.Instructions, "Konkrete Sicherheitsanweisung", 80, 3999);
            Guard.Text(fsn.DocumentName, "Dokumentname", 5, 150);
            Guard.Text(fsn.DocumentVersion, "Dokumentfassung", 1, 80);
            Guard.Against(fsn.ReportType is not ("PRELIMINARY" or "FINAL") || fsn.DocumentDate == default,
                "eudamed_fsn_phase", "Dokumentdatum und ausdrueckliche FSN-Berichtsphase erforderlich.");
            Guard.Against(fsn.DocumentHash is not { Length: 64 } ||
                fsn.DocumentHash.Any(c => !Uri.IsHexDigit(c)) || fsn.PublicationState != "Draft",
                "eudamed_fsn_document", "Originalhash erforderlich; ein Entwurf darf keine erfolgte Veroeffentlichung oder Verteilung behaupten.");
        }
        else Guard.Against(value.Fsn is not null, "eudamed_content_kind", "FSN-Inhalt gehoert nur zu FSN.");
    }

    public static void RequirePackage(EudamedPackage package, string? findingDisposition = null)
    {
        EudamedContracts.RequireBinding(package.Binding);
        RequireContent(package.Kind, package.Content);
        Guard.Against(package.Contract != EudamedContracts.Business || package.Revision < 1 ||
            package.Calendar.Id != package.Content.Jurisdiction ||
            package.ProductId != package.References.ProductId || package.Binding.ReferencesSource != EudamedContracts.Bind(package.References) ||
            package.Binding.ActorSource != package.References.ActorApplication || package.Binding.ActorIdentity != package.References.ActorIdentity ||
            package.Simulated != package.Binding.Simulated || package.Sources.Length is < 1 or > 64 ||
            package.BusinessXmlHash != Canonical.HashText(package.BusinessXml) ||
            package.Validation.Contract != EudamedContracts.Business || package.Validation.MappingVersion != EudamedContracts.Mapping ||
            package.Validation.SchemaFingerprint.Length != 64 || package.Validation.EnumerationFingerprint.Length != 64 ||
            package.Validation.SchemaRuntime != EudamedContracts.SchemaRuntime || package.Validation.RuntimeSchemaFingerprint.Length != 64 ||
            package.Validation.Errors.Length != 0,
            "eudamed_package", "Unvollstaendige oder inkonsistente Fach-/Quellen-/Vertragsbindung; keine Freigabe.", 409);
        foreach (var source in package.Sources)
            Guard.Against(source.Source.Hash != Canonical.HashText(source.Json), "eudamed_source_hash", "Quelloriginal wurde veraendert.", 409);
        switch (package.Kind)
        {
            case EudamedDossierKind.MIR:
                var c = Source<SafetyCase>(package.Sources, "case");
                var report = c.CurrentReport ?? throw new DomainException("eudamed_mir_source", "Aktueller MIR-Bericht fehlt.");
                var assessment = c.CurrentAssessment ?? throw new DomainException("eudamed_mir_source", "Aktuelles Assessment fehlt.");
                Guard.Against(!c.Synthetic || c.ProductId != package.ProductId || report.Jurisdiction != package.Content.Jurisdiction ||
                    report.Language != package.Content.Language || report.ReviewReturn is not null ||
                    report.AssessmentVersion != assessment.Version || assessment.EvidenceHash != Canonical.Hash(c.Evidence) ||
                    report.EvidenceHash != assessment.EvidenceHash || report.ScopeHash != ReportRules.Scope(assessment, report),
                    "eudamed_mir_source", "Nur eine konsistente konkrete synthetische MIR-Fassung im selben Sprache-/Jurisdiktionsscope.", 409);
                ReportRules.RequireNoUnknown(c, c.Jurisdiction);
                ReportRules.CoveredObligations(c, report);
                FollowUpRules.Covered(c, report);
                AssessmentRules.RequireReviewable(assessment, report.Preliminary, findingDisposition);
                MirSchemas.RequireValid(report);
                Guard.Against(package.Content.IntendedAction == "FollowUp" && report.Kind == "Initial",
                    "eudamed_mir_followup", "Eine Folgemeldung braucht einen fachlichen Follow-up-/Final-Bericht, keinen erneut etikettierten Initialbericht.");
                break;
            case EudamedDossierKind.MTR:
                var trend = Source<TrendRun>(package.Sources, "trend");
                Guard.Against(trend.ProductId != package.ProductId || trend.InputHash != Canonical.Hash(trend.Input) ||
                    Canonical.Hash(trend.Result) != Canonical.Hash(Statistics.Calculate(trend.Input)) ||
                    trend.Input.CaseVersions is null || trend.Status != "Reviewed",
                    "eudamed_mtr_source", "Deterministischer freigegebener Nenner, Originalquellen, Methodenbindung und medizinische Trendeinordnung erforderlich.", 409);
                RequirePeriod(package.Content, trend.Input.Start, trend.Input.End);
                break;
            case EudamedDossierKind.PSR:
                var agreement = Source<SummaryAgreement>(package.Sources, "summaryAgreement");
                SummaryReportingRules.RequireAgreement(agreement);
                Guard.Against(agreement.ProductId != package.ProductId || agreement.Status != "Active" ||
                    agreement.Language != package.Content.Language || agreement.Calendar.Id != package.Content.Jurisdiction,
                    "eudamed_psr_agreement", "Konkrete aktive Sammelvereinbarung im gebundenen MDR-Scope erforderlich.", 409);
                var terms = package.Content.PsrTerms!;
                Guard.Against(terms.FirstPeriodStart > DateOnly.MaxValue.AddMonths(-terms.FrequencyMonths) ||
                    terms.FirstPeriodStart < agreement.ValidFrom ||
                    terms.FirstPeriodStart.AddMonths(terms.FrequencyMonths) > agreement.ValidThrough,
                    "eudamed_psr_terms_period", "Der explizite erste Kalenderzeitraum muss von der Voraussetzungen-Evidenz gedeckt sein.");
                break;
            case EudamedDossierKind.PSRP:
                var periodic = Source<PeriodicSummaryReport>(package.Sources, "summaryReport");
                SummaryReportingRules.RequireFrozen(periodic);
                Guard.Against(periodic.ProductId != package.ProductId || periodic.Agreement.Language != package.Content.Language ||
                    periodic.Agreement.Calendar.Id != package.Content.Jurisdiction,
                    "eudamed_psrp_scope", "Gebundenes periodisches Analysepaket muss denselben Vereinbarungs-/Sprachscope treffen.");
                RequirePeriod(package.Content, periodic.Start, periodic.End);
                break;
            case EudamedDossierKind.PSUR:
                var psur = Source<Psur>(package.Sources, "psur");
                Guard.Against(!psur.Simulated || psur.ProductId != package.ProductId || psur.SnapshotHash != Canonical.Hash(psur.Snapshot) ||
                    psur.ScopeHash != PortfolioRules.PsurScope(psur) || psur.Snapshot.Series is null ||
                    psur.Findings.Concat(PortfolioRules.CheckPsur(psur.Snapshot, psur.Draft)).Any(f => f.Severity is "Critical" or "Major"),
                    "eudamed_psur_source", "Konsistenter PSUR mit kontrollierter Serie/Periode und ohne offene wesentliche Assurance-Befunde erforderlich.", 409);
                RequirePeriod(package.Content, psur.Snapshot.Start, psur.Snapshot.End);
                _ = PsurMemberReferences(psur, package.References, package.Sources);
                Guard.Against(!package.Attachments.Any(a => a.MediaType == "application/pdf" &&
                    a.Source == new VersionReference(psur.Id, psur.Version, Canonical.Hash(psur))),
                    "eudamed_psur_document", "Der genaue PSUR braucht eine hashgebundene menschenlesbare PDF-Fassung.");
                break;
            case EudamedDossierKind.FSCA:
                var capas = package.Sources.Where(s => s.Kind == "capa").Select(s => Canonical.Read<Capa>(s.Json)).ToArray();
                Guard.Against(capas.Length == 0 || capas.Any(capa => capa.ProductId != package.ProductId) ||
                    !capas.Select(capa => capa.Id).Order(StringComparer.Ordinal)
                        .SequenceEqual(package.Content.Fsca!.CapaIds.Order(StringComparer.Ordinal)),
                    "eudamed_fsca_source", "Jede ausdruecklich verknuepfte CAPA braucht eine eigene unveraenderte Quelle.");
                break;
            case EudamedDossierKind.FSN:
                var fsca = package.Sources.Where(s => s.Kind == "eudamedDossier" && s.Source.Id == package.Content.Fsn!.FscaId)
                    .Select(s => Canonical.Read<EudamedDossier>(s.Json)).SingleOrDefault();
                Guard.Against(fsca is null || fsca.Kind != EudamedDossierKind.FSCA || fsca.ProductId != package.ProductId ||
                    fsca.Version != package.Content.Fsn!.FscaVersion, "eudamed_fsn_source", "Die konkrete freigegebene FSCA fehlt.");
                RequireFrozen(fsca!);
                Guard.Against(!package.Attachments.Any(a => a.Hash == package.Content.Fsn!.DocumentHash &&
                    a.Name == package.Content.Fsn.DocumentName), "eudamed_fsn_document", "Das konkrete FSN-Original fehlt im Paket.");
                break;
        }
    }

    public static void RequireFrozen(EudamedDossier value)
    {
        var medical = value.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == value.ScopeHash);
        var regulatory = value.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == value.ScopeHash);
        RequirePackage(value.Package, medical?.FindingDisposition);
        Guard.Against(value.ReviewStatus != "Frozen" || value.ScopeHash != Canonical.Hash(value.Package) ||
            value.FrozenPackageHash != value.ScopeHash || value.Approvals.Length != 2 || medical is null || regulatory is null ||
            medical.ActorId == regulatory.ActorId, "eudamed_approval", "Zwei getrennte aktuelle Freigaben und identisches eingefrorenes Paket erforderlich.", 409);
    }

    public static EudamedDossier PsrAgreement(EudamedDossierContent content, EudamedSource[] sources)
    {
        var periodic = Source<PeriodicSummaryReport>(sources, "summaryReport");
        var periodicSource = sources.Single(s => s.Kind == "summaryReport").Source;
        var cursor = content;
        var originals = sources;
        for (var depth = 0; depth < 32; depth++)
        {
            var source = originals.SingleOrDefault(s => s.Kind == "eudamedDossier" && s.Source.Id == cursor.ParentId);
            Guard.Against(source is null || source.Source.Version != cursor.ParentVersion ||
                source.Source.Hash != Canonical.HashText(source.Json),
                "eudamed_psrp_parent", "Die konkrete PSRP-/PSR-Originalbindung fehlt oder wurde veraendert.", 409);
            var parent = Canonical.Read<EudamedDossier>(source!.Json);
            Guard.Against(EudamedContracts.Bind(parent) != source.Source || parent.ProductId != periodic.ProductId,
                "eudamed_psrp_parent", "Die gebundene Vorfassung hat einen anderen Original- oder Produktscope.", 409);
            if (cursor.IntendedAction != "Reply")
            {
                Guard.Against(parent.Kind != EudamedDossierKind.PSR, "eudamed_psrp_parent",
                    "Die periodische Analyse braucht ihre konkret gebundene PSR-Vereinbarung.", 409);
                var agreement = Source<SummaryAgreement>(parent.Package.Sources, "summaryAgreement");
                Guard.Against(periodic.AgreementSource != SummaryReportingRules.Bind(agreement) ||
                    Canonical.Hash(periodic.Agreement) != Canonical.Hash(agreement),
                    "eudamed_psrp_agreement", "Die Analyse darf keine fremde oder neuere PSR-Voraussetzungen-Vereinbarung einschieben.", 409);
                return parent;
            }
            Guard.Against(parent.Kind != EudamedDossierKind.PSRP, "eudamed_psrp_parent",
                "Eine PSRP-Antwort adressiert die konkrete PSRP-Korrespondenz, nicht die PSR-Vereinbarung.", 409);
            Guard.Against(parent.Package.Sources.SingleOrDefault(s => s.Kind == "summaryReport")?.Source != periodicSource,
                "eudamed_psrp_reply_source", "Eine Korrespondenzantwort behaelt das exakt gebundene periodische Originalpaket.", 409);
            RequirePeriod(cursor, parent.Package.Content.Period!.Start, parent.Package.Content.Period.End);
            cursor = parent.Package.Content;
            originals = parent.Package.Sources;
        }
        throw new DomainException("eudamed_psrp_parent", "PSRP-Antwortkette ueberschreitet die begrenzte Originalaufloesung.", 409);
    }

    public static IEnumerable<EudamedBusinessResponse> CurrentResponses(EudamedDossier value)
    {
        var binding = Canonical.Hash(value.Package.Binding);
        var attempts = value.Attempts.Where(a => a.DossierRevision == value.Revision &&
            a.PackageHash == value.ScopeHash && Canonical.Hash(a.Binding) == binding)
            .Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return value.BusinessResponses.Where(r => attempts.Contains(r.AttemptId));
    }

    public static void RequireActorScope(EudamedDossier value, Actor actor)
    {
        actor.RequireProduct(value.ProductId);
        foreach (var product in value.AccessProducts) actor.RequireProduct(product);
        foreach (var source in value.Package.Sources)
        {
            if (source.Kind == "psur") PsurGroupRules.RequireScope(Canonical.Read<Psur>(source.Json).Snapshot, actor);
            if (source.Kind == "eudamedDossier") RequireActorScope(Canonical.Read<EudamedDossier>(source.Json), actor);
            if (source.Kind == "eudamedReferences") actor.RequireProduct(Canonical.Read<EudamedReferences>(source.Json).ProductId);
        }
    }

    public static bool CanRead(EudamedDossier value, Actor actor) => actor.Products.Contains(value.ProductId) &&
        value.AccessProducts.All(p => actor.Products.Contains(p, StringComparer.Ordinal)) &&
        value.Package.Sources.All(s => s.Kind switch
        {
            "psur" => PsurGroupRules.CanRead(Canonical.Read<Psur>(s.Json).Snapshot, actor),
            "eudamedDossier" => CanRead(Canonical.Read<EudamedDossier>(s.Json), actor),
            "eudamedReferences" => actor.Products.Contains(Canonical.Read<EudamedReferences>(s.Json).ProductId),
            _ => true
        });

    public static string[] AccessProducts(EudamedPackage package) => new[] { package.ProductId }
        .Concat(package.Sources.SelectMany(s => s.Kind switch
        {
            "psur" => PsurGroupRules.Products(Canonical.Read<Psur>(s.Json).Snapshot),
            "eudamedDossier" => DossierProducts(Canonical.Read<EudamedDossier>(s.Json)),
            "eudamedReferences" => [Canonical.Read<EudamedReferences>(s.Json).ProductId],
            _ => Array.Empty<string>()
        })).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string[] DossierProducts(EudamedDossier value) => value.AccessProducts
        .Concat(AccessProducts(value.Package)).Distinct(StringComparer.Ordinal).ToArray();

    public static EudamedReferences[] PsurMemberReferences(Psur psur, EudamedReferences leading, EudamedSource[] sources)
    {
        var members = psur.Snapshot.Group?.Members.Where(m => m.Governance.ProductId != psur.ProductId).ToArray() ?? [];
        var references = sources.Where(s => s.Kind == "eudamedReferences").Select(s =>
        {
            Guard.Against(s.Source.Hash != Canonical.HashText(s.Json), "eudamed_source_hash", "Gruppenreferenz wurde veraendert.", 409);
            var value = Canonical.Read<EudamedReferences>(s.Json);
            Guard.Against(EudamedContracts.Bind(value) != s.Source, "eudamed_group_reference", "Gruppenreferenz ist nicht versionsgebunden.", 409);
            return value;
        }).ToArray();
        Guard.Against(references.Length != members.Length ||
            references.Select(r => r.ProductId).Distinct(StringComparer.Ordinal).Count() != references.Length,
            "eudamed_group_references", "Jedes weitere PSUR-Gruppenmitglied braucht eine eigene bestaetigte UDI-/Issuer-/Zertifikatszuordnung.", 409);
        return members.Select(member =>
        {
            var reference = references.SingleOrDefault(r => r.ProductId == member.Governance.ProductId);
            Guard.Against(reference is null || reference.GovernanceSource != member.Product.GovernanceSource ||
                Canonical.Hash(reference.RegulatoryDevice) != Canonical.Hash(member.Governance.Device) ||
                reference.ActorApplication != leading.ActorApplication || reference.ActorIdentity != leading.ActorIdentity ||
                Canonical.Hash(reference.Manufacturer) != Canonical.Hash(leading.Manufacturer) ||
                reference.Synthetic != leading.Synthetic,
                "eudamed_group_reference", "Gruppenmitglied muss sein eigenes Geraeteprofil und denselben ausdruecklichen Hersteller-Actor binden.", 409);
            return reference!;
        }).ToArray();
    }

    public static string ObligationKey(EudamedPackage package)
    {
        var source = package.Sources.First(s => s.Kind == PrimaryKind(package.Kind));
        object identity = package.Kind switch
        {
            EudamedDossierKind.MIR => new { Source<SafetyCase>(package.Sources, "case").Id, package.Content.Jurisdiction },
            EudamedDossierKind.MTR => new { Source<TrendRun>(package.Sources, "trend").Input.Region, package.Content.Period!.Start, package.Content.Period.End },
            EudamedDossierKind.PSUR => new { Source<Psur>(package.Sources, "psur").Snapshot.Series!.Reference, package.Content.Period!.Start, package.Content.Period.End },
            EudamedDossierKind.PSRP => new { Source<PeriodicSummaryReport>(package.Sources, "summaryReport").Agreement.Id, package.Content.Period!.Start, package.Content.Period.End },
            EudamedDossierKind.FSN => new { package.Content.Fsn!.FscaId, package.Content.Language },
            EudamedDossierKind.FSCA => new { CaseIds = package.Sources.Where(s => s.Kind == "capa")
                .Select(s => Canonical.Read<Capa>(s.Json).CaseId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
            _ => new { source.Source.Id }
        };
        return Canonical.Hash(new { package.ProductId, package.Kind, package.Binding.Profile.Environment, identity });
    }

    public static bool Pending(EudamedDeliveryEntry value) =>
        value.Attempt.Status is "Queued" or "Started" or "TransmissionUnknown" ||
        (value.Attempt.Status is "TransportAccepted" or "TransportDelivered" &&
            value.BusinessStatus is "Pending" or "PeriodMismatch" or "ActionMismatch");

    public static string DeliveryId(string obligationKey) => "eu-delivery-" + obligationKey;

    public static string MirDeliveryId(string product, string caseId, string jurisdiction) =>
        DeliveryId(Canonical.Hash(new { ProductId = product, Kind = EudamedDossierKind.MIR,
            Environment = EudamedEnvironment.Simulation, identity = new { Id = caseId, Jurisdiction = jurisdiction } }));

    public static void RequireLegacySendAvailable(EudamedDeliveryLedger? ledger, SafetyCase value)
    {
        if (ledger is null) return;
        var report = value.CurrentReport ?? throw new DomainException("report_required", "Bericht fehlt.");
        Guard.Against(ledger.Entries.Any(Pending), "eudamed_delivery_pending",
            "Diese Meldepflicht hat einen laufenden oder ungeklaerten EUDAMED-Versuch. Erst originalgebunden abgleichen, kein Legacy-Zweitversand.", 409);
        Guard.Against(ledger.Entries.Any(e => e.CaseObligation?.ReportScopeHash == report.ScopeHash &&
            e.BusinessStatus is "BusinessAccepted" or "DraftCreated"), "already_submitted",
            "Diese konkrete Fassung wurde bereits als EUDAMED-Dossier verarbeitet; keine zweite Legacy-Einreichung.", 409);
    }

    private static void RequirePeriod(EudamedDossierContent content, DateOnly start, DateOnly end) =>
        Guard.Against(content.Period is null || content.Period.Start != start || content.Period.End != end,
            "eudamed_period_source", "Angegebene Periode widerspricht dem unveraenderten Quellpaket; keine pauschale Drei-Monats-Frist.");
}
