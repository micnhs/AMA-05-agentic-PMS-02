using System.Security.Cryptography;
using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class EudamedService
{
    public async Task<FsnFeedbackView> FsnFeedback(string id, Actor actor, CancellationToken ct = default)
    {
        var dossier = await GetDossier(id, actor, ct);
        FsnPublicationRules.RequireDossier(dossier);
        var ledger = await GetLedger(dossier.Package, ct);
        Guard.Against(ledger.Id != LedgerId(dossier.Package) || ledger.ProductId != dossier.ProductId,
            "fsn_feedback_source", "Kommentarstand gehoert nicht zur ausgewaehlten FSN-Meldepflicht.", 409);
        RequireFsnFeedbackScope(await FsnFeedbackSources(ledger, ct), actor);
        return new(EudamedContracts.Bind(dossier), LedgerDependency(ledger).Source, FsnPublicationRules.Feedback(ledger));
    }

    public async Task<EudamedDossier> DraftFsnPublication(string id, DraftFsnPublicationCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var value = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Begruendung des gesonderten FSN-Verteilungsentwurfs", 80, 3999);
        var context = Context(id, "eudamedDossier", value.ProductId, input.ExpectedVersion, key,
            "FsnPublicationDrafted", input.Reason, actor, input);
        if (await store.Receipt<EudamedDossier>(context, ct) is { } replay)
        {
            EudamedRules.RequireActorScope(replay, actor);
            await RequireFsnPublicationAccess(replay, actor, ct);
            return replay;
        }
        Guard.Against(value.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
        var (channel, ledger, sources) = await FsnPublicationContext(value, input.ExpectedLedgerVersion, actor, ct);
        var fsn = value.Package.Content.Fsn!;
        var feedback = FsnPublicationRules.Feedback(ledger);
        Guard.Against(input.Feedback is null || input.Feedback.Length != feedback.Length ||
            input.Feedback.Any(f => f is null) ||
            input.Feedback.Select(f => f.CorrespondenceId).Distinct(StringComparer.Ordinal).Count() != input.Feedback.Length,
            "fsn_feedback_incomplete", "Jeder bekannte Kommentar braucht genau eine ausdrueckliche Bewertung.", 409);
        var assessed = input.Feedback.Select(decision =>
        {
            var original = feedback.SingleOrDefault(f => f.Id == decision.CorrespondenceId && f.OriginalHash == decision.OriginalHash)
                ?? throw new DomainException("fsn_feedback_stale", "Ausgewaehlte Originalkorrespondenz fehlt oder wurde geaendert.", 409);
            return new FsnFeedbackAssessment(original, decision.Disposition, decision.Rationale);
        }).OrderBy(f => f.Original.Id, StringComparer.Ordinal).ToArray();
        var plan = new FsnPublicationPlan(FsnPublicationRules.Contract, EudamedContracts.Bind(value), value.Revision,
            value.ScopeHash, fsn.DocumentHash, fsn.DocumentName, fsn.DocumentVersion,
            value.Package.Sources.Single(s => s.Kind == "eudamedDossier" && s.Source.Id == fsn.FscaId).Source,
            new(ledger.Id, ledger.Version, Canonical.Hash(ledger)), fsn.Audience, input.Recipients,
            input.Channel, input.Route, assessed, input.ChangeAssessment, input.RegulatoryDecision);
        FsnPublicationRules.RequirePlan(value, plan, ledger);
        var review = new FsnPublicationReview("fsn-publication-" + Canonical.Hash(new { DossierId = id, ActorId = actor.Id, key })[..32],
            plan, Canonical.Hash(plan), "Draft", [], actor.Id, clock.GetUtcNow());
        var dependencies = PackageDependencies(value.Package, channel);
        dependencies.Add(LedgerDependency(ledger));
        dependencies.AddRange(sources.Select(s => new StoreDependency("eudamedDossier", EudamedContracts.Bind(s.Current))));
        var accessProducts = value.AccessProducts.Concat(sources.SelectMany(s =>
                s.Current.AccessProducts.Concat(s.Approved.AccessProducts)
                    .Concat(EudamedRules.AccessProducts(s.Current.Package))
                    .Concat(EudamedRules.AccessProducts(s.Approved.Package))))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return await store.Execute<EudamedDossier>(context with { Dependencies = dependencies.ToArray() },
            current => current! with { Version = current.Version + 1, FsnPublication = review, AccessProducts = accessProducts }, ct);
    }

    public async Task<EudamedDossier> ApproveFsnPublication(string id, ApprovalCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var value = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Eigenstaendige medizinische/regulatorische Veroeffentlichungsentscheidung", 100, 3999);
        var context = Context(id, "eudamedDossier", value.ProductId, input.ExpectedVersion, key,
            "FsnPublicationApproved", input.Reason, actor, input);
        if (await store.Receipt<EudamedDossier>(context, ct) is { } replay)
        {
            EudamedRules.RequireActorScope(replay, actor);
            await RequireFsnPublicationAccess(replay, actor, ct);
            return replay;
        }
        Guard.Against(value.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
        var review = value.FsnPublication ?? throw new DomainException("fsn_publication_missing", "Gesonderter Verteilungsentwurf fehlt.", 409);
        var role = review.ReviewStatus switch
        {
            "Draft" => Roles.Medical,
            "MedicalApproved" => Roles.Regulatory,
            _ => throw new DomainException("fsn_publication_review", "Keine aktuelle freizugebende Verteilungsfassung.", 409)
        };
        actor.RequireHuman(role);
        Guard.Against(review.Approvals.Any(a => a.ActorId == actor.Id), "four_eyes", "Getrennte menschliche Konten sind erforderlich.", 403);
        Guard.Against(role == Roles.Medical ? review.Approvals.Length != 0 :
            review.Approvals.Length != 1 || review.Approvals[0].Role != Roles.Medical ||
            review.Approvals[0].ScopeHash != review.ScopeHash,
            "fsn_publication_review", "Die erforderliche getrennte medizinische Vorpruefung muss genau diesen Scope binden.", 409);
        var (channel, ledger, sources) = await FsnPublicationContext(value, review.Plan.DeliveryLedger.Version, actor, ct);
        EudamedRules.RequireActorScope(await FsnPublicationSource(value, review, ledger, ct), actor);
        var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, review.ScopeHash,
            clock.GetUtcNow(), "Gesonderte Pruefung der FSN-Dokumentfassung, FSCA, bewerteten Kommentare/Aenderungen, Empfaenger und des ausdruecklichen Veroeffentlichungswegs; keine reale Kommunikation.",
            input.Reason, null);
        var next = review with { Approvals = [.. review.Approvals, approval],
            ReviewStatus = role == Roles.Medical ? "MedicalApproved" : "Frozen" };
        var dependencies = PackageDependencies(value.Package, channel);
        dependencies.Add(LedgerDependency(ledger));
        dependencies.AddRange(sources.Select(s => new StoreDependency("eudamedDossier", EudamedContracts.Bind(s.Current))));
        return await store.Execute<EudamedDossier>(context with { Dependencies = dependencies.ToArray() },
            current => current! with { Version = current.Version + 1, FsnPublication = next }, ct);
    }

    public async Task<EudamedDossier> PublishFsnDistribution(string id, EudamedDistributionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var value = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Explizite finale simulierte Veroeffentlichung und Verteilung", 100, 3999);
        var context = Context(id, "eudamedDossier", value.ProductId, input.ExpectedVersion, key,
            "FsnPublicationSimulated", input.Reason, actor, input);
        if (await store.Receipt<EudamedDossier>(context, ct) is { } replay)
        {
            EudamedRules.RequireActorScope(replay, actor);
            await RequireFsnPublicationAccess(replay, actor, ct);
            return replay;
        }
        Guard.Against(value.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
        var review = value.FsnPublication ?? throw new DomainException("fsn_publication_missing", "Gesonderte Verteilungsfreigabe fehlt.", 409);
        Guard.Against(!input.ConfirmSimulation || input.PublicationScopeHash != review.ScopeHash,
            "fsn_publication_confirmation", "Genau den angezeigten freigegebenen Verteilungsscope ausdruecklich nur fuer die Simulation bestaetigen.", 409);
        FsnPublicationRules.RequireFrozen(review);
        var (channel, ledger, sources) = await FsnPublicationContext(value, input.ExpectedLedgerVersion, actor, ct);
        EudamedRules.RequireActorScope(await FsnPublicationSource(value, review, ledger, ct), actor);
        var document = await objects.ReadBytes(review.Plan.DocumentHash, ct);
        Guard.Against(Convert.ToHexStringLower(SHA256.HashData(document)) != review.Plan.DocumentHash ||
            Canonical.HashText(await objects.Read(value.FrozenPackageHash!, ct)) != value.ScopeHash,
            "fsn_publication_original", "Originaldokument oder eingefrorenes Paket ist nicht unveraendert lesbar.", 409);
        var distributionId = "fsn-distribution-" + Canonical.Hash(new { DossierId = id, PublicationId = review.Id, review.ScopeHash })[..32];
        var now = clock.GetUtcNow();
        var published = review with { ReviewStatus = "Published" };
        var evidence = new FsnDistributionEvidence(FsnPublicationRules.Contract, distributionId, id, published,
            value.Package.Binding, review.Plan.Recipients.Select(recipient => new FsnRecipientReceipt(recipient,
                "simulator://fsn/" + recipient.Id, review.Plan.Channel, "SimulatedDelivered")).ToArray(),
            now, actor.Id, true,
            "Nur lokal generierter Veroeffentlichungs-/Kommunikationsbeleg; keine echte Empfaengerannahme, keine externe Versendung, kein FSN.PUT und keine neue EUDAMED-Uebermittlung.");
        var evidenceJson = Canonical.Serialize(evidence);
        var evidenceHash = await objects.Put(evidenceJson, ct);
        Guard.Against(evidenceHash != Canonical.HashText(evidenceJson), "fsn_distribution_integrity", "Simulationsbeleg wurde nicht hashkonsistent gespeichert.", 409);
        var distribution = new FsnDistribution(distributionId, value.Revision, value.ScopeHash,
            review.Plan.Audience, review.Plan.Channel, evidenceHash, now, actor.Id, true)
            { PublicationId = review.Id, PublicationScopeHash = review.ScopeHash, PublicationSource = EudamedContracts.Bind(value) };
        var dependencies = PackageDependencies(value.Package, channel);
        dependencies.Add(LedgerDependency(ledger));
        dependencies.AddRange(sources.Select(s => new StoreDependency("eudamedDossier", EudamedContracts.Bind(s.Current))));
        return await store.Execute<EudamedDossier>(context with { Dependencies = dependencies.ToArray() },
            current => current! with { Version = current.Version + 1, FsnPublication = published,
                Distributions = [.. current.Distributions, distribution] }, ct);
    }

    private async Task<(EudamedChannel Channel, EudamedDeliveryLedger Ledger,
        (EudamedDossier Current, EudamedDossier Approved)[] Sources)> FsnPublicationContext(
        EudamedDossier value, int expectedLedgerVersion, Actor actor, CancellationToken ct)
    {
        FsnPublicationRules.RequireDossier(value);
        RequireProjection(value.Package);
        var channel = await CurrentChannel(value.Package.Binding, value.ProductId, ct);
        var ledger = await GetLedger(value.Package, ct);
        Guard.Against(ledger.Version != expectedLedgerVersion, "fsn_publication_stale",
            "Versand-/Kommentarstand wurde geaendert; Verteilungsentwurf erneut pruefen.", 409);
        var sources = await FsnFeedbackSources(ledger, ct);
        RequireFsnFeedbackScope(sources, actor);
        return (channel, ledger, sources);
    }

    private async Task<(EudamedDossier Current, EudamedDossier Approved)[]> FsnFeedbackSources(
        EudamedDeliveryLedger ledger, CancellationToken ct)
    {
        var result = new List<(EudamedDossier Current, EudamedDossier Approved)>();
        Guard.Against(ledger.Id != EudamedRules.DeliveryId(ledger.ObligationKey),
            "fsn_feedback_source", "Versandjournal und Meldepflichtbindung unterscheiden sich.", 409);
        foreach (var group in ledger.Entries.GroupBy(e => e.DossierId, StringComparer.Ordinal))
        {
            var current = await store.Get<EudamedDossier>("eudamedDossier", group.Key, ct)
                ?? throw new DomainException("fsn_feedback_source", "Aktuelles Dossier der gebundenen Korrespondenz fehlt.", 409);
            foreach (var entry in group)
            {
                var approved = await ApprovedDossier(entry, ct);
                Guard.Against(current.Kind != EudamedDossierKind.FSN || approved.Kind != EudamedDossierKind.FSN ||
                    current.ProductId != ledger.ProductId || approved.ProductId != ledger.ProductId ||
                    EudamedRules.ObligationKey(current.Package) != ledger.ObligationKey ||
                    EudamedRules.ObligationKey(approved.Package) != ledger.ObligationKey,
                    "fsn_feedback_source", "Aktuelles und historisches Korrespondenzdossier gehoeren nicht zur gebundenen FSN-Meldepflicht.", 409);
                result.Add((current, approved));
            }
        }
        return result.ToArray();
    }

    private static void RequireFsnFeedbackScope((EudamedDossier Current, EudamedDossier Approved)[] sources, Actor actor)
    {
        foreach (var source in sources)
        {
            EudamedRules.RequireActorScope(source.Current, actor);
            EudamedRules.RequireActorScope(source.Approved, actor);
        }
    }

    private async Task<bool> CanReadFsnPublication(EudamedDossier value, Actor actor, CancellationToken ct)
    {
        if (value.FsnPublication is not { } review) return true;
        var reference = review.Plan.DeliveryLedger;
        Guard.Against(reference.Id != LedgerId(value.Package) || reference.Version < 1 ||
            review.ScopeHash != Canonical.Hash(review.Plan),
            "fsn_publication_hash", "Gebundener Verteilungs-/Kommentarstand wurde veraendert.", 409);
        var ledger = await store.GetVersion<EudamedDeliveryLedger>("eudamedDelivery", reference.Id, reference.Version, ct)
            ?? throw new DomainException("fsn_feedback_source", "Gebundene historische Korrespondenzfassung fehlt.", 409);
        Guard.Against(LedgerDependency(ledger).Source != reference,
            "fsn_feedback_source", "Historische Korrespondenz passt nicht zur Versions-/Hashbindung.", 409);
        var source = await FsnPublicationSource(value, review, ledger, ct);
        return EudamedRules.CanRead(source, actor) && (await FsnFeedbackSources(ledger, ct))
            .All(s => EudamedRules.CanRead(s.Current, actor) && EudamedRules.CanRead(s.Approved, actor));
    }

    private async Task RequireFsnPublicationAccess(EudamedDossier value, Actor actor, CancellationToken ct) =>
        Guard.Against(!await CanReadFsnPublication(value, actor, ct), "fsn_feedback_scope",
            "Der vollstaendige aktuelle und historische Produktscope der gebundenen Korrespondenz ist erforderlich.", 403);

    private async Task RequireFsnDistributionAccess(EudamedDossier dossier, FsnDistribution distribution,
        Actor actor, CancellationToken ct)
    {
        if (distribution.PublicationId is null && distribution.PublicationScopeHash is null &&
            distribution.PublicationSource is null) return;
        var reference = distribution.PublicationSource;
        Guard.Against(reference is null || reference.Id != dossier.Id || reference.Version < 1 || reference.Version > dossier.Version,
            "fsn_distribution_binding", "Versionsgebundener Ursprung der Verteilungsfreigabe fehlt.", 409);
        var source = await store.GetVersion<EudamedDossier>("eudamedDossier", reference!.Id, reference.Version, ct)
            ?? throw new DomainException("fsn_distribution_binding", "Originalfassung der Verteilungsfreigabe fehlt.", 409);
        EudamedRules.RequireActorScope(source, actor);
        await RequireFsnPublicationAccess(source, actor, ct);
        Guard.Against(EudamedContracts.Bind(source) != reference || source.Revision != distribution.DossierRevision ||
            source.ScopeHash != distribution.PackageHash || source.FsnPublication is null ||
            source.FsnPublication.Id != distribution.PublicationId ||
            source.FsnPublication.ScopeHash != distribution.PublicationScopeHash,
            "fsn_distribution_binding", "Kommunikationsbeleg passt nicht zum freigegebenen Originalscope.", 409);
        FsnPublicationRules.RequireFrozen(source.FsnPublication!);
    }

    private async Task<EudamedDossier> FsnPublicationSource(EudamedDossier value, FsnPublicationReview review,
        EudamedDeliveryLedger ledger, CancellationToken ct)
    {
        Guard.Against(review.ScopeHash != Canonical.Hash(review.Plan), "fsn_publication_hash", "Verteilungsscope wurde veraendert.", 409);
        FsnPublicationRules.RequirePlan(value, review.Plan, ledger);
        var source = await store.GetVersion<EudamedDossier>("eudamedDossier", value.Id, review.Plan.DossierSource.Version, ct)
            ?? throw new DomainException("fsn_publication_source", "Gebundene Dossier-Originalfassung fehlt.", 409);
        EudamedRules.RequireFrozen(source);
        Guard.Against(EudamedContracts.Bind(source) != review.Plan.DossierSource ||
            source.Revision != value.Revision || source.ScopeHash != value.ScopeHash,
            "fsn_publication_source", "Dokument-/Paketursprung passt nicht zur gesonderten Verteilungsfassung.", 409);
        return source;
    }
}
