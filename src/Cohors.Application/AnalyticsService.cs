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

    public async Task<Psur[]> ListPsurs(Actor actor, CancellationToken ct = default) =>
        (await store.List<Psur>("psur", actor.Products, ct)).Where(p => PsurGroupRules.CanRead(p.Snapshot, actor)).ToArray();

    public async Task<PsurGroupDefinition[]> ListGroups(Actor actor, CancellationToken ct = default) =>
        (await store.List<PsurGroupDefinition>("psurGroup", actor.Products, ct))
            .Where(g => g.Members.All(m => actor.Products.Contains(m.ProductId))).ToArray();

    public async Task<PsurGroupDefinition> RecordGroup(RecordPsurGroupCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        actor.RequireProduct(input.LeadProductId);
        var members = input.MemberProductIds ?? throw new DomainException("psur_group_members", "Explizite Gruppenmitglieder fehlen.");
        foreach (var member in members) { actor.RequireProduct(member); policies.Device(member); }
        var id = PsurGroupRules.Id(input.Reference);
        var current = await store.Get<PsurGroupDefinition>("psurGroup", id, ct);
        if (current is not null)
        {
            foreach (var member in current.Members) actor.RequireProduct(member.ProductId);
            Guard.Against(current.ProductId != input.LeadProductId, "psur_group_identity", "Leitproduktwechsel braucht eine neue Serie; alte Pflichten bleiben bestehen.");
        }
        var profiles = await store.List<ProductGovernance>("productGovernance", members, ct);
        return await store.Execute<PsurGroupDefinition>(new(id, "psurGroup", input.LeadProductId, input.ExpectedVersion, key,
            "PsurGroupDefined", input.Rationale, actor, input, clock.GetUtcNow()), previous =>
        {
            if (previous is not null) foreach (var member in previous.Members) actor.RequireProduct(member.ProductId);
            var definition = PsurGroupRules.Define(input.Reference, input.LeadProductId, members, input.FirstPeriodStart,
                input.Rationale, input.ScheduleBasis, profiles, previous, clock.GetUtcNow());
            foreach (var profile in profiles) GovernanceRules.RequireValid(profile, policies.Device(profile.ProductId));
            return definition;
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
        if (await store.Receipt<Psur>(context, ct) is not null) return await GetPsur(id, actor, ct);
        var group = input.GroupId is null ? null : await store.Get<PsurGroupDefinition>("psurGroup", input.GroupId, ct)
            ?? throw new DomainException("psur_group_missing", "Gewaehlte Gruppe fehlt.", 404);
        Guard.Against(group is null ? input.GroupVersion is not null : group.Version != input.GroupVersion || group.ProductId != input.ProductId,
            "psur_group_version", "Explizite aktuelle Gruppenversion und unveraendertes Leitprodukt erforderlich.", 409);
        var products = group?.Members.Select(m => m.ProductId).ToArray() ?? [input.ProductId];
        foreach (var product in products) actor.RequireProduct(product);
        var owner = Guid.NewGuid().ToString("N");
        var saved = await store.FreezePsur(context, new(products, group?.Id, policies.Device(input.ProductId).BasicUdi), portfolio =>
        {
            var governance = (portfolio.Governance ?? []).SingleOrDefault(p => p.ProductId == input.ProductId)
                ?? throw new DomainException("psur_governance", "Kontrolliertes synthetisches Produktprofil fehlt.");
            GovernanceRules.RequireValid(governance, policies.Device(input.ProductId));
            var frozenGroup = group is null ? null : (portfolio.Groups ?? []).SingleOrDefault(g => g.Id == group.Id);
            Guard.Against(group is not null && (frozenGroup is null || frozenGroup.Version != input.GroupVersion ||
                Canonical.Hash(frozenGroup) != Canonical.Hash(group)), "psur_group_version", "Gruppierung wurde zwischenzeitlich geaendert.", 409);
            if (frozenGroup is not null)
            {
                PsurGroupRules.Validate(frozenGroup, portfolio.Governance ?? []);
                foreach (var member in frozenGroup.Members)
                    GovernanceRules.RequireValid(portfolio.Governance!.Single(g => g.ProductId == member.ProductId), policies.Device(member.ProductId));
            }
            var series = PsurSeriesRules.Bind(new(input.ProductId, input.LeadProductId, input.Start, input.End,
                input.PreviousReportId, input.PreviousReportVersion, input.SubmissionDate, input.ScheduleAgreement, input.RevisionReason),
                governance, portfolio.Psurs ?? [], frozenGroup, portfolio.LeadingPeriods ??
                    throw new DomainException("psur_period_source", "Die globale fuehrende Basic-UDI-Periodenbindung fehlt.", 409));
            if (series.PreviousReport is { } previous)
                PsurGroupRules.RequireScope(portfolio.Psurs!.Single(p => p.Id == previous.Source.Id).Snapshot, actor);
            var frozenAt = clock.GetUtcNow();
            Guard.Against(portfolio.RegulatoryReviews is null, "psur_regulatory_source", "SQL-Schnitt der BS-Vorgaenge fehlt; kein behaupteter Nullbestand.", 409);
            PortfolioSnapshotInput FamilyInput(string product) => new(product, product, input.Start, input.End, input.Region,
                portfolio.Cases.Where(c => c.ProductId == product).ToArray(), portfolio.Exposures.Where(e => e.ProductId == product).ToArray(),
                portfolio.Trends.Where(t => t.ProductId == product).ToArray(), portfolio.Capas.Where(c => c.ProductId == product).ToArray(),
                portfolio.Evidence.Where(e => e.ProductId == product).ToArray(), portfolio.Changes.Where(c => c.ProductId == product).ToArray(),
                frozenAt, portfolio.History, portfolio.SourceRuns?.Where(s => s.ProductId == product).ToArray(), portfolio.RegulatoryReviews);
            var leadInput = FamilyInput(input.ProductId);
            var frozen = PortfolioRules.Snapshot(leadInput, series);
            if (frozenGroup is not null)
            {
                var members = frozenGroup.Members.Select(member => new PsurGroupMember(member,
                    portfolio.Governance!.Single(g => g.ProductId == member.ProductId),
                    member.ProductId == input.ProductId ? frozen : PortfolioRules.Snapshot(FamilyInput(member.ProductId)))).ToArray();
                frozen = frozen with { Group = new(PsurGroupRules.Contract, new(frozenGroup.Id, frozenGroup.Version, Canonical.Hash(frozenGroup)),
                    frozenGroup, members, "Each Basic-UDI has separate cases, exposure, coding, actions and source manifests. No pooled incidence. Current grouping does not claim historical membership. Earlier series and obligations remain unchanged.",
                    PsurRegulatoryRules.Capture(leadInput, products)) };
            }
            foreach (var evidence in PsurGroupRules.Documents(frozen)) EvidenceRules.RequireIntegrity(evidence);
            return new(id, 1, input.ProductId, frozen, Canonical.Hash(frozen), "", "", "Authoring", [], null, true, [], input.Mode, null, owner);
        }, ct);
        PsurGroupRules.RequireScope(saved.Snapshot, actor);
        if (saved.ExecutionId != owner) return await GetPsur(id, actor, ct);
        var snapshot = saved.Snapshot;
        try
        {
            var output = await executions.Run(owner, input.ProductId,
                new(AgentRole.PsurAuthoring, input.Mode, null, snapshot, null, PsurGroupRules.Documents(snapshot),
                    "Deutscher PSUR-Entwurf mit N/N2/N3/N4, getrennten Regionen und unbekannten Daten. Alle Kennzahlen mit [metric:Periode:Region:events|exposure=Wert] (unknown wenn unbekannt), heutige F02-Kodierungen mit [metric:Periode:Region:deathCoded=Wert], historische nicht releasevalidierte F02-Angaben separat mit [metric:Periode:Region:historicalDeathClaimed=Wert]. Bei einer gebundenen Gruppe alle Marker zusaetzlich mit Produkt qualifizieren: [metric:Produkt:Periode:Region:Kennzahl=Wert]. Jede Basic-UDI einzeln auswerten; keine produktuebergreifende Ratenaddition. Historische Originalangabe und heute vorgenommene retrospektive Kodierung strikt trennen; weder alte Release-Gueltigkeit noch alte Hierarchie aus heutigen Metadaten erfinden. Unvollstaendige Kodierung ist kein Null-Todesfallnachweis. Fallbelege [case:ID@Version], Dokumentbelege [evidence:ID@Version]. Jeden relevanten offenen/aktualisierten/nachtraeglichen BS-Vorgang mit [psurReview:ID@Version] belegen; Stichtagsstatus, Freeze-Status und Antwortzeitpunkt getrennt benennen. Gruppenweite BS-Historie nicht als Einzelfamilieninformation darstellen; fruehere Serienpflichten nicht schliessen. Medizinisch-technisch begruenden, keine autonome Risikoakzeptanz.", 0), ct);
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
        AgentBoundary.ValidateNarrative(input.Draft, "Ausfuehrlicher PSUR-Entwurf mit nachvollziehbaren Belegen", 300);
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

    public async Task<Psur> GetPsur(string id, Actor actor, CancellationToken ct = default)
    {
        var p = await store.Get<Psur>("psur", id, ct) ?? throw new DomainException("psur_missing", "PSUR fehlt.", 404);
        PsurGroupRules.RequireScope(p.Snapshot, actor);
        return p;
    }

    public async Task<Psur> GetVerifiedPsur(string id, Actor actor, CancellationToken ct = default)
    {
        var report = await GetPsur(id, actor, ct);
        RequirePsurIntegrity(report);
        await VerifySourceVersions(report, ct);
        return report;
    }

    public async Task<PsurMonthlyView> Monthly(string id, int version, string productId, Actor actor, CancellationToken ct = default)
    {
        var current = await GetPsur(id, actor, ct);
        var selected = current.Version == version ? current :
            (await store.History<Psur>("psur", [current.ProductId], ct)).SingleOrDefault(p => p.Id == id && p.Version == version)
                ?? throw new DomainException("psur_version_missing", "Die ausgewaehlte PSUR-Fassung fehlt; kein aktueller Ersatz.", 404);
        return PsurMonthly.Read(selected, productId, actor);
    }

    private async Task VerifySourceVersions(Psur p, CancellationToken ct)
    {
        Guard.Against(p.Snapshot.SourceManifest is null, "psur_numerator_provenance",
            "Historischer Bericht hat keinen hinreichenden Quellenstand; nicht still reparieren oder freigeben.");
        var series = p.Snapshot.Series ?? throw new DomainException("psur_series_legacy",
            "Altbericht ohne kontrollierte Serienbindung: keine stille Vervollstaendigung oder Freigabe.");
        var products = PsurGroupRules.Products(p.Snapshot);
        var profiles = await store.History<ProductGovernance>("productGovernance", products, ct);
        Guard.Against(!profiles.Any(g => g.Id == series.GovernanceSource.Id && g.Version == series.GovernanceSource.Version &&
            Canonical.Hash(g) == series.GovernanceSource.Hash), "psur_governance_source", "Gebundene Produktprofilversion fehlt oder wurde veraendert.", 409);
        if (series.PreviousReport is { } previous)
        {
            var reports = await store.History<Psur>("psur", [p.ProductId], ct);
            var prior = reports.SingleOrDefault(r => r.Id == previous.Source.Id && r.Version == previous.Source.Version);
            Guard.Against(prior is null || Canonical.Hash(prior) != previous.Source.Hash ||
                prior.Snapshot.Start != previous.Start || prior.Snapshot.End != previous.End ||
                prior.Status != previous.Status || prior.NotifiedBodyReceipt != previous.SimulatedReceipt ||
                prior.Snapshot.Series?.ReportVersion != previous.ReportVersion,
                "psur_previous_source", "Gebundene PSUR-Vorfassung fehlt oder ihre Zusammenfassung wurde veraendert.", 409);
        }
        if (p.Snapshot.Group is { } group)
        {
            var definitions = await store.History<PsurGroupDefinition>("psurGroup", products, ct);
            Guard.Against(!definitions.Any(g => g.Id == group.DefinitionSource.Id && g.Version == group.DefinitionSource.Version &&
                Canonical.Hash(g) == group.DefinitionSource.Hash), "psur_group_source", "Gebundene Gruppendefinition fehlt oder wurde veraendert.", 409);
            foreach (var member in group.Members)
                Guard.Against(!profiles.Any(g => g.Id == member.Product.GovernanceSource.Id &&
                    g.Version == member.Product.GovernanceSource.Version && Canonical.Hash(g) == member.Product.GovernanceSource.Hash),
                    "psur_group_source", "Ein eingefrorenes Mitgliedsprofil stimmt nicht mit der SQL-Historie ueberein.", 409);
        }
        foreach (var family in PsurGroupRules.Snapshots(p.Snapshot)) await VerifyFamilySourceVersions(family, ct);
        if (p.Snapshot.Group?.RegulatoryHistory is { } regulatory)
            PsurRegulatoryRules.RequireSources(regulatory, (await store.Capture(products, ct)).History ?? [],
                p.Snapshot.Start, p.Snapshot.End);
    }

    private async Task VerifyFamilySourceVersions(PsurSnapshot snapshot, CancellationToken ct)
    {
        Guard.Against(snapshot.SourceManifest is null, "psur_numerator_provenance", "Quellenmanifest einer ausgewiesenen Familie fehlt.", 409);
        var capturedHistory = (await store.Capture([snapshot.ProductId], ct)).History ?? [];
        var versions = (await store.History<SafetyCase>("case", [snapshot.ProductId], ct))
            .ToDictionary(c => (c.Id, c.Version));
        foreach (var source in snapshot.SourceManifest!.Cases)
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
            var cutoff = capturedHistory.Where(h => h.Kind == "case" && h.Id == source.Reference.Id &&
                    h.Version <= source.Reference.Version && h.At < snapshot.SourceManifest.KnowledgeCutoffExclusiveUtc)
                .OrderByDescending(h => h.Version).FirstOrDefault();
            var expectedStatus = source.FirstAwarenessAt >= snapshot.SourceManifest.KnowledgeCutoffExclusiveUtc ? "NotKnownAtCutoff" :
                cutoff is null ? "NotRecordedAtCutoff" : Canonical.Read<SafetyCase>(cutoff.Json).Stage.ToString();
            Guard.Against(source.CutoffVersion != (cutoff is null ? null : new VersionReference(cutoff.Id, cutoff.Version, cutoff.Hash)) ||
                source.StatusAtCutoff != expectedStatus, "psur_cutoff_source", "Historischer Fallstatus ist nicht durch die tatsaechliche SQL-Stichtagsversion belegt.", 409);
        }
        if (snapshot.Actions is { } actions)
        {
            await VerifyActionSources("capa", actions.Capas, v => v.Id, v => v.Version);
            await VerifyActionSources("change", actions.Changes, v => v.Id, v => v.Version);
        }
        if (snapshot.RegulatoryHistory is { } regulatory)
            PsurRegulatoryRules.RequireSources(regulatory, capturedHistory, snapshot.Start, snapshot.End);
        async Task VerifyActionSources<T>(string kind, PsurActionState<T>[] sources, Func<T, string> id, Func<T, int> version) where T : class
        {
            var history = (await store.History<T>(kind, [snapshot.ProductId], ct))
                .Select(v => new VersionReference(id(v), version(v), Canonical.Hash(v))).ToHashSet();
            Guard.Against(sources.SelectMany(s => new[] { s.CurrentSource, s.CutoffSource }.OfType<VersionReference>())
                .Any(source => !history.Contains(source)), "psur_action_source", "Gebundene aktuelle/historische Massnahmenversion fehlt oder wurde veraendert.", 409);
            foreach (var source in sources)
            {
                var cutoff = capturedHistory.Where(h => h.Kind == kind && h.Id == source.CurrentSource.Id &&
                        h.Version <= source.CurrentSource.Version && h.At < PsurSources.EndExclusive(snapshot.End))
                    .OrderByDescending(h => h.Version).FirstOrDefault();
                Guard.Against(source.CutoffSource != (cutoff is null ? null : new VersionReference(cutoff.Id, cutoff.Version, cutoff.Hash)) ||
                    source.RecordedAtCutoff != cutoff?.At, "psur_action_cutoff",
                    "Massnahmenstichtag oder gewaehlte historische Version widerspricht der SQL-Historie.", 409);
            }
        }
        if (snapshot.Directory is { } directory)
        {
            await VerifyDirectory<Evidence>("evidence", v => v.Id, v => v.Version);
            await VerifyDirectory<Exposure>("exposure", v => v.Id, v => v.Version);
            await VerifyDirectory<TrendRun>("trend", v => v.Id, v => v.Version);
            await VerifyDirectory<PublicSourceRun>("source", v => v.Id, v => v.Version);
            async Task VerifyDirectory<T>(string kind, Func<T, string> id, Func<T, int> version)
            {
                var required = directory.Entries.Where(e => e.Kind == kind).Select(e => e.Source).ToArray();
                if (required.Length == 0) return;
                var history = (await store.History<T>(kind, [snapshot.ProductId], ct))
                    .Select(v => new VersionReference(id(v), version(v), Canonical.Hash(v))).ToHashSet();
                Guard.Against(required.Any(r => !history.Contains(r)), "psur_directory_source",
                    "Ein verwendeter oder ausgeschlossen dokumentierter Datensatz stimmt nicht mit seiner SQL-Version ueberein.", 409);
            }
        }
    }

    private static void RequirePsurIntegrity(Psur p)
    {
        Guard.Against(!p.Simulated || p.SnapshotHash != Canonical.Hash(p.Snapshot) || p.ScopeHash != PortfolioRules.PsurScope(p),
            "psur_hash", "PSUR-Snapshot, Fassung oder Simulatorbindung wurde veraendert.", 409);
        Guard.Against(PsurSources.Check(p.Snapshot).Any(f => f.Severity == "Critical"), "psur_numerator_provenance",
            "Unvollstaendiger Stichtags-/Quellenstand: keine Freigabe oder Simulation. Explizit einen neuen pruefbaren Snapshot erstellen.");
        Guard.Against(PsurSeriesRules.Check(p.Snapshot).Length != 0, "psur_series_integrity", "PSUR-Serien- und Zeitplanbindung ist nicht vollstaendig.");
        Guard.Against(PortfolioRules.CheckPsur(p.Snapshot, p.Draft).Any(f => f.Severity is "Critical" or "Major"),
            "psur_assurance", "Aktuelle Nachweisregeln verhindern die Freigabe/Simulation; bestehenden Snapshot nicht still korrigieren.");
    }

    private static bool IsAuthoringFailure(Exception ex) => ex is DomainException or AgentExecutionException or HttpRequestException or
        TimeoutException or OperationCanceledException or JsonException;
    private static string ErrorCode(Exception ex) => ex is DomainException d ? d.Code : ex is AgentExecutionException a ? a.Code : ex.GetType().Name;
}
