using Cohors.Domain;

namespace Cohors.Application;

public sealed class RegulatoryService(IStateStore store, IObjectStore objects, CaseService cases,
    IMirComposer composer, PolicyCatalog policies, TimeProvider clock)
{
    public async Task<SafetyCase> AttachMir(string id, MirCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await cases.Get(id, actor, ct);
        Guard.Text(input.Reason, "MIR-Mapping-/Aenderungsbegruendung", 30);
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
        var mir = composer.Build(c, report, policies.Device(c.ProductId), policies.Calendar(report.Jurisdiction), input.Facts, clock.GetUtcNow());
        await objects.Put(mir.Xml, ct);
        return await store.Execute<SafetyCase>(context, value =>
        {
            var current = CaseService.Require(value);
            var next = report with { Version = report.Version + 1, Mir = mir, State = ReportState.Draft, FrozenJson = null,
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
}
