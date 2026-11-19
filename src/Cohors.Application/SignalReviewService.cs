using Cohors.Domain;

namespace Cohors.Application;

public sealed class SignalReviewService(IStateStore store, IObjectStore objects, TimeProvider clock)
{
    public async Task<SignalView[]> List(string status, Actor actor, CancellationToken ct = default)
    {
        Guard.Against(status is not ("Rejected" or "ReviewRequired" or "All"), "signal_filter", "Unbekannter Signalfilter.");
        var cases = await store.List<SafetyCase>("case", actor.Products, ct);
        var sources = await store.List<PublicSourceRun>("source", actor.Products, ct);
        var documents = new SignalDocumentLookup(sources);
        return cases.SelectMany(CaseViews)
            .Where(s => status == "All" || s.Status == status)
            .Concat(sources.SelectMany(run => SourceViews(run, documents, status)))
            .OrderByDescending(s => s.History.LastOrDefault()?.At ?? s.ReceivedAt)
            .ThenBy(s => s.OwnerId, StringComparer.Ordinal).ThenBy(s => s.SourceId, StringComparer.Ordinal).ToArray();
    }

    public async Task<SignalView> Decide(string kind, string id, string action, SignalDecisionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        Guard.Against(kind is not ("case" or "source"), "signal_kind", "Unbekannte Signalquelle.");
        Guard.Against(action is not ("Reject" or "Reopen" or "ConfirmRelevance"), "signal_action", "Unbekannte Signalentscheidung.");
        Guard.Text(input.Reason, "Fachliche Signalbegruendung", 40, 12000);
        Guard.Text(input.SourceId, "Originalquellreferenz", 1, 200);
        var at = clock.GetUtcNow();
        var role = actor.Roles.Contains(Roles.Medical) ? Roles.Medical : Roles.Regulatory;
        SignalDecision Decision(string hash) => new(action, "Reviewer", input.Reason, at,
            actor.Id, actor.Name, role, input.ExpectedVersion + 1, hash);
        CommandContext Context(string product) => new(id, kind, product, input.ExpectedVersion, key,
            action switch { "Reject" => "SignalRejected", "Reopen" => "SignalReopened", _ => "SignalRelevanceConfirmed" },
            input.Reason, actor, input, at);

        if (kind == "case")
        {
            var c = await store.Get<SafetyCase>("case", id, ct) ?? throw new DomainException("not_found", "Fall fehlt.", 404);
            actor.RequireProduct(c.ProductId);
            var context = Context(c.ProductId);
            if (await store.Receipt<SafetyCase>(context, ct) is { } receipt) return Select(CaseViews(receipt), input.SourceId);
            Guard.Against(c.Version != input.ExpectedVersion, "version_conflict", "Fallstand hat sich geaendert; Entscheidung neu pruefen.", 409);
            var original = c.Sources.SingleOrDefault(s => s.Id == input.SourceId)
                ?? throw new DomainException("signal_missing", "Originalsignal fehlt in dieser Fallakte.", 404);
            SignalReviewRules.Decide(c.SignalReviews, kind, id, original.Id, original.Hash, Decision(original.Hash));
            Evidence? rationale = null;
            if (action == "Reopen")
            {
                var text = "SYNTHETISCHE SIGNAL-WIEDERAUFNAHME: " + input.Reason;
                var hash = await objects.Put(text, ct);
                rationale = new("signal-reopen-" + Canonical.Hash(new { id, input.SourceId, key })[..24], 1, c.ProductId,
                    "Menschliche Signal-Wiederaufnahme", "Reopening", text, hash, "cohors:human-review",
                    "Signal-Wiederaufnahmebegruendung", "Synthetic", "Product", at, null, at, true, true, hash);
            }
            var saved = await store.Execute<SafetyCase>(context, current =>
            {
                var value = CaseService.Require(current);
                var source = value.Sources.Single(s => s.Id == input.SourceId);
                var reviews = SignalReviewRules.Decide(value.SignalReviews, kind, id, source.Id, source.Hash, Decision(source.Hash));
                var next = rationale is null ? value : SignalReviewRules.ResumeCase(value, rationale, at);
                return next with { Version = value.Version + 1, SignalReviews = reviews };
            }, ct);
            return Select(CaseViews(saved), input.SourceId);
        }

        var run = await store.Get<PublicSourceRun>("source", id, ct)
            ?? throw new DomainException("not_found", "Quellenlauf fehlt.", 404);
        actor.RequireProduct(run.ProductId);
        var command = Context(run.ProductId);
        if (await store.Receipt<PublicSourceRun>(command, ct) is { } previous)
            return await SourceView(previous, input.SourceId, ct);
        var updated = await store.Execute<PublicSourceRun>(command, current =>
        {
            var value = current ?? throw new DomainException("not_found", "Quellenlauf fehlt.", 404);
            Guard.Against(value.Status != "ReadOnlyMetadata" && value.Status != SignalDocumentRules.Status,
                "signal_source_unavailable", "Nur tatsaechlich gelesene Quellen koennen triagiert werden.", 409);
            if (value.Document is not null || value.Status == SignalDocumentRules.Status) SignalDocumentRules.RequireIntegrity(value);
            var item = value.Items.SingleOrDefault(i => i.ExternalId == input.SourceId)
                ?? throw new DomainException("signal_missing", "Konkreter Quelldatensatz fehlt.", 404);
            return value with { Version = value.Version + 1, SignalReviews = SignalReviewRules.Decide(value.SignalReviews,
                kind, id, item.ExternalId, item.ProvenanceHash, Decision(item.ProvenanceHash)) };
        }, ct);
        return await SourceView(updated, input.SourceId, ct);
    }

    private async Task<SignalView> SourceView(PublicSourceRun run, string sourceId, CancellationToken ct)
    {
        var documents = SignalDocumentLookup.IsDocument(run)
            ? SignalDocumentLookup.ForSource(run, await store.List<PublicSourceRun>("source", [run.ProductId], ct))
            : new SignalDocumentLookup([]);
        return Select(SourceViews(run, documents), sourceId);
    }

    private static SignalView Select(IEnumerable<SignalView> views, string id) =>
        views.Single(v => v.SourceId == id);

    private static IEnumerable<SignalView> CaseViews(SafetyCase c) => c.Sources.Select(s =>
    {
        var state = SignalReviewRules.Find(c.SignalReviews, s.Id, s.Hash);
        var original = c.Evidence.FirstOrDefault(e => e.Id == s.Id && e.Hash == s.Hash);
        if (original is not null) EvidenceRules.RequireIntegrity(original);
        return new SignalView("case", c.Id, c.Version, s.Id, c.ProductId, s.Title, s.ExternalId, s.Hash,
            s.Uri, s.ReceivedAt, s.AwarenessAt, c.Id, c.Reference, state?.Status ?? "Unreviewed",
            state?.ReviewReference, state?.History ?? [],
            original?.Content ?? "Originaltext hier nicht verfuegbar; Originalbeleg in der Fallakte pruefen.",
            SignalReviewRules.Boundary);
    });

    private static IEnumerable<SignalView> SourceViews(PublicSourceRun run, SignalDocumentLookup documents, string status = "All")
    {
        var prepared = documents.Get(run);
        var document = prepared?.Document;
        foreach (var s in run.Items)
        {
            var state = SignalReviewRules.Find(run.SignalReviews, s.ExternalId, s.ProvenanceHash);
            if (status != "All" && (state?.Status ?? "Unreviewed") != status) continue;
            yield return new SignalView("source", run.Id, run.Version, s.ExternalId, run.ProductId, s.Title,
                run.Source + ":" + s.ExternalId, s.ProvenanceHash, s.Url, run.At, document?.FirstKnownAt, null, null,
                state?.Status ?? "Unreviewed", state?.ReviewReference, state?.History ?? [], s.Summary,
                document is not null ? SignalDocumentRules.Boundary + " " + SignalReviewRules.Boundary :
                "Oeffentliche Vergleichsmetadaten, kein eigener Herstellerfall. Kenntnis eines konkreten Vorkommnisses ist nicht belegt; " +
                "Abruf-/Publikationszeit wird nicht als Awareness oder Fristbeginn ausgegeben. " + SignalReviewRules.Boundary,
                document, prepared is null ? null : documents.Matches(prepared));
        }
    }
}
