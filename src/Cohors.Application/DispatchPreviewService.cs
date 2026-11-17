using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class CaseService
{
    public async Task<DispatchPreview> PreviewSubmission(string id, int version, Actor actor, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var value = await Get(id, actor, ct);
        Guard.Against(value.Version != version, "version_conflict", "Ausgewaehlte Fallfassung ist nicht mehr aktuell.", 409);
        ReportRules.RequireSendable(value, actor);
        await ReportDependencies(value.CurrentReport, ct);
        var report = Canonical.Read<ReportDraft>(value.CurrentReport!.FrozenJson!);
        var source = report.Preparation is { } preparation
            ? await store.GetVersion<SafetyCase>("case", id, preparation.CaseSource.Version, [value.ProductId], ct)
            : null;
        var ledger = await store.Get<EudamedDeliveryLedger>("eudamedDelivery",
            EudamedRules.MirDeliveryId(value.ProductId, value.Id, report.Jurisdiction), ct);
        EudamedRules.RequireLegacySendAvailable(ledger, value);
        return DispatchPreviews.ForCase(value, source, actor);
    }
}

public sealed partial class EudamedService
{
    public async Task<DispatchPreview> PreviewDossier(string id, int version, int ledgerVersion,
        Actor actor, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var value = await GetDossier(id, actor, ct);
        Guard.Against(value.Version != version, "version_conflict", "Ausgewaehlte Dossierfassung ist nicht mehr aktuell.", 409);
        EudamedRules.RequireFrozen(value);
        await CurrentChannel(value.Package.Binding, value.ProductId, ct);
        var ledger = await GetLedger(value.Package, ct);
        Guard.Against(ledger.Version != ledgerVersion, "version_conflict", "Versandreservierung wurde geaendert.", 409);
        RequireNoPending(ledger);
        return DispatchPreviews.ForDossier(value, ledger.Version, await objects.Read(value.FrozenPackageHash!, ct), actor);
    }
}
