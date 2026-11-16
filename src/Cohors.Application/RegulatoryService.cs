using Cohors.Domain;

namespace Cohors.Application;

public sealed class RegulatoryService(IStateStore store, IObjectStore objects, CaseService cases,
    IMirComposer composer, PolicyCatalog policies, TimeProvider clock)
{
    public async Task<SafetyCase> AttachMir(string id, MirCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var c = await cases.Get(id, actor, ct);
        Guard.Text(input.Reason, "MIR-Mapping-/Aenderungsbegruendung", input.ResolveSourceProblems ? 40 : 30);
        var context = new CommandContext(id, "case", c.ProductId, input.ExpectedVersion, key, "MirMappedAndValidated",
            input.Reason, actor, input, clock.GetUtcNow());
        var receipt = await store.Receipt<SafetyCase>(context, ct);
        if (receipt is not null) return receipt;
        Guard.Against(c.Version != input.ExpectedVersion, "version_conflict", "Fallversion geaendert; neu laden.", 409);
        var report = c.CurrentReport ?? throw new DomainException("report_required", "Berichtsentwurf fehlt.");
        ReportRules.RequireNoUnknown(c, report.Jurisdiction);
        Guard.Against(report.State is not (ReportState.Draft or ReportState.MedicalApproved or ReportState.Frozen),
            "report_state", "Bereits versandte Pakete nicht veraendern; eigenen Folgebericht vorbereiten.");
        var assessment = c.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
        Guard.Against(assessment.EvidenceHash != Canonical.Hash(c.Evidence), "assessment_stale", "Aktuelles Assessment erforderlich.", 409);
        context = context with { Dependencies = await cases.ReportDependencies(report, ct) };
        var preparation = report.Preparation is null ? null :
            ReportPrefill.Review(report.Preparation, input.Facts, actor, input.Reason, c.Evidence, input.ResolveSourceProblems);
        var mir = composer.Build(c, report with { Preparation = preparation }, policies.Device(c.ProductId),
            policies.Calendar(report.Jurisdiction), input.Facts, clock.GetUtcNow());
        await objects.Put(mir.Xml, ct);
        return await store.Execute<SafetyCase>(context, value =>
        {
            var current = CaseService.Require(value);
            var next = report with { Version = report.Version + 1, Mir = mir, State = ReportState.Draft, FrozenJson = null, ReviewReturn = null,
                Preparation = preparation,
                AttachmentHashes = current.Evidence.Select(e => e.Hash).Append(mir.XmlHash).Order(StringComparer.Ordinal).ToArray() };
            next = next with { ScopeHash = ReportRules.Scope(assessment, next) };
            return current with { Version = current.Version + 1, Reports = [.. current.Reports.SkipLast(1),
                report with { State = ReportState.Superseded }, next] };
        }, ct);
    }

    public async Task<MirArtifact> Export(string id, Actor actor, CancellationToken ct = default)
    {
        var c = await cases.Get(id, actor, ct);
        var report = c.CurrentReport ?? throw new DomainException("report_required", "Bericht fehlt.");
        MirSchemas.RequireValid(report);
        var artifact = report.Mir ?? throw new DomainException("mir_scope", "GB hat keinen EU-MIR-Export.");
        Guard.Against(report.ScopeHash != ReportRules.Scope(c.CurrentAssessment!, report), "scope_changed", "MIR-Paketbindung geaendert.", 409);
        Guard.Against(composer.Validate(artifact.Xml, artifact.Kind).Length != 0, "mir_invalid", "Erneute XSD-Pruefung fehlgeschlagen.", 409);
        await objects.Read(artifact.XmlHash, ct);
        return artifact;
    }

    public async Task<SummaryAgreement[]> ListSummaryAgreements(Actor actor, CancellationToken ct = default)
    {
        RequireSummaryReader(actor);
        var values = await store.List<SummaryAgreement>("summaryAgreement", actor.Products, ct);
        foreach (var value in values) SummaryReportingRules.RequireAgreement(value);
        return values;
    }

    public async Task<SummaryAgreement> GetSummaryAgreement(string id, Actor actor, CancellationToken ct = default)
    {
        RequireSummaryReader(actor);
        var value = await store.Get<SummaryAgreement>("summaryAgreement", id, ct)
            ?? throw new DomainException("summary_agreement_missing", "Simulierte Sammelvereinbarung fehlt.", 404);
        actor.RequireProduct(value.ProductId);
        SummaryReportingRules.RequireAgreement(value);
        return value;
    }

    public async Task<SummaryAgreement> RecordSummaryAgreement(RecordSummaryAgreementCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        Guard.Text(input.Reference, "Synthetische Vereinbarungsreferenz", 8, 120);
        Guard.Text(input.Jurisdiction, "Vereinbarte Jurisdiktion", 2, 20);
        Guard.Against(input.AuthorityName != PolicyCatalog.Recipient(input.Jurisdiction), "summary_authority",
            "Simulierte Behoerde muss dem freigegebenen Empfaengerprofil dieser Jurisdiktion entsprechen.");
        Guard.Text(input.Reason, "Vereinbarungs-/Aenderungsbegruendung", 80, 4000);
        var id = "summary-agreement-" + Canonical.Hash(new { input.ProductId, input.Reference })[..32];
        var command = new CommandContext(id, "summaryAgreement", input.ProductId, input.ExpectedVersion, key,
            "SummaryAgreementRecorded", input.Reason, actor, input, clock.GetUtcNow());
        if (await store.Receipt<SummaryAgreement>(command, ct) is { } replay) return replay;
        var document = await store.Get<Evidence>("evidence", input.EvidenceId, ct)
            ?? throw new DomainException("summary_prerequisite_source", "Versionierte Voraussetzungen-Evidenz fehlt.", 404);
        Guard.Against(document.Version != input.EvidenceVersion, "version_conflict", "Voraussetzungen-Evidenz wurde geaendert; neu binden.", 409);
        var agreement = new SummaryAgreement(id, input.ExpectedVersion + 1, input.ProductId, input.Reference,
            policies.Device(input.ProductId), input.AuthorityName, policies.Calendar(input.Jurisdiction),
            SummaryReportingRules.Recipient(input.Jurisdiction), input.Language, input.Format, input.AgreementText,
            input.ContentRequirements, input.FrequencyDays, input.ValidFrom, input.ValidThrough, input.AgreedAt,
            input.Prerequisite, input.PrerequisiteRationale, SummaryReportingRules.Bind(document), document,
            "Active", actor.Id, clock.GetUtcNow(), true);
        SummaryReportingRules.RequireAgreement(agreement);
        await objects.Read(document.Hash, ct);
        return await store.Execute<SummaryAgreement>(command
            with { Dependencies = [new("evidence", agreement.EvidenceSource)] }, _ => agreement, ct);
    }

    public async Task<SummaryAgreement> WithdrawSummaryAgreement(string id, VersionCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var agreement = await GetSummaryAgreement(id, actor, ct);
        Guard.Text(input.Reason, "Begruendeter Widerruf der simulierten Vereinbarung", 80, 4000);
        var command = new CommandContext(id, "summaryAgreement", agreement.ProductId, input.ExpectedVersion, key,
            "SummaryAgreementWithdrawn", input.Reason, actor, input, clock.GetUtcNow());
        if (await store.Receipt<SummaryAgreement>(command, ct) is { } replay) return replay;
        return await store.Execute<SummaryAgreement>(command, current =>
        {
            Guard.Against(current?.Status != "Active", "summary_agreement_inactive", "Nur eine aktive Vereinbarung widerrufen.", 409);
            return current! with { Version = current.Version + 1, Status = "Withdrawn" };
        }, ct);
    }

    public async Task<PeriodicSummaryReport[]> ListSummaries(Actor actor, CancellationToken ct = default)
    {
        RequireSummaryReader(actor);
        var values = await store.List<PeriodicSummaryReport>("summaryReport", actor.Products, ct);
        foreach (var value in values) SummaryReportingRules.RequireIntegrity(value);
        return values;
    }

    public async Task<PeriodicSummaryReport> GetSummary(string id, Actor actor, CancellationToken ct = default)
    {
        RequireSummaryReader(actor);
        var value = await store.Get<PeriodicSummaryReport>("summaryReport", id, ct)
            ?? throw new DomainException("summary_missing", "Periodische Sammelmeldung fehlt.", 404);
        actor.RequireProduct(value.ProductId);
        SummaryReportingRules.RequireIntegrity(value);
        return value;
    }

    public async Task<PeriodicSummaryReport> DraftSummary(DraftSummaryCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var agreement = await GetSummaryAgreement(input.AgreementId, actor, ct);
        var id = "summary-" + Canonical.Hash(new { input.AgreementId, input.Start })[..32];
        var command = new CommandContext(id, "summaryReport", agreement.ProductId, input.ExpectedVersion, key,
            "PeriodicSummaryDrafted", input.Reason, actor, input, clock.GetUtcNow());
        if (await store.Receipt<PeriodicSummaryReport>(command, ct) is { } replay) return replay;
        Guard.Text(input.Reason, "Begruendung des eigenstaendigen Sammelpakets", 80, 4000);
        Guard.Against(agreement.Version != input.AgreementVersion, "version_conflict", "Vereinbarungsfassung hat sich geaendert; neu laden.", 409);
        SummaryReportingRules.RequirePeriod(agreement, input.Start, input.End, clock.GetUtcNow());
        SummaryReportingRules.RequireContent(input.Content);
        var selections = input.Cases ?? [];
        Guard.Against(selections.Length is < 1 or > 32 || selections.Any(s => s is null || s.Version < 1) ||
            selections.Select(s => s.CaseId).Distinct(StringComparer.Ordinal).Count() != selections.Length,
            "summary_cases", "Eine bis 32 ausdrueckliche, eindeutige Fallversionen mit je eigener Gemeinsamkeitsbegruendung erforderlich.");
        var sources = new List<SummaryCase>();
        foreach (var selection in selections.OrderBy(s => s.CaseId, StringComparer.Ordinal))
        {
            Guard.Text(selection.SimilarityRationale, "Begruendung dieses aehnlichen Vorkommnisses", 80, 4000);
            var source = await cases.Get(selection.CaseId, actor, ct);
            Guard.Against(source.Version != selection.Version, "version_conflict", "Ausgewaehlte Fallversion hat sich geaendert.", 409);
            SummaryReportingRules.RequireCase(source, agreement, input.Start, input.End);
            sources.Add(new(SummaryReportingRules.Bind(source), source, selection.SimilarityRationale));
        }
        var draft = new PeriodicSummaryReport(id, input.ExpectedVersion + 1, agreement.ProductId,
            "DEMO-SUMMARY-" + id["summary-".Length..], SummaryReportingRules.Bind(agreement), agreement,
            input.Start, input.End, sources.ToArray(), input.Content!, "Draft", "", [], null, null, null, true);
        draft = draft with { ScopeHash = SummaryReportingRules.Scope(draft) };
        var dependencies = await SummaryDependencies(draft, actor, ct);
        return await store.Execute<PeriodicSummaryReport>(command with { Dependencies = dependencies }, current =>
        {
            Guard.Against(current?.Status == "SimulatedAccepted", "summary_immutable",
                "Quittierte Sammelmeldungen bleiben unveraendert; ein neuer Paket-Hash ist keine neue Vereinbarungsperiode.", 409);
            return draft;
        }, ct);
    }

    public async Task<PeriodicSummaryReport> ApproveSummary(string id, VersionCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var report = await GetSummary(id, actor, ct);
        var command = SummaryContext(report, input, actor, key, "PeriodicSummaryApproved");
        if (await store.Receipt<PeriodicSummaryReport>(command, ct) is { } replay) return replay;
        var dependencies = await SummaryDependencies(report, actor, ct);
        return await store.Execute<PeriodicSummaryReport>(command with { Dependencies = dependencies }, current =>
        {
            var value = current ?? throw new DomainException("summary_missing", "Sammelmeldung fehlt.", 404);
            SummaryReportingRules.RequireIntegrity(value);
            Guard.Against(value.Status is not ("Draft" or "MedicalApproved"), "summary_state", "Kein aktueller freizugebender Sammelentwurf.", 409);
            var role = value.Status == "Draft" ? Roles.Medical : Roles.Regulatory;
            actor.RequireHuman(role);
            Guard.Against(value.Approvals.Any(a => a.ActorId == actor.Id), "four_eyes", "Zwei getrennte menschliche Identitaeten erforderlich.", 403);
            Guard.Against(role == Roles.Regulatory && !value.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == value.ScopeHash),
                "medical_required", "Gebundene medizinische Sammelpruefung fehlt.", 409);
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, value.ScopeHash,
                clock.GetUtcNow(), "Synthetischer Sammelinhalt, belegte Voraussetzungen und konkrete Vereinbarung; keine Einzelpflichtbefreiung oder Risikoakzeptanz.",
                input.Reason, null);
            return value with { Version = value.Version + 1, Approvals = [.. value.Approvals, approval],
                Status = role == Roles.Medical ? "MedicalApproved" : "Frozen",
                FrozenJson = role == Roles.Regulatory ? Canonical.Serialize(SummaryReportingRules.Package(value)) : null };
        }, ct);
    }

    public async Task<PeriodicSummaryReport> SimulateSummary(string id, VersionCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var report = await GetSummary(id, actor, ct);
        var command = SummaryContext(report, input, actor, key, "PeriodicSummarySimulated");
        if (await store.Receipt<PeriodicSummaryReport>(command, ct) is { } replay) return replay;
        var dependencies = await SummaryDependencies(report, actor, ct);
        return await store.Execute<PeriodicSummaryReport>(command with { Dependencies = dependencies }, current =>
        {
            var value = current ?? throw new DomainException("summary_missing", "Sammelmeldung fehlt.", 404);
            SummaryReportingRules.RequireFrozen(value);
            Guard.Against(value.Status != "Frozen" || value.SimulatedReceipt is not null,
                "summary_duplicate", "Diese Vereinbarungsperiode ist bereits synthetisch quittiert.", 409);
            return value with { Version = value.Version + 1, Status = "SimulatedAccepted",
                SimulatedReceipt = "DEMO-SUMMARY-RECEIPT-" + value.ScopeHash[..20], SubmittedAt = clock.GetUtcNow() };
        }, ct);
    }

    public async Task<PeriodicSummaryReport> ExportSummary(string id, Actor actor, CancellationToken ct = default)
    {
        var report = await GetSummary(id, actor, ct);
        SummaryReportingRules.RequireFrozen(report);
        await SummaryObjects(report, ct);
        return report;
    }

    private async Task<StoreDependency[]> SummaryDependencies(PeriodicSummaryReport report, Actor actor, CancellationToken ct)
    {
        var agreement = await GetSummaryAgreement(report.Agreement.Id, actor, ct);
        Guard.Against(SummaryReportingRules.Bind(agreement) != report.AgreementSource, "summary_agreement_stale",
            "Vereinbarung wurde geaendert oder widerrufen; Sammelpaket neu binden und freigeben.", 409);
        SummaryReportingRules.RequirePeriod(agreement, report.Start, report.End, clock.GetUtcNow());
        var document = await store.Get<Evidence>("evidence", agreement.Evidence.Id, ct)
            ?? throw new DomainException("summary_prerequisite_source", "Voraussetzungen-Evidenz fehlt.", 409);
        Guard.Against(SummaryReportingRules.Bind(document) != agreement.EvidenceSource,
            "summary_prerequisite_stale", "Voraussetzungen-Evidenz wurde geaendert; Vereinbarung und Bericht explizit neu pruefen.", 409);
        var dependencies = new List<StoreDependency>
        {
            new("summaryAgreement", report.AgreementSource), new("evidence", agreement.EvidenceSource)
        };
        foreach (var source in report.Cases)
        {
            var current = await cases.Get(source.Source.Id, actor, ct);
            Guard.Against(SummaryReportingRules.Bind(current) != source.Source, "summary_case_stale",
                "Gebundener Fall hat sich geaendert; Sammelentwurf und Freigaben muessen erneuert werden.", 409);
            SummaryReportingRules.RequireCase(current, agreement, report.Start, report.End);
            dependencies.Add(new("case", source.Source));
        }
        await SummaryObjects(report, ct);
        return dependencies.ToArray();
    }

    private async Task SummaryObjects(PeriodicSummaryReport report, CancellationToken ct)
    {
        var documents = report.Cases.SelectMany(s => s.Case.Evidence).Append(report.Agreement.Evidence).ToArray();
        foreach (var document in documents) EvidenceRules.RequireIntegrity(document);
        var hashes = documents.Select(d => d.Hash)
            .Concat(report.Cases.SelectMany(s => s.Case.Sources).Select(s => s.Hash))
            .Concat(report.Cases.SelectMany(s => s.Case.Reports).SelectMany(r => r.AttachmentHashes))
            .Distinct(StringComparer.Ordinal);
        foreach (var hash in hashes) await objects.Read(hash, ct);
    }

    private CommandContext SummaryContext(PeriodicSummaryReport report, VersionCommand input, Actor actor, string key, string action)
    {
        Guard.Text(input.Reason, "Begruendete menschliche Sammelentscheidung", 80, 4000);
        return new(report.Id, "summaryReport", report.ProductId, input.ExpectedVersion, key, action,
            input.Reason, actor, input, clock.GetUtcNow());
    }

    private static void RequireSummaryReader(Actor actor) =>
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);
}
