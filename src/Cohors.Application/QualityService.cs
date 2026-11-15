using Cohors.Domain;

namespace Cohors.Application;

public sealed class QualityService(IStateStore store, IObjectStore objects, TimeProvider clock)
{
    private async Task<SafetyCase> SourceCase(string id, string product, Actor actor, CancellationToken ct)
    {
        actor.RequireProduct(product);
        var c = await store.Get<SafetyCase>("case", id, ct) ?? throw new DomainException("case_missing", "Ursprungsfall fehlt.", 404);
        Guard.Against(c.ProductId != product || !c.Synthetic, "source_scope", "Synthetischer Ursprungsfall und Produktscope muessen uebereinstimmen.", 403);
        return c;
    }

    public async Task<Capa> OpenCapa(OpenCapaCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Quality);
        await SourceCase(input.CaseId, input.ProductId, actor, ct);
        Guard.Text(input.Title, "CAPA-Titel", 10, 200);
        Guard.Text(input.Action, "Konkrete Massnahme", 20);
        Guard.Text(input.Rationale, "Medizinisch-technische Begruendung", 30);
        Guard.Text(input.EffectivenessCriterion, "Messbares Wirksamkeitskriterium", 20);
        Guard.Against(input.OwnerId != actor.Id, "capa_owner", "Im Pilot uebernimmt die eroeffnende Person selbst die Massnahmenverantwortung.");
        Guard.Against(input.DueAt <= clock.GetUtcNow(), "capa_due", "Die interne Massnahmenfrist muss in der Zukunft liegen.");
        var id = "capa-" + Canonical.Hash(new { actorId = actor.Id, key })[..32];
        return await store.Execute<Capa>(new(id, "capa", input.ProductId, 0, key, "CapaOpened", input.Rationale,
            actor, input, clock.GetUtcNow()), _ => new(id, 1, input.ProductId, input.CaseId, input.Title,
                input.Rationale, actor.Id, input.Action, input.EffectivenessCriterion, input.DueAt,
                "Open", null, null, null), ct);
    }

    public async Task<Capa> Amend(string id, AmendCapaCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Quality);
        var current = await store.Get<Capa>("capa", id, ct) ?? throw new DomainException("capa_missing", "CAPA fehlt.", 404);
        actor.RequireProduct(current.ProductId);
        Guard.Against(actor.Id != current.OwnerId && !actor.Roles.Contains(Roles.Quality), "capa_owner",
            "Nur verantwortliche Person oder QM darf den Massnahmenplan bearbeiten.", 403);
        Guard.Text(input.Action, "Massnahme", 20);
        Guard.Text(input.EffectivenessCriterion, "Wirksamkeitskriterium", 20);
        Guard.Text(input.Reason, "Aenderungs-/Wiedereroeffnungsbegruendung", 30);
        Guard.Against(input.DueAt <= clock.GetUtcNow(), "capa_due", "Neue interne Frist muss in der Zukunft liegen.");
        return await store.Execute<Capa>(new(id, "capa", current.ProductId, input.ExpectedVersion, key, "CapaPlanAmended",
            input.Reason, actor, input, clock.GetUtcNow()), value => value! with { Version = value.Version + 1,
                Action = input.Action, EffectivenessCriterion = input.EffectivenessCriterion, DueAt = input.DueAt,
                Status = "Open", Evidence = null, VerifiedBy = null, VerifiedAt = null }, ct);
    }

    public async Task<Capa> Verify(string id, VerifyCapaCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var current = await store.Get<Capa>("capa", id, ct) ?? throw new DomainException("capa_missing", "CAPA fehlt.", 404);
        actor.RequireProduct(current.ProductId);
        Guard.Against(current.OwnerId == actor.Id, "independent_effectiveness",
            "Wirksamkeitspruefung braucht eine berechtigte Person, die nicht Massnahmenverantwortliche ist.", 403);
        Guard.Text(input.Evidence, "Konkreter Ergebnisnachweis gegen das Wirksamkeitskriterium", 40, 6000);
        Guard.Text(input.Reason, "Wirksamkeitsbegruendung", 30);
        return await store.Execute<Capa>(new(id, "capa", current.ProductId, input.ExpectedVersion, key, "CapaEffectivenessReviewed",
            input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status == "Verified", "capa_closed", "Eine abgeschlossene CAPA muss vor neuer Pruefung begruendet wiedereroeffnet werden.");
            return value with { Version = value.Version + 1, Status = input.Effective ? "Verified" : "NotEffective",
                Evidence = input.Evidence, VerifiedBy = actor.Id, VerifiedAt = clock.GetUtcNow() };
        }, ct);
    }

    public async Task<ChangeRequest> OpenChange(OpenChangeCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Quality);
        var c = await SourceCase(input.CaseId, input.ProductId, actor, ct);
        var document = await store.Get<Evidence>("evidence", input.DocumentId, ct)
            ?? throw new DomainException("document_missing", "Kontrolliertes Zieldokument fehlt.", 404);
        Guard.Against(document.ProductId != c.ProductId || !document.Synthetic || document.Version != input.DocumentVersion,
            "document_scope", "Aktuelle synthetische Dokumentversion und Produktscope muessen explizit passen.");
        Guard.Against(document.Kind is not ("Specification" or "BenchTest" or "RiskFile" or "CER" or "PMCF" or "SSCP"),
            "controlled_document", "Kein kontrolliertes Produktdokument.");
        EvidenceRules.RequireIntegrity(document);
        await objects.Read(document.Hash, ct);
        Guard.Text(input.ProposedChange, "Konkreter Aenderungsvorschlag", 30);
        Guard.Text(input.Rationale, "Evidenzbezogene Begruendung", 30);
        var id = "change-" + Canonical.Hash(new { actorId = actor.Id, key })[..32];
        return await store.Execute<ChangeRequest>(new(id, "change", input.ProductId, 0, key, "ChangeProposed",
            input.Rationale, actor, input, clock.GetUtcNow()), _ => new(id, 1, input.ProductId, c.Id,
                document.Id, document.Version, input.ProposedChange, input.Rationale, "Proposed", null, null,
                actor.Id, c.Version, Canonical.Hash(c.Evidence)), ct);
    }

    public async Task<ChangeRequest> ApproveChange(string id, ChangeDecisionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var c = await store.Get<ChangeRequest>("change", id, ct) ?? throw new DomainException("change_missing", "Aenderungsauftrag fehlt.", 404);
        actor.RequireProduct(c.ProductId);
        Guard.Against(c.RequestedBy is null || c.RequestedBy == actor.Id, "change_review",
            "Die kontrollierte Aenderung braucht einen vom Antragsteller getrennten QM-Review.", 403);
        var document = await store.Get<Evidence>("evidence", c.DocumentId, ct);
        Guard.Against(document?.Version != c.DocumentVersion, "document_stale",
            "Zieldokument hat sich seit dem Vorschlag geaendert; Auftrag neu abstimmen.", 409);
        Guard.Text(input.Reason, "QM-Begruendung", 30);
        return await store.Execute<ChangeRequest>(new(id, "change", c.ProductId, input.ExpectedVersion, key,
            "ChangeApproved", input.Reason, actor, input, clock.GetUtcNow()), current =>
        {
            Guard.Against(current!.Status != "Proposed", "change_state", "Nur ein offener Vorschlag kann genehmigt werden.");
            return current with { Version = current.Version + 1, Status = "Approved", ApprovedBy = actor.Id };
        }, ct);
    }

    public async Task<ChangeRequest> CompleteChange(string id, ChangeDecisionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var c = await store.Get<ChangeRequest>("change", id, ct) ?? throw new DomainException("change_missing", "Aenderungsauftrag fehlt.", 404);
        actor.RequireProduct(c.ProductId);
        Guard.Text(input.ImplementationEvidence ?? "", "Umsetzungsnachweis", 40);
        var published = (await store.History<Evidence>("evidence", [c.ProductId], ct))
            .SingleOrDefault(e => e.Id == c.DocumentId && e.Version == c.DocumentVersion + 1 && e.ChangeId == c.Id);
        Guard.Against(published is null, "revision_required",
            "Die konkret genehmigte Dokumentfolgeversion muss zuerst kontrolliert veroeffentlicht werden.");
        await objects.Read(published!.Hash, ct);
        return await store.Execute<ChangeRequest>(new(id, "change", c.ProductId, input.ExpectedVersion, key,
            "ChangeImplemented", input.Reason, actor, input, clock.GetUtcNow()), current =>
        {
            Guard.Against(current!.Status != "Approved", "change_state", "Nur ein genehmigter Auftrag kann umgesetzt bestaetigt werden.");
            return current with { Version = current.Version + 1, Status = "Implemented",
                ImplementationEvidence = $"{input.ImplementationEvidence}\nRevision {published.Version}, SHA-256 {published.Hash}" };
        }, ct);
    }
}
