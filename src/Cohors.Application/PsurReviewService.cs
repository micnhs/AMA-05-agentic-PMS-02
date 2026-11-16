using Cohors.Domain;

namespace Cohors.Application;

public sealed class PsurReviewService(IStateStore store, AnalyticsService analytics, TimeProvider clock)
{
    public async Task<PsurReview[]> List(string psurId, Actor actor, CancellationToken ct = default)
    {
        var report = await analytics.GetPsur(psurId, actor, ct);
        var reviews = await store.List<PsurReview>("psurReview", actor.Products, ct);
        var visible = reviews.Where(r => r.OriginalReport.SeriesReference == report.Snapshot.Series?.Reference &&
            r.Products.All(actor.Products.Contains)).ToArray();
        foreach (var review in visible) PsurReviewRules.RequireScope(review, actor);
        return visible;
    }

    public async Task<PsurReview> Get(string id, Actor actor, CancellationToken ct = default)
    {
        var review = await store.Get<PsurReview>("psurReview", id, ct)
            ?? throw new DomainException("psur_review_missing", "BS-Rueckfrage fehlt.", 404);
        PsurReviewRules.RequireScope(review, actor);
        return review;
    }

    public async Task<PsurReview> Open(OpenPsurReviewCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var report = await SubmittedReport(input.PsurId, input.PsurVersion, actor, ct);
        Guard.Text(input.Reference, "Synthetische BS-Referenz", 8, 120);
        Guard.Against(!input.Reference.StartsWith("DEMO-", StringComparison.Ordinal) || input.Language is not ("de" or "en"),
            "psur_review_boundary", "DEMO-Referenz und ausdrueckliche Sprache de/en erforderlich.");
        var question = Question(1, input.Question, input.DueAt, input.ActionRequired, input.ScheduleBasis);
        Guard.Text(input.Reason, "Erfassungsbegruendung", 40, 4000);
        var id = "psur-review-" + Canonical.Hash(new { actorId = actor.Id, key })[..32];
        var source = PsurReviewRules.Bind(report);
        return await store.Execute<PsurReview>(new(id, "psurReview", report.ProductId, 0, key,
            "PsurQuestionSimulated", input.Reason, actor, input, clock.GetUtcNow())
            { Dependencies = [new("psur", source.Source)] }, _ => new(id, 1, report.ProductId, source.Products,
                input.Reference, source, "EU", input.Language, PsurReviewRules.Recipient,
                [question], "QuestionOpen", null, "", [], [], [], [], true), ct);
    }

    public async Task<PsurReview> Draft(string id, DraftPsurReplyCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var current = await Get(id, actor, ct);
        var command = Context(current, input.ExpectedVersion, key, "PsurReplyRevised", input.Reason, actor, input);
        if (await Replay(command, ct) is { } replay) return replay;
        var report = await SubmittedReport(input.PsurId, input.PsurVersion, actor, ct);
        var binding = PsurReviewRules.Bind(report);
        var original = current.OriginalReport;
        var originReport = await SubmittedReport(original.Source.Id, original.Source.Version, actor, ct);
        Guard.Against(binding.SeriesReference != original.SeriesReference || binding.Start != original.Start || binding.End != original.End ||
            binding.ReportNumber < original.ReportNumber || report.ProductId != current.ProductId ||
            report.Snapshot.Series!.Governance.Device.NotifiedBodyNumber != originReport.Snapshot.Series!.Governance.Device.NotifiedBodyNumber,
            "psur_reply_report", "Antwort braucht dieselbe Serie/Periode, das feste Leitprodukt und dieselbe BS; keine andere oder aeltere Berichtspflicht.");
        Guard.Text(input.Response, "Medizinisch-technische Antwort mit Begruendung", 80, 20000);
        Guard.Against(input.Response.Length > 20000, "psur_reply_length", "Antwort ist auf 20000 Zeichen begrenzt; Berichtsbytes bleiben separat unverkuerzt.");
        Guard.Text(input.Reason, "Antwort-/Aenderungsbegruendung", 40, 4000);
        var selections = input.Sources ?? [];
        var actions = input.Actions ?? [];
        Guard.Against(selections.Length is < 1 or > 32 || selections.Distinct().Count() != selections.Length ||
            actions.Length > 32 || actions.Distinct().Count() != actions.Length || actions.Any(a => a.Version < 1),
            "psur_reply_sources", "Eine bis 32 eindeutige gebundene Quellen und hoechstens 32 eindeutige Massnahmen waehlen.");
        var directory = PsurGroupRules.Snapshots(report.Snapshot).SelectMany(s => s.Directory?.Entries ?? [])
            .Concat(PsurRegulatoryRules.Directory(report.Snapshot.Group?.RegulatoryHistory))
            .DistinctBy(e => (e.Kind, e.Source)).ToArray();
        var sources = selections.Select(s => directory.SingleOrDefault(e => e.Kind == s.Kind && e.Source.Id == s.Id && e.Source.Version == s.Version)
            ?? throw new DomainException("psur_reply_sources", "Antwortquelle fehlt in der ausdruecklich gewaehlten eingefrorenen Berichtsfassung.")).ToArray();
        var products = current.Products.Concat(binding.Products).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var boundActions = new List<PsurReviewAction>();
        foreach (var selection in actions)
            boundActions.Add(await Action(selection, products, actor, ct));
        if (boundActions.Count == 0)
            Guard.Text(input.NoActionRationale, "Begruendung ohne Folgemassnahme", 40, 4000);
        Guard.Against(input.NoActionRationale?.Length > 4000, "invalid_field", "Zusatzbegruendung ist auf 4000 Zeichen begrenzt.");
        var draft = new PsurReviewDraft(binding, input.Response, sources, boundActions.ToArray(), input.NoActionRationale ?? "");
        return await store.Execute<PsurReview>(command
            with { Dependencies = [new("psur", binding.Source), .. boundActions.Select(a => new StoreDependency(a.Kind, a.Source))] }, value =>
        {
            var review = Require(value, actor);
            Guard.Against(review.Status is not ("QuestionOpen" or "ReplyDraft" or "MedicalApproved" or "ReplyApproved"),
                "psur_review_state", "Quittierte Antworten bleiben unveraendert; nur die naechste konkrete Rueckfrage eroeffnet eine weitere Runde.", 409);
            Guard.Against(review.Questions[^1].ActionRequired && boundActions.Count == 0,
                "psur_review_action", "Diese Rueckfrage verlangt eine nachvollziehbar verknuepfte Massnahme; ein Text allein schliesst sie nicht.");
            var next = review with { Version = review.Version + 1, Products = products, Draft = draft, Approvals = [], Status = "ReplyDraft" };
            return next with { ScopeHash = PsurReviewRules.Scope(next) };
        }, ct);
    }

    public async Task<PsurReview> Approve(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var current = await Get(id, actor, ct);
        var command = Context(current, input.ExpectedVersion, key, "PsurReplyApproved", input.Reason, actor, input);
        if (await Replay(command, ct) is { } replay) return replay;
        var dependencies = await ReplyDependencies(current, actor, ct);
        Guard.Text(input.Reason, "Fachliche Antwortfreigabe", 40, 4000);
        return await store.Execute<PsurReview>(command
            with { Dependencies = dependencies }, value =>
        {
            var review = Require(value, actor);
            Guard.Against(review.ScopeHash != PsurReviewRules.Scope(review) || review.Status is not ("ReplyDraft" or "MedicalApproved"),
                "psur_review_state", "Kein unveraenderter freizugebender Antwortentwurf.", 409);
            var role = review.Status == "ReplyDraft" ? Roles.Medical : Roles.Regulatory;
            actor.RequireHuman(role);
            Guard.Against(review.Approvals.Any(a => a.ActorId == actor.Id), "four_eyes", "Getrennte menschliche Antwortpruefer erforderlich.", 403);
            Guard.Against(role == Roles.Regulatory && !review.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == review.ScopeHash),
                "medical_required", "Aktuelle medizinische Antwortfreigabe fehlt.");
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, review.ScopeHash, clock.GetUtcNow(),
                "Konkrete synthetische BS-Antwort; keine neue Signatur auf unveraenderten PSUR-Bytes, keine Risikoakzeptanz.",
                input.Reason, null);
            return review with { Version = review.Version + 1, Approvals = [.. review.Approvals, approval],
                Status = role == Roles.Medical ? "MedicalApproved" : "ReplyApproved" };
        }, ct);
    }

    public async Task<PsurReview> Simulate(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var current = await Get(id, actor, ct);
        var command = Context(current, input.ExpectedVersion, key, "PsurReplySimulated", input.Reason, actor, input);
        if (await Replay(command, ct) is { } replay) return replay;
        var dependencies = await ReplyDependencies(current, actor, ct);
        Guard.Text(input.Reason, "Begruendung der Antwortsimulation", 40, 4000);
        return await store.Execute<PsurReview>(command
            with { Dependencies = dependencies }, value =>
        {
            var review = Require(value, actor);
            PsurReviewRules.RequireApproved(review);
            var question = review.Questions[^1];
            Guard.Against(review.Submissions.Any(s => s.Round == question.Round),
                "psur_reply_duplicate", "Diese Antwortrunde hat bereits eine synthetische Quittung.", 409);
            var submission = new PsurReviewSubmission(question.Round, question, review.Draft!, review.ScopeHash,
                review.Approvals, "DEMO-BS-REPLY-" + review.ScopeHash[..20], clock.GetUtcNow());
            return review with { Version = review.Version + 1, Status = "ReplySubmitted", Submissions = [.. review.Submissions, submission] };
        }, ct);
    }

    public async Task<PsurReview> Assess(string id, AssessPsurReplyCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var current = await Get(id, actor, ct);
        var command = Context(current, input.ExpectedVersion, key, "PsurReplyAssessed", input.Reason, actor, input);
        if (await Replay(command, ct) is { } replay) return replay;
        Guard.Against(current.Status != "ReplySubmitted", "psur_review_state", "Erst eine technisch quittierte simulierte Antwort bewerten.", 409);
        Guard.Against(input.Outcome is not ("FurtherResponseRequired" or "NoFurtherQuestion"),
            "psur_review_outcome", "Weitere Rueckfrage oder ausdruecklich dokumentierter Abschluss der Frage waehlen.");
        Guard.Text(input.Reason, "Simulierte BS-Bewertung mit Begruendung; keine Risikoakzeptanz", 80, 4000);
        var evidence = new List<PsurReviewAction>();
        foreach (var action in current.Submissions.SelectMany(s => s.Reply.Actions).DistinctBy(a => (a.Kind, a.Source.Id)))
            evidence.Add(await Action(new(action.Kind, action.Source.Id, 0), current.Products, actor, ct));
        Guard.Against(input.Outcome == "NoFurtherQuestion" &&
            evidence.Any(a => a.Status != (a.Kind == "capa" ? "Verified" : "Implemented")),
            "psur_review_open_actions", "Offene oder unwirksame verknuepfte Massnahmen verhindern den Fragenabschluss.", 409);
        PsurReviewQuestion? nextQuestion = input.Outcome == "FurtherResponseRequired"
            ? Question(current.Questions.Length + 1, input.NextQuestion ?? "", input.NextDueAt ?? default,
                input.NextActionRequired, input.NextScheduleBasis ?? "") : null;
        Guard.Against(nextQuestion is null && (input.NextQuestion is not null || input.NextDueAt is not null ||
            input.NextScheduleBasis is not null || input.NextActionRequired),
            "psur_review_outcome", "Beim Fragenabschluss keine versteckte weitere Frage/Frist erfassen.");
        return await store.Execute<PsurReview>(command
            with { Dependencies = evidence.Select(a => new StoreDependency(a.Kind, a.Source)).ToArray() }, value =>
        {
            var review = Require(value, actor);
            Guard.Against(review.Status != "ReplySubmitted", "psur_review_state", "Antwortrunde wurde bereits bewertet.", 409);
            var assessment = new PsurReviewAssessment(review.Questions[^1].Round, input.Outcome, input.Reason,
                evidence.ToArray(), actor.Id, clock.GetUtcNow());
            return review with { Version = review.Version + 1, Assessments = [.. review.Assessments, assessment],
                Questions = nextQuestion is null ? review.Questions : [.. review.Questions, nextQuestion],
                Status = nextQuestion is null ? "QuestionClosed" : "QuestionOpen",
                Draft = nextQuestion is null ? review.Draft : null, Approvals = nextQuestion is null ? review.Approvals : [],
                ScopeHash = nextQuestion is null ? review.ScopeHash : "" };
        }, ct);
    }

    private async Task<Psur> SubmittedReport(string id, int version, Actor actor, CancellationToken ct)
    {
        var report = await analytics.GetVerifiedPsur(id, actor, ct);
        Guard.Against(report.Version != version, "version_conflict", "Gewaehlte Berichtsfassung hat sich geaendert.", 409);
        var medical = report.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == report.ScopeHash);
        var regulatory = report.Approvals.SingleOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == report.ScopeHash);
        Guard.Against(report.Status != "SimulatedAccepted" || report.NotifiedBodyReceipt is null ||
            medical is null || regulatory is null || medical.ActorId == regulatory.ActorId,
            "psur_review_source", "Nur ein bereits getrennt freigegebener, synthetisch quittierter PSUR darf Grundlage der BS-Korrespondenz sein.", 409);
        return report;
    }

    private async Task<StoreDependency[]> ReplyDependencies(PsurReview review, Actor actor, CancellationToken ct)
    {
        var draft = review.Draft ?? throw new DomainException("psur_review_draft", "Antwortentwurf fehlt.");
        var report = await SubmittedReport(draft.Report.Source.Id, draft.Report.Source.Version, actor, ct);
        Guard.Against(Canonical.Hash(PsurReviewRules.Bind(report)) != Canonical.Hash(draft.Report),
            "psur_reply_report", "Gebundene PSUR-Fassung stimmt nicht mehr ueberein.", 409);
        var dependencies = new List<StoreDependency> { new("psur", draft.Report.Source) };
        foreach (var action in draft.Actions)
        {
            var current = await Action(new(action.Kind, action.Source.Id, action.Source.Version), review.Products, actor, ct);
            Guard.Against(current != action, "psur_reply_action_stale", "Massnahmeninhalt hat sich geaendert; Antwort neu begruendet binden.", 409);
            dependencies.Add(new(action.Kind, action.Source));
        }
        return dependencies.ToArray();
    }

    private async Task<PsurReviewAction> Action(PsurReviewSelection selection, string[] products, Actor actor, CancellationToken ct)
    {
        PsurReviewAction result;
        if (selection.Kind == "capa")
        {
            var value = await store.Get<Capa>("capa", selection.Id, ct)
                ?? throw new DomainException("capa_missing", "Verknuepfte CAPA fehlt.", 404);
            result = new("capa", new(value.Id, value.Version, Canonical.Hash(value)), value.ProductId, value.Title, value.Status);
        }
        else if (selection.Kind == "change")
        {
            var value = await store.Get<ChangeRequest>("change", selection.Id, ct)
                ?? throw new DomainException("change_missing", "Verknuepfter Aenderungsauftrag fehlt.", 404);
            result = new("change", new(value.Id, value.Version, Canonical.Hash(value)), value.ProductId, value.ProposedChange, value.Status);
        }
        else throw new DomainException("psur_review_action", "Nur bestehende CAPA oder kontrollierte Aenderungsauftraege verknuepfen.");
        actor.RequireProduct(result.ProductId);
        Guard.Against(!products.Contains(result.ProductId), "psur_review_action_scope", "Massnahme liegt ausserhalb der gebundenen PSUR-Mitgliedschaft.", 403);
        Guard.Against(selection.Version != 0 && result.Source.Version != selection.Version,
            "version_conflict", "Massnahmenfassung hat sich geaendert; neu laden.", 409);
        return result;
    }

    private PsurReviewQuestion Question(int round, string text, DateTimeOffset due, bool action, string basis)
    {
        Guard.Text(text, "Konkrete simulierte BS-Rueckfrage", 40, 8000);
        Guard.Text(basis, "Ausdrueckliche Terminvereinbarung; keine erfundene gesetzliche Pauschalfrist", 30, 2000);
        Guard.Against(text.Length > 8000 || basis.Length > 2000,
            "invalid_field", "Rueckfrage beziehungsweise Terminvereinbarung ueberschreitet die endliche Eingabegrenze.");
        Guard.Against(due == default || due.Offset != TimeSpan.Zero,
            "psur_review_due", "Expliziten vereinbarten UTC-Termin angeben; auch ein bereits verstrichener Eingang bleibt unveraendert faellig.");
        return new(round, text, due, action, basis, clock.GetUtcNow());
    }

    private CommandContext Context(PsurReview review, int version, string key, string action, string reason, Actor actor, object input) =>
        new(review.Id, "psurReview", review.ProductId, version, key, action, reason, actor, input, clock.GetUtcNow());
    private async Task<PsurReview?> Replay(CommandContext command, CancellationToken ct)
    {
        var receipt = await store.Receipt<PsurReview>(command, ct);
        if (receipt is not null) PsurReviewRules.RequireScope(receipt, command.Actor);
        return receipt;
    }
    private static PsurReview Require(PsurReview? value, Actor actor)
    {
        var review = value ?? throw new DomainException("psur_review_missing", "BS-Rueckfrage fehlt.", 404);
        PsurReviewRules.RequireScope(review, actor);
        return review;
    }
}
