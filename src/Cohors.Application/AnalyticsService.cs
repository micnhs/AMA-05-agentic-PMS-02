using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Application;

public sealed class AnalyticsService(IStateStore store, IAgentRunner runner, PolicyCatalog policies, TimeProvider clock)
{
    private readonly AgentExecutionService executions = new(store, runner, clock);

    public async Task<TrendRun> Trend(RunTrendCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Quality);
        actor.RequireProduct(input.ProductId);
        policies.Device(input.ProductId);
        PortfolioRules.RequireRegion(input.Region);
        Guard.Against(input.Start > input.End || input.End >= PortfolioRules.UtcDate(clock.GetUtcNow()),
            "trend_window", "Abgeschlossenes explizites Beobachtungsfenster erforderlich.");
        Guard.Against(policies.Trend.Unit != "Implants" || policies.Trend.Alpha != 0.05m,
            "trend_policy", "Die implementierte Poisson-Demomethode braucht das versionierte Implantat-/Alpha-0,05-Profil.");
        var id = "trend-" + Canonical.Hash(new { actor.Id, key })[..32];
        var context = new CommandContext(id, "trend", input.ProductId, 0, key, "TrendCalculated",
            "Deterministische Berechnung aus konsistentem SQL-Snapshot; eigener Ereigniszaehler, keine MAUDE-Inzidenz.", actor, input, clock.GetUtcNow());
        if (await store.Receipt<TrendRun>(context, ct) is not null) return (await store.Get<TrendRun>("trend", id, ct))!;
        var portfolio = await store.Capture([input.ProductId], ct);
        var exposure = portfolio.Exposures.SingleOrDefault(e => e.Id == input.ExposureId)
            ?? throw new DomainException("exposure_missing", "Freigegebener Nenner fehlt.", 404);
        var cases = portfolio.Cases.Where(c => c.Synthetic && c.ProductId == input.ProductId &&
            PortfolioRules.InRegion(c, input.Region)).ToArray();
        var selected = cases.Where(c => PortfolioRules.InPeriod(c, input.Start, input.End)).OrderBy(c => c.Id).ToArray();
        var unknown = cases.Where(c => c.OccurredAt is null).OrderBy(c => c.Id).ToArray();
        var data = new TrendInput(input.ProductId, input.Region, input.Start, input.End, selected.Select(c => c.Id).ToArray(),
            exposure, policies.Trend.ReferenceRate, policies.Trend.MethodVersion, selected.Select(PortfolioRules.Reference).ToArray(),
            unknown.Select(PortfolioRules.Reference).ToArray(), policies.Trend);
        var result = Statistics.Calculate(data);
        var owner = Guid.NewGuid().ToString("N");
        var trend = await store.Execute<TrendRun>(context, _ => new(id, 1, input.ProductId, data, result, clock.GetUtcNow(),
            "Authoring", "", null, null, Canonical.Hash(data), input.Mode, null, owner), ct);
        if (trend.ExecutionId != owner) return (await store.Get<TrendRun>("trend", id, ct))!;
        try
        {
            var output = await executions.Run(owner, input.ProductId,
                new(AgentRole.TrendSignal, input.Mode, null, null, trend, [],
                    "Erlaeutere die deterministischen Werte, Nennerpassung, ausgeschlossene unbekannte Daten, Annahmen und Unsicherheit. Keine automatische Art.-88-Entscheidung.", 0), ct);
            return await store.Execute<TrendRun>(context with { ExpectedVersion = 1, IdempotencyKey = key + "-result",
                Action = "TrendNarrativeDrafted", Request = output, At = clock.GetUtcNow() }, current =>
                    current! with { Version = 2, Interpretation = output.Narrative, Status = "Candidate" }, ct);
        }
        catch (Exception ex) when (IsAuthoringFailure(ex))
        {
            var error = ErrorCode(ex);
            return await store.Execute<TrendRun>(context with { ExpectedVersion = 1, IdempotencyKey = key + "-failure",
                Action = "TrendAuthoringFailed", Request = new { error }, At = clock.GetUtcNow() }, current =>
                    current! with { Version = 2, Status = "AuthoringFailed", Error = error }, CancellationToken.None);
        }
    }

    public async Task<TrendRun> ReviewTrend(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        var current = await store.Get<TrendRun>("trend", id, ct) ?? throw new DomainException("trend_missing", "Trend fehlt.", 404);
        actor.RequireProduct(current.ProductId);
        Guard.Text(input.Reason, "Begruendete klinische Einordnung und naechste Massnahme", 40);
        return await store.Execute<TrendRun>(new(id, "trend", current.ProductId, input.ExpectedVersion, key, "TrendReviewed",
            input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status is not ("Candidate" or "AuthoringFailed"), "trend_state", "Kein offener Trend zur Beurteilung.");
            Guard.Against(value.InputHash != Canonical.Hash(value.Input), "trend_hash", "Berechnungsbasis wurde veraendert.", 409);
            return value with { Version = value.Version + 1, Status = "Reviewed", ReviewerId = actor.Id, Rationale = input.Reason };
        }, ct);
    }

    public async Task<Psur> Freeze(FreezePsurCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        actor.RequireProduct(input.ProductId);
        policies.Device(input.ProductId);
        Guard.Against(input.End >= PortfolioRules.UtcDate(clock.GetUtcNow()), "psur_window", "PSUR-Beobachtungsfenster ist noch nicht abgeschlossen.");
        var id = "psur-" + Canonical.Hash(new { actor.Id, key })[..32];
        var context = new CommandContext(id, "psur", input.ProductId, 0, key, "PsurSnapshotFrozen",
            "Konsistenter versionierter SQL-Snapshot; offene Faelle und fehlende Vergleichsdaten bleiben sichtbar.", actor, input, clock.GetUtcNow());
        if (await store.Receipt<Psur>(context, ct) is not null) return (await store.Get<Psur>("psur", id, ct))!;
        var portfolio = await store.Capture([input.ProductId], ct);
        var snapshot = PortfolioRules.Snapshot(new(input.ProductId, input.LeadProductId, input.Start, input.End, input.Region,
            portfolio.Cases, portfolio.Exposures, portfolio.Trends, portfolio.Capas, portfolio.Evidence, portfolio.Changes, clock.GetUtcNow(), portfolio.History));
        foreach (var evidence in snapshot.Documents) EvidenceRules.RequireIntegrity(evidence);
        var owner = Guid.NewGuid().ToString("N");
        var saved = await store.Execute<Psur>(context, _ => new(id, 1, input.ProductId, snapshot, Canonical.Hash(snapshot),
            "", "", "Authoring", [], null, true, [], input.Mode, null, owner), ct);
        if (saved.ExecutionId != owner) return (await store.Get<Psur>("psur", id, ct))!;
        try
        {
            var output = await executions.Run(owner, input.ProductId,
                new(AgentRole.PsurAuthoring, input.Mode, null, snapshot, null, snapshot.Documents,
                    "Deutscher PSUR-Entwurf mit N/N2/N3/N4, getrennten Regionen und unbekannten Daten. Alle Kennzahlen mit [metric:Periode:Region:events|exposure=Wert] (unknown wenn unbekannt), explizite F02-Kodierungen getrennt mit [metric:Periode:Region:deathCoded=Wert]. Unvollstaendige Kodierung ist kein Null-Todesfallnachweis; A/C/D/F-Kodes und Terme sowie historische/gesperrte Gruppen transparent zeigen. Fallbelege [case:ID@Version], Dokumentbelege [evidence:ID@Version]. Medizinisch-technisch begruenden, keine autonome Risikoakzeptanz.", 0), ct);
            return await store.Execute<Psur>(context with { ExpectedVersion = 1, IdempotencyKey = key + "-result",
                Action = "PsurDrafted", Request = output, At = clock.GetUtcNow() }, value =>
            {
                var next = value! with { Version = 2, Draft = output.Narrative, Status = "Draft",
                    Findings = [.. PortfolioRules.CheckPsur(snapshot, output.Narrative), .. output.Findings] };
                return next with { ScopeHash = PortfolioRules.PsurScope(next) };
            }, ct);
        }
        catch (Exception ex) when (IsAuthoringFailure(ex))
        {
            var error = ErrorCode(ex);
            return await store.Execute<Psur>(context with { ExpectedVersion = 1, IdempotencyKey = key + "-failure",
                Action = "PsurAuthoringFailed", Request = new { error }, At = clock.GetUtcNow() }, value =>
                    value! with { Version = 2, Status = "AuthoringFailed", Error = error }, CancellationToken.None);
        }
    }

    public async Task<Psur> Revise(string id, RevisePsurCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var current = await GetPsur(id, actor, ct);
        Guard.Text(input.Draft, "Ausfuehrlicher PSUR-Entwurf mit nachvollziehbaren Belegen", 300, 60000);
        Guard.Text(input.Reason, "Aenderungsbegruendung", 30);
        return await store.Execute<Psur>(new(id, "psur", current.ProductId, input.ExpectedVersion, key, "PsurRevised",
            input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status == "Authoring" || value.NotifiedBodyReceipt is not null,
                "psur_state", "Aktiver Autorenauftrag oder bereits quittierter Bericht braucht einen eigenen Folgezyklus.");
            var next = value with { Version = value.Version + 1, Draft = input.Draft, Status = "Draft",
                Findings = PortfolioRules.CheckPsur(value.Snapshot, input.Draft), Approvals = [] };
            return next with { ScopeHash = PortfolioRules.PsurScope(next) };
        }, ct);
    }

    public async Task<Psur> Approve(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var current = await GetPsur(id, actor, ct);
        await VerifySourceVersions(current, ct);
        Guard.Text(input.Reason, "Fachliche PSUR-Freigabebegruendung einschliesslich offener Risiken", 40);
        return await store.Execute<Psur>(new(id, "psur", current.ProductId, input.ExpectedVersion, key, "PsurApproved",
            input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            var p = value!;
            RequirePsurIntegrity(p);
            Guard.Against(p.Findings.Any(f => f.Severity is "Critical" or "Major"), "psur_assurance", "Kritische/wesentliche PSUR-Befunde erst beheben.");
            var role = p.Status == "Draft" ? Roles.Medical : Roles.Regulatory;
            actor.RequireHuman(role);
            Guard.Against(p.Status is not ("Draft" or "MedicalApproved"), "psur_state", "Kein freizugebender Entwurf.");
            Guard.Against(p.Approvals.Any(a => a.ActorId == actor.Id), "four_eyes", "Getrennte berechtigte menschliche Konten erforderlich.", 403);
            if (role == Roles.Regulatory)
                Guard.Against(!p.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == p.ScopeHash), "medical_required", "Aktuelle medizinische Freigabe fehlt.");
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, p.ScopeHash, clock.GetUtcNow(),
                "PSUR-Snapshot und konkrete Fassung; keine produktive BS-Uebermittlung", input.Reason, null);
            return p with { Version = p.Version + 1, Status = role == Roles.Medical ? "MedicalApproved" : "Approved", Approvals = [.. p.Approvals, approval] };
        }, ct);
    }

    public async Task<Psur> Simulate(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var current = await GetPsur(id, actor, ct);
        await VerifySourceVersions(current, ct);
        return await store.Execute<Psur>(new(id, "psur", current.ProductId, input.ExpectedVersion, key, "PsurNotifiedBodySimulated",
            input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            var p = value!;
            RequirePsurIntegrity(p);
            var medical = p.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == p.ScopeHash);
            var regulatory = p.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == p.ScopeHash);
            Guard.Against(p.Status != "Approved" || p.NotifiedBodyReceipt is not null || medical is null || regulatory is null ||
                medical.ActorId == regulatory.ActorId, "psur_gates", "Einmalige Simulation nur mit aktuellen getrennten Freigaben.", 409);
            return p with { Version = p.Version + 1, NotifiedBodyReceipt = "DEMO-BS-" + p.ScopeHash[..16], Status = "SimulatedAccepted" };
        }, ct);
    }

    private async Task<Psur> GetPsur(string id, Actor actor, CancellationToken ct)
    {
        var p = await store.Get<Psur>("psur", id, ct) ?? throw new DomainException("psur_missing", "PSUR fehlt.", 404);
        actor.RequireProduct(p.ProductId);
        return p;
    }

    private async Task VerifySourceVersions(Psur p, CancellationToken ct)
    {
        Guard.Against(p.Snapshot.SourceManifest is null, "psur_numerator_provenance",
            "Historischer Bericht hat keinen hinreichenden Quellenstand; nicht still reparieren oder freigeben.");
        var versions = (await store.History<SafetyCase>("case", [p.ProductId], ct))
            .ToDictionary(c => (c.Id, c.Version));
        foreach (var source in p.Snapshot.SourceManifest!.Cases)
        {
            foreach (var reference in new[] { source.Reference, source.CutoffVersion }.OfType<VersionReference>())
                Guard.Against(!versions.TryGetValue((reference.Id, reference.Version), out var value) || Canonical.Hash(value) != reference.Hash,
                    "psur_source_version", "Eine gebundene SQL-Quellfallversion fehlt oder hat einen anderen Hash.", 409);
            var actual = versions[(source.Reference.Id, source.Reference.Version)];
            Guard.Against(source.ProductId != actual.ProductId || source.Country != actual.Country || source.Jurisdiction != actual.Jurisdiction ||
                source.OccurredAt != actual.OccurredAt || source.FirstAwarenessAt != actual.FirstAwarenessAt ||
                source.SystemReceivedAt != actual.SystemReceivedAt || source.Synthetic != actual.Synthetic ||
                Canonical.Hash(source.Coding) != Canonical.Hash(actual.Coding), "psur_source_content",
                "Metadaten oder Kodierung des Quellenmanifests widersprechen der gebundenen SQL-Fallversion.", 409);
        }
    }

    private static void RequirePsurIntegrity(Psur p)
    {
        Guard.Against(!p.Simulated || p.SnapshotHash != Canonical.Hash(p.Snapshot) || p.ScopeHash != PortfolioRules.PsurScope(p),
            "psur_hash", "PSUR-Snapshot, Fassung oder Simulatorbindung wurde veraendert.", 409);
        Guard.Against(PsurSources.Check(p.Snapshot).Any(f => f.Severity == "Critical"), "psur_numerator_provenance",
            "Unvollstaendiger Stichtags-/Quellenstand: keine Freigabe oder Simulation. Explizit einen neuen pruefbaren Snapshot erstellen.");
    }

    private static bool IsAuthoringFailure(Exception ex) => ex is DomainException or AgentExecutionException or HttpRequestException or
        TimeoutException or OperationCanceledException or JsonException;
    private static string ErrorCode(Exception ex) => ex is DomainException d ? d.Code : ex is AgentExecutionException a ? a.Code : ex.GetType().Name;
}
