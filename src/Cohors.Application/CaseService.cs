using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class CaseService(IStateStore store, IObjectStore objects, PolicyCatalog policies,
    IAgentRunner agents, IKnowledgeSearch knowledge, TimeProvider clock, IMirComposer mirComposer)
{
    private readonly AgentExecutionService execution = new(store, agents, clock);
    public static readonly Actor Workflow = new("cohors-workflow", "Cohors workflow", [Roles.Workflow], ["ortho", "cardio"], false);

    public async Task<SafetyCase> Get(string id, Actor actor, CancellationToken ct = default)
    {
        var c = await store.Get<SafetyCase>("case", id, ct)
            ?? throw new DomainException("not_found", "Fall nicht gefunden.", 404);
        actor.RequireProduct(c.ProductId);
        return c;
    }

    public async Task<EvidenceDocument> ReadEvidence(string id, string evidenceId, int? caseVersion,
        Actor actor, CancellationToken ct = default)
    {
        var current = await Get(id, actor, ct);
        var selected = caseVersion is null || caseVersion == current.Version ? current :
            (await store.History<SafetyCase>("case", [current.ProductId], ct))
                .SingleOrDefault(c => c.Id == id && c.Version == caseVersion)
                ?? throw new DomainException("case_version_missing", "Die ausgewaehlte Fallfassung fehlt.", 404);
        var evidence = selected.Evidence.SingleOrDefault(e => e.Id == evidenceId)
            ?? throw new DomainException("evidence_missing", "Fundstelle fehlt in dieser Fallfassung.", 404);
        return await EvidenceService.ReadOriginal(evidence, objects, ct);
    }

    public async Task<CaseProcessView> Process(string id, Actor actor, CancellationToken ct = default)
    {
        var c = await Get(id, actor, ct);
        var revised = 0;
        var requested = 0;
        if (c.CurrentAssessment?.Mode == "Human")
        {
            var audit = (await store.Audit(id, ct)).Where(e => e.Version <= c.Version).ToArray();
            revised = audit.Where(e => e.Action == "AssessmentRevised").Select(e => e.Version).DefaultIfEmpty(0).Max();
            requested = audit.Where(e => e.Action == "AssessmentRequested").Select(e => e.Version).DefaultIfEmpty(0).Max();
        }
        SafetyCase? source = null;
        if (c.Decisions.LastOrDefault() is { Disposition: Disposition.NonReportable } decision && c.CurrentAssessment is not null)
            source = (await store.History<SafetyCase>("case", [c.ProductId], ct))
                .Where(v => v.Id == id && v.Version <= c.Version && v.Decisions.LastOrDefault()?.Id == decision.Id)
                .MinBy(v => v.Version);
        return ProjectProcess(new(c, revised, requested, source));
    }

    public static CaseProcessView ProjectProcess(CaseProcessInput input)
    {
        var c = input.Case;
        var humanReplacesRun = c.CurrentAssessment?.Mode == "Human" && input.LastAssessmentRevision > input.LastAssessmentRequest;
        var decisionCurrent = false;
        var decisionApproved = false;
        if (c.Decisions.LastOrDefault() is { Disposition: Disposition.NonReportable } decision && c.CurrentAssessment is { } assessment)
        {
            var source = input.DecisionSource;
            if (source is not null && source.Id == c.Id && source.ProductId == c.ProductId &&
                source.Version <= c.Version && source.Decisions.LastOrDefault()?.Id == decision.Id)
            {
                var obligations = source.Deadlines.Where(d => d.Status == "Open").ToArray();
                decisionCurrent = source.Jurisdiction == c.Jurisdiction && decision.AssessmentVersion == assessment.Version &&
                    decision.ScopeHash == DispositionScope(source, assessment, decision.Disposition, decision.Rationale, decision.ExpectedSideEffect) &&
                    obligations.All(before => c.Deadlines.Any(after => after.Id == before.Id &&
                        after.Status is "Open" or "NonReportableReviewed" && Canonical.Hash(after with { Status = "Open" }) == Canonical.Hash(before))) &&
                    c.Deadlines.Where(d => d.Status == "Open").All(d => obligations.Any(before => before.Id == d.Id));
                decisionApproved = decisionCurrent && c.Disposition == Disposition.NonReportable && !c.Deadlines.Any(d => d.Status == "Open") &&
                    c.Approvals.Any(a => a.Role == Roles.Regulatory && a.ScopeHash == decision.ScopeHash && a.ActorId != decision.ActorId);
            }
        }
        return CaseProcess.Read(c, new(humanReplacesRun, decisionCurrent, decisionApproved));
    }

    private CommandContext Context(SafetyCase c, int version, string key, string action, string reason, Actor actor, object request) =>
        new(c.Id, "case", c.ProductId, version, key, action, reason, actor, request, clock.GetUtcNow());

    public async Task<SafetyCase> Register(RegisterComplaint input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.Require(Roles.Pms, Roles.Workflow);
        actor.RequireProduct(input.ProductId);
        policies.Device(input.ProductId);
        var calendar = policies.Calendar(input.Jurisdiction);
        Guard.Against(!input.Synthetic, "synthetic_only", "Der Pilot nimmt ausschliesslich synthetische Beschwerden an.");
        Guard.Text(input.IncidentKey, "Vorfallreferenz", 3, 100);
        Guard.Text(input.SourceId, "Quellreferenz", 3, 100);
        Guard.Text(input.Title, "Titel", 5, 200);
        Guard.Text(input.Description, "Beschreibung", 20, 12000);
        var now = clock.GetUtcNow();
        Guard.Against(input.AwarenessAt > now || input.OccurredAt > input.AwarenessAt, "awareness_order",
            "Ereignis, erste Kenntnis und Systemeingang muessen zeitlich plausibel sein.");
        var id = Canonical.Hash(new { manufacturer = "cohors-synthetic", input.ProductId, input.IncidentKey })[..32];
        var existing = await store.Get<SafetyCase>("case", id, ct);
        if (existing?.Sources.Any(s => s.ExternalId == input.SourceId) == true)
        {
            Guard.Against(existing.Sources.First(s => s.ExternalId == input.SourceId).Hash != Canonical.HashText(input.Description),
                "source_revision", "Gleiche Quell-ID mit geaendertem Inhalt: als neue Quellversion erfassen.", 409);
            return existing;
        }
        var hash = await objects.Put(input.Description, ct);
        var sourceId = "src-" + Canonical.HashText(input.SourceId)[..24];
        var source = new SourceReference(sourceId, input.SourceId, "SyntheticComplaint", input.Title,
            "cohors:synthetic:" + input.SourceId, hash, now, input.AwarenessAt, "Explizite synthetische Vorfallreferenz");
        var evidence = new Evidence(sourceId, 1, input.ProductId, input.Title, "Complaint", input.Description,
            hash, source.Uri, "Beschwerde: Originaltext", "Synthetic demo, no patient", "Product",
            input.OccurredAt ?? input.AwarenessAt, null, now, true, true, hash);
        var awareness = new AwarenessEvent(Guid.NewGuid().ToString("N"), "Organizational", input.AwarenessAt, now,
            input.SourceId, "Erster Wissenseingang beim Mitarbeiter/Vertreter", actor.Id);
        var baseline = EvidenceRules.Effective(await store.History<Evidence>("evidence", [input.ProductId], ct),
            input.OccurredAt ?? input.AwarenessAt);
        return await store.Execute<SafetyCase>(new(id, "case", input.ProductId, existing?.Version ?? 0, key,
            "ComplaintRegistered", "Synthetischer Hinweis aufgenommen; Ursprungswissen erhalten.", actor, input, now), current =>
        {
            if (current is not null)
            {
                var first = input.AwarenessAt < current.FirstAwarenessAt ? input.AwarenessAt : current.FirstAwarenessAt;
                var deadlines = current.Deadlines.Select(d => d.TriggerAt == current.FirstAwarenessAt
                    ? DeadlineCalculator.ProtectEarlier(d, DeadlineCalculator.Calculate(d.Jurisdiction, d.Seriousness, first, policies.Calendar(d.Jurisdiction))) with { Id = d.Id }
                    : d).ToArray();
                var updated = current.Stage == CaseStage.Closed ? current : Invalidate(current);
                return updated with
                {
                    Version = current.Version + 1, Sources = [.. current.Sources, source],
                    Evidence = [.. current.Evidence, evidence], Awareness = [.. current.Awareness, awareness],
                    FirstAwarenessAt = first, Deadlines = deadlines, Stage = current.Stage == CaseStage.Closed ? CaseStage.Closed : CaseStage.AwaitingMedicalReview,
                    Alerts = [.. current.Alerts, "Doppelter Eingang: ein Vorfall, beide Originalquellen erhalten. Abgeschlossene Pflicht bleibt erledigt; neue Information menschlich triagieren."]
                };
            }
            return new SafetyCase(id, 1, "COH-" + input.IncidentKey, input.ProductId, input.Title, input.Description,
                input.Udi, input.Batch, input.Country, input.Jurisdiction, input.Seriousness, input.OccurredAt,
                input.AwarenessAt, now, null, input.Scenario, true, CaseStage.Intake, Disposition.Undetermined,
                [source], [awareness], [DeadlineCalculator.Calculate(input.Jurisdiction, input.Seriousness, input.AwarenessAt, calendar)],
                [evidence, .. baseline], [], [], [], [], [], [], [], input.Udi is null ? ["UDI unbekannt; Produktzuordnung ist zu bestaetigen."] : []);
        }, ct);
    }

    public async Task<SafetyCase> RequestAssessment(string id, AssessmentCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "AssessmentRequested", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            Guard.Against(value.AgentRuns.Any(r => r.Status is "Queued" or "Running"), "agent_busy", "Ein Lauf ist bereits aktiv.", 409);
            var run = new AgentRun(Guid.NewGuid().ToString("N"), AgentRole.CausalityRisk, input.Mode, "Queued",
                clock.GetUtcNow(), clock.GetUtcNow(), input.Mode == AgentMode.Live ? "configured-foundry" : "replay-2026.1",
                "assessment-1", Canonical.Hash(value.Evidence), null, value.Evidence.Select(e => e.Id).ToArray(),
                ["knowledge.retrieve"], null, 0, null);
            return Invalidate(value) with { Version = value.Version + 1, Stage = CaseStage.Assessing, AgentRuns = [.. value.AgentRuns, run] };
        }, ct);
    }

    public async Task<SafetyCase> ProcessAssessment(string id, string runId, CancellationToken ct = default)
    {
        var c = await Get(id, Workflow, ct);
        var run = c.AgentRuns.SingleOrDefault(r => r.Id == runId)
            ?? throw new DomainException("run_missing", "Agentenauftrag fehlt.", 404);
        if (run.Status is "Completed" or "Failed" or "Abandoned") return c;
        Guard.Against(run.Status != "Queued", "run_in_progress",
            "Lauf ist aktiv oder Ergebnis unklar; kein verdeckter erneuter Modellaufruf.", 409);
        var owner = Guid.NewGuid().ToString("N");
        c = await store.Execute<SafetyCase>(Context(c, c.Version, "claim-" + runId, "AgentStarted",
            "Persistenter Agentenauftrag beansprucht; keine Freigaberechte.", Workflow, new { runId }), current =>
        {
            var value = Require(current);
            return value with { Version = value.Version + 1, AgentRuns = value.AgentRuns.Select(r => r.Id == runId ? r with { Status = "Running", ExecutionId = owner } : r).ToArray() };
        }, ct);
        Guard.Against(c.AgentRuns.Single(r => r.Id == runId).ExecutionId != owner, "run_in_progress",
            "Eine andere Activity besitzt den Lauf. Ein idempotenter Claim ist keine zweite Ausfuehrungsberechtigung.", 409);
        try
        {
            var retrieved = await knowledge.Retrieve(c.ProductId, c.OccurredAt ?? c.FirstAwarenessAt, c.Title, Workflow, ct);
            var evidence = c.Evidence.Concat(retrieved).GroupBy(e => e.Id).Select(g => g.First()).ToArray();
            foreach (var item in evidence)
            {
                EvidenceRules.RequireIntegrity(item);
                await objects.Read(item.Hash, ct);
            }
            var ingestion = await execution.Run(runId + "-ingestion", c.ProductId, new(AgentRole.Ingestion, run.Mode, c, null, null, evidence, "Produktzuordnung und Quellenunabhaengigkeit pruefen.", 0), ct);
            await RequireRunCurrent(c, runId, owner, ct);
            var output = await execution.Run(runId + "-assessment", c.ProductId, new(AgentRole.CausalityRisk, run.Mode, c, null, null, evidence, "Begruendeten medizinisch-technischen Entwurf erstellen.", 0), ct);
            var content = output.Assessment ?? throw new DomainException("model_output", "Strukturiertes Assessment fehlt.");
            var assessmentVersion = (c.CurrentAssessment?.Version ?? 0) + 1;
            var proposed = new Assessment(assessmentVersion, clock.GetUtcNow(), "agent:CausalityRisk", run.Mode.ToString(),
                output.PromptVersion, output.Model, evidence.Select(e => e.Id).ToArray(), Canonical.Hash(evidence), content, null);
            await RequireRunCurrent(c, runId, owner, ct);
            var assurance = await execution.Run(runId + "-assurance", c.ProductId, new(AgentRole.EvidenceAssurance, run.Mode,
                c with { Assessments = [.. c.Assessments, proposed], Evidence = evidence }, null, null,
                evidence, "Unabhaengig Belege, Gegenhypothesen und Datenluecken pruefen.", 0), ct);
            var findings = AssessmentRules.Check(content, evidence).Concat(assurance.Findings)
                .DistinctBy(f => new { f.Code, f.ClaimId }).ToArray();
            var quality = new QualityReview(Guid.NewGuid().ToString("N"), assessmentVersion, clock.GetUtcNow(),
                runId + "-assurance", findings, assurance.Narrative);
            var assessment = proposed with { Quality = quality };
            return await store.Execute<SafetyCase>(Context(c, c.Version, "result-" + runId, "AssessmentDrafted",
                "Entwurf und unabhaengige Assurance gespeichert; menschliche Entscheidung steht aus.", Workflow, new { runId, assessment }), current =>
            {
                var value = Require(current);
                var final = run with { Status = "Completed", FinishedAt = clock.GetUtcNow(), Model = output.Model, ExecutionId = owner,
                    OutputHash = Canonical.Hash(output), OutputJson = Canonical.Serialize(output), EvidenceIds = assessment.EvidenceIds };
                AgentRun Subrun(AgentRole role, AgentOutput result) => final with
                {
                    Id = runId + "-" + role, Role = role, OutputHash = Canonical.Hash(result), OutputJson = Canonical.Serialize(result), Tools = result.Tools
                };
                return value with { Version = value.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                    Evidence = evidence, Assessments = [.. value.Assessments, assessment],
                    AgentRuns = [.. value.AgentRuns.Select(r => r.Id == runId ? final : r),
                        Subrun(AgentRole.Ingestion, ingestion), Subrun(AgentRole.EvidenceAssurance, assurance)] };
            }, ct);
        }
        catch (Exception ex) when (ex is DomainException or HttpRequestException or TimeoutException or
            AgentExecutionException or OperationCanceledException or System.Text.Json.JsonException)
        {
            var latest = await Get(id, Workflow, CancellationToken.None);
            if (latest.AgentRuns.Single(r => r.Id == runId).Status == "Abandoned") return latest;
            return await store.Execute<SafetyCase>(Context(latest, latest.Version, "failure-" + runId, "AgentFailed",
                "Agent/Knowledge fehlgeschlagen. Kein Fixture-Fallback; menschlicher Vorlaeufigkeitsweg bleibt verfuegbar.", Workflow,
                new { runId, code = ex is DomainException d ? d.Code : ex.GetType().Name }), current =>
            {
                var value = Require(current);
                return value with { Version = value.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                    AgentRuns = value.AgentRuns.Select(r => r.Id == runId ? r with { Status = "Failed",
                        FinishedAt = clock.GetUtcNow(), ErrorCode = ex is DomainException d ? d.Code : ex.GetType().Name } : r).ToArray(),
                    Alerts = [.. value.Alerts, "KI/Knowledge-Ausfall: Frist laeuft weiter. Menschlichen Mindestbericht vorbereiten."] };
            }, CancellationToken.None);
        }
    }

    public async Task<SafetyCase> Revise(string id, ReviseAssessmentCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "AssessmentRevised", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var version = (value.CurrentAssessment?.Version ?? 0) + 1;
            var quality = new QualityReview(Guid.NewGuid().ToString("N"), version, clock.GetUtcNow(), "deterministic-human-edit",
                AssessmentRules.Check(input.Content, value.Evidence), "Deterministische Quellenpruefung; Human-Edit ist keine KI-Qualitaetsfreigabe.");
            var assessment = new Assessment(version, clock.GetUtcNow(), actor.Id, "Human", "human-edit-1", "none",
                value.Evidence.Select(e => e.Id).ToArray(), Canonical.Hash(value.Evidence), input.Content, quality);
            return Invalidate(value) with { Version = value.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                Assessments = [.. value.Assessments, assessment] };
        }, ct);
    }

    public async Task<SafetyCase> Manual(string id, ManualAssessmentCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        var c = await Get(id, actor, ct);
        return await Revise(id, new(input.ExpectedVersion, ManualAssessment.Create(c, input), input.Reason), actor, key, ct);
    }

    public async Task<SafetyCase> Prepare(string id, PrepareReportCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        var context = Context(c, input.ExpectedVersion, key, "ReportDrafted", input.Reason, actor, input);
        var receipt = await store.Receipt<SafetyCase>(context, ct);
        if (receipt is not null) return receipt;
        Guard.Against(c.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
        Guard.Against(c.Version != input.ExpectedVersion, "version_conflict", "Fall wurde geaendert; vor einem Modellaufruf neu laden.", 409);
        var assessment = c.CurrentAssessment ?? throw new DomainException("assessment_required", "Zuerst einen KI- oder menschlichen Entwurf erstellen.");
        ReportRules.RequireNoUnknown(c, input.Jurisdiction);
        Guard.Against(assessment.EvidenceHash != Canonical.Hash(c.Evidence), "assessment_stale",
            "Neue Evidenz erfordert eine erneute Bewertung vor dem Berichtsentwurf.", 409);
        Guard.Against(!new[] { "Initial", "FollowUp", "Final" }.Contains(input.Kind), "report_kind", "Unbekannte Meldeart.");
        Guard.Against(input.Kind == "Final" && input.Preliminary, "final_preliminary", "Abschlussmeldung darf nicht als vorlaeufig ausgegeben werden.");
        Guard.Against(input.Language is not ("en" or "de"), "report_language", "Der Pilot unterstuetzt Deutsch/Englisch; weitere Sprachen brauchen validierte Vorlagen.");
        Guard.Against(!c.Deadlines.Any(d => d.Jurisdiction == input.Jurisdiction), "jurisdiction_scope",
            "Meldejurisdiktion muss einer dokumentierten Fallverpflichtung entsprechen.");
        Guard.Against(input.Kind != "Initial" && !c.Submissions.Any(s => s.Status == "SimulatedAccepted" &&
            c.Reports.Any(r => r.ScopeHash == s.ReportScopeHash && r.Jurisdiction == input.Jurisdiction)),
            "initial_required", "Folgemeldung erfordert einen abgeglichenen Vorbericht derselben Jurisdiktion.");
        var obligations = c.Deadlines.Where(d => d.Jurisdiction == input.Jurisdiction && d.Status == "Open").Select(d => d.Id).ToArray();
        if (input.Kind != "Initial" && obligations.Length == 0)
            obligations = c.Reports.LastOrDefault(r => r.Jurisdiction == input.Jurisdiction && r.State == ReportState.SimulatedAccepted)?.ObligationIds ?? [];
        Guard.Against(obligations.Length == 0, "obligation_required", "Keine passende regulatorische Verpflichtung fuer diesen Bericht.");
        Guard.Against(input.Kind == "Initial" && c.Submissions.Any(s => s.Status == "SimulatedAccepted" &&
            c.Reports.Any(r => r.ScopeHash == s.ReportScopeHash && r.ObligationIds?.Intersect(obligations).Any() == true)),
            "initial_already_reported", "Bereits gemeldete Verpflichtung braucht einen begruendeten Folgebericht, keine zweite Erstmeldung.", 409);
        var governance = await store.Get<ProductGovernance>("productGovernance", c.ProductId, ct);
        if (governance is not null)
        {
            GovernanceRules.RequireValid(governance, policies.Device(c.ProductId));
            context = context with { Dependencies = [new("productGovernance", new(governance.Id, governance.Version, Canonical.Hash(governance)))] };
        }
        var mode = assessment.Mode switch
        {
            "Human" or "Replay" => AgentMode.Replay, "Live" => AgentMode.Live, "Failure" => AgentMode.Failure,
            _ => throw new DomainException("agent_mode", "Assessment-Modus ist unbekannt; kein stiller Replay-Ersatz.")
        };
        var started = clock.GetUtcNow();
        var authored = assessment.Mode == "Human"
            ? new AgentOutput("none", "human-report-2", null, VigilanceNarrative.FromAssessment(c, input.Language), [], [],
                VigilanceNarrative.Fields(c, input.Language))
            : await execution.Run(Canonical.Hash(new { actorId = actor.Id, caseId = id, key, input }), c.ProductId, new(AgentRole.VigilanceAuthoring, mode, c, null, null, c.Evidence,
                "Bericht und strukturierte Vigilance-Texte aus bestehendem Assessment mit exakten Claim-Referenzen; keine neuen Sicherheitsbehauptungen, Fakten oder Termine.", 0, input.Language), ct);
        var now = clock.GetUtcNow();
        var run = new AgentRun(Guid.NewGuid().ToString("N"), AgentRole.VigilanceAuthoring, mode, "Completed",
            started, now, authored.Model, authored.PromptVersion, Canonical.Hash(assessment), Canonical.Hash(authored),
            assessment.EvidenceIds, authored.Tools, null, 0, Canonical.Serialize(authored));
        var report = new ReportDraft((c.CurrentReport?.Version ?? 0) + 1, assessment.Version, input.Kind,
            input.Jurisdiction, PolicyCatalog.Recipient(input.Jurisdiction), input.Language,
            input.Jurisdiction == "GB" ? "GB-SIM-1" : "MIR-7.3.1-SB11154", input.Jurisdiction == "GB" ? "GB-SIM-1" : "MIR-7.3.1-SB11252",
            authored.Narrative, input.Preliminary, input.OpenQuestions, DeadlineCalculator.RuleVersion,
            assessment.EvidenceHash, c.Evidence.Select(e => e.Hash).Order().ToArray(), ReportState.Draft, "", null, obligations);
        report = report with { FollowUpObligationIds = input.Kind == "Initial" ? [] :
            (c.FollowUps ?? []).Where(f => f.Jurisdiction == input.Jurisdiction && f.Status == "Open").Select(f => f.Id).ToArray() };
        var prefill = ReportPrefill.Create(c, report, policies.Device(c.ProductId), governance, policies.Calendar(input.Jurisdiction),
            authored.Vigilance ?? throw new DomainException("vigilance_fields", "Autorenlauf lieferte keine pruefbaren strukturierten Meldungstexte."), run.Id, now);
        report = report with { Preparation = prefill.Preparation };
        if (prefill.Preparation.Target.MirSupported)
        {
            var mir = mirComposer.Build(c, report, policies.Device(c.ProductId), policies.Calendar(input.Jurisdiction), prefill.Facts, now);
            await objects.Put(mir.Xml, ct);
            report = report with { Mir = mir, AttachmentHashes = report.AttachmentHashes.Append(mir.XmlHash).Order(StringComparer.Ordinal).ToArray() };
        }
        report = report with { ScopeHash = ReportRules.Scope(assessment, report) };
        return await store.Execute<SafetyCase>(context, current =>
        {
            var value = Require(current);
            ReportRules.RequireNoUnknown(value, input.Jurisdiction);
            return Invalidate(value) with { Version = value.Version + 1, Reports = [.. Supersede(value.Reports), report],
                AgentRuns = [.. value.AgentRuns, run] };
        }, ct);
    }

    public async Task<SafetyCase> Approve(string id, ApprovalCommand input, Actor actor, string key, bool medical, CancellationToken ct = default)
    {
        actor.RequireHuman(medical ? Roles.Medical : Roles.Regulatory);
        var c = await Get(id, actor, ct);
        var context = Context(c, input.ExpectedVersion, key, medical ? "MedicalApproved" : "RegulatoryApproved", input.Reason, actor, input);
        if (await store.Receipt<SafetyCase>(context, ct) is { } replay) return replay;
        return await store.Execute<SafetyCase>(context with { Dependencies = await ReportDependencies(c.CurrentReport, ct) }, current =>
        {
            var value = Require(current);
            var approval = medical
                ? ReportRules.ApproveMedical(value, actor, input.Reason, input.FindingDisposition, clock.GetUtcNow())
                : ReportRules.ApproveRegulatory(value, actor, input.Reason, clock.GetUtcNow());
            var report = value.CurrentReport!;
            var next = report with { State = medical ? ReportState.MedicalApproved : ReportState.Frozen };
            if (!medical) next = next with { FrozenJson = Canonical.Serialize(report with { State = ReportState.Frozen }) };
            var decision = new MedicalDecision(Guid.NewGuid().ToString("N"), Disposition.Reportable,
                value.CurrentAssessment!.Version, report.ScopeHash, actor.Id, clock.GetUtcNow(), input.Reason);
            return value with { Version = value.Version + 1, Disposition = Disposition.Reportable, Stage = CaseStage.Investigating,
                Approvals = [.. value.Approvals, approval], Decisions = medical ? [.. value.Decisions, decision] : value.Decisions,
                Reports = [.. value.Reports.SkipLast(1), next] };
        }, ct);
    }

    public async Task<SafetyCase> ReturnReport(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        Guard.Text(input.Reason, "Konkrete Korrekturanforderung und benoetigte Belege", 40, 4000);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "ReportReturnedForCorrection",
            input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var report = value.CurrentReport ?? throw new DomainException("report_required", "Kein zurueckzugebender Bericht.");
            ReportRules.RequireNoUnknown(value, report.Jurisdiction);
            Guard.Against(report.State is not (ReportState.Draft or ReportState.MedicalApproved or ReportState.Frozen),
                "report_state", "Nur einen aktuellen ungesendeten Entwurf zur Korrektur zurueckgeben.", 409);
            Guard.Against(report.ReviewReturn is not null, "report_correction_required", "Diese Fassung ist bereits zur Korrektur zurueckgegeben.", 409);
            var role = actor.Roles.Contains(Roles.Regulatory) ? Roles.Regulatory : Roles.Medical;
            var returned = report with { Version = report.Version + 1, State = ReportState.Draft, FrozenJson = null,
                ReviewReturn = new(actor.Id, role, clock.GetUtcNow(), input.Reason, report.ScopeHash) };
            returned = returned with { ScopeHash = ReportRules.Scope(value.CurrentAssessment!, returned) };
            return Invalidate(value) with { Version = value.Version + 1,
                Reports = [.. Supersede(value.Reports), returned], Stage = CaseStage.Investigating };
        }, ct);
    }

    public async Task<StoreDependency[]> ReportDependencies(ReportDraft? report, CancellationToken ct = default)
    {
        if (report?.Preparation?.Governance is not { } bound) return [];
        var current = await store.Get<ProductGovernance>("productGovernance", bound.Id, ct);
        Guard.Against(current is null || current.Version != bound.Version || Canonical.Hash(current) != Canonical.Hash(bound),
            "report_profile_stale", "Das fuehrende Produkt-/Herstellerprofil wurde geaendert. Entwurf neu vorbereiten und getrennt freigeben.", 409);
        return [new("productGovernance", new(bound.Id, bound.Version, Canonical.Hash(bound)))];
    }

    public async Task<SafetyCase> Submit(string id, SubmissionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        Guard.Against(!new[] { "Accept", "Reject", "Unknown" }.Contains(input.Scenario), "simulator_scenario", "Ungueltiges Simulatorszenario.");
        var c = await Get(id, actor, ct);
        var context = Context(c, input.ExpectedVersion, key, "SimulatedSubmission", input.Reason, actor, input);
        if (await store.Receipt<SafetyCase>(context, ct) is { } replay) return replay;
        return await store.Execute<SafetyCase>(context with { Dependencies = await ReportDependencies(c.CurrentReport, ct) }, current =>
        {
            var value = Require(current);
            ReportRules.RequireSendable(value, actor);
            var report = value.CurrentReport!;
            var state = input.Scenario switch { "Accept" => ReportState.SimulatedAccepted, "Reject" => ReportState.Rejected, _ => ReportState.TransmissionUnknown };
            var submissionId = Guid.NewGuid().ToString("N");
            var submission = new Submission(submissionId, report.ScopeHash, report.Version, "SIM-" + submissionId,
                state.ToString(), input.Scenario, clock.GetUtcNow(), null, input.Scenario == "Accept" ? "DEMO-RECEIPT-" + submissionId[..12] : null,
                true, 1, input.Scenario == "Reject" ? "BusinessRejected" : "BusinessAccepted");
            return value with { Version = value.Version + 1, Submissions = [.. value.Submissions, submission],
                Reports = [.. value.Reports.SkipLast(1), report with { State = state }],
                FollowUps = input.Scenario == "Accept"
                    ? FollowUpRules.Acknowledge(value, report, submission.ReceiptReference!, clock.GetUtcNow()) : value.FollowUps,
                Deadlines = input.Scenario == "Accept" ? value.Deadlines.Select(d => report.ObligationIds!.Contains(d.Id)
                    ? d with { Status = "SimulatedReported" } : d).ToArray() : value.Deadlines };
        }, ct);
    }

    public async Task<SafetyCase> Reconcile(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "SubmissionReconciled", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var submission = value.Submissions.LastOrDefault(s => s.Status == "TransmissionUnknown")
                ?? throw new DomainException("no_unknown", "Keine ungeklaerte Uebermittlung vorhanden.");
            Guard.Against(submission.ReceiverStatus != "BusinessAccepted", "reconciliation_pending",
                "Simulator hat keinen passenden fachlichen Empfang bestaetigt; manuelle Klaerung erforderlich.", 409);
            var reconciled = submission with { Status = "SimulatedAccepted", ReconciledAt = clock.GetUtcNow(),
                ReceiptReference = "DEMO-RECONCILED-" + submission.Id[..12] };
            var sent = value.Reports.Single(r => r.ScopeHash == submission.ReportScopeHash);
            var covered = ReportRules.CoveredObligations(value, sent);
            return value with { Version = value.Version + 1,
                Submissions = value.Submissions.Select(s => s.Id == submission.Id ? reconciled : s).ToArray(),
                Reports = value.Reports.Select(r => r.ScopeHash == submission.ReportScopeHash ? r with { State = ReportState.SimulatedAccepted } : r).ToArray(),
                FollowUps = FollowUpRules.Acknowledge(value, sent, reconciled.ReceiptReference!, clock.GetUtcNow()),
                Deadlines = value.Deadlines.Select(d => covered.Contains(d.Id)
                    ? d with { Status = "SimulatedReported" } : d).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> Decide(string id, DispositionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        Guard.Against(input.Disposition is Disposition.Undetermined or Disposition.Reportable, "decision_path",
            "Meldepflichtige Pakete brauchen den getrennten medizinischen und RA-Freigabeweg.");
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "MedicalDisposition", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var assessment = value.CurrentAssessment ?? throw new DomainException("assessment_required", "Begruendetes Assessment fehlt.");
            Guard.Text(input.Reason, "Medizinisch-regulatorische Begruendung", 40);
            if (input.Disposition == Disposition.NonReportable) AssessmentRules.RequireReviewable(assessment, false, null);
            Guard.Against(assessment.EvidenceHash != Canonical.Hash(value.Evidence), "assessment_stale", "Aktuelle Evidenz muss vor der Entscheidung bewertet werden.", 409);
            if (input.ExpectedSideEffect is not null)
            {
                Guard.Against(input.Disposition != Disposition.NonReportable, "expected_effect_decision", "Ausnahme nur im begruendeten Nichtmeldeweg.");
                EvidenceRules.RequireExpectedSideEffect(input.ExpectedSideEffect, value.Evidence, policies.Trend.Version);
            }
            var decision = new MedicalDecision(Guid.NewGuid().ToString("N"), input.Disposition, assessment.Version,
                DispositionScope(value, assessment, input.Disposition, input.Reason, input.ExpectedSideEffect), actor.Id, clock.GetUtcNow(),
                input.Reason, input.ExpectedSideEffect);
            return Invalidate(value) with { Version = value.Version + 1, Disposition = input.Disposition == Disposition.NonReportable ? Disposition.Undetermined : input.Disposition,
                Stage = input.Disposition == Disposition.Investigate ? CaseStage.Investigating : CaseStage.Monitoring,
                Decisions = [.. value.Decisions, decision] };
        }, ct);
    }

    private static string DispositionScope(SafetyCase c, Assessment assessment, Disposition disposition, string reason,
        ExpectedSideEffectException? expectedSideEffect = null)
    {
        var content = new { c.ProductId, c.Jurisdiction, assessment, disposition, reason,
            obligations = c.Deadlines.Where(d => d.Status == "Open").OrderBy(d => d.Id).ToArray() };
        return expectedSideEffect is null ? Canonical.Hash(content) : Canonical.Hash(new { content, expectedSideEffect });
    }

    public async Task<SafetyCase> ApproveDisposition(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "NonReportableRegulatoryApproved", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var decision = value.Decisions.LastOrDefault() ?? throw new DomainException("medical_required", "Begruendete medizinische Entscheidung fehlt.");
            var assessment = value.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
            Guard.Against(decision.Disposition != Disposition.NonReportable || value.Disposition != Disposition.Undetermined,
                "decision_state", "Keine offene Nichtmeldepflichtentscheidung.");
            Guard.Against(decision.ActorId == actor.Id, "four_eyes", "Getrennte medizinische und regulatorische Identitaeten erforderlich.", 403);
            Guard.Against(assessment.EvidenceHash != Canonical.Hash(value.Evidence) ||
                decision.ScopeHash != DispositionScope(value, assessment, decision.Disposition, decision.Rationale, decision.ExpectedSideEffect),
                "scope_changed", "Entscheidung passt nicht mehr zum aktuellen Assessment und Evidenzstand.", 409);
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, Roles.Regulatory, decision.ScopeHash,
                clock.GetUtcNow(), "Begruendete Nichtmeldepflicht im konkreten Fall", Guard.Text(input.Reason, "RA-Begruendung", 40), null);
            return value with { Version = value.Version + 1, Disposition = Disposition.NonReportable,
                Approvals = [.. value.Approvals, approval],
                Deadlines = value.Deadlines.Select(d => d.Status == "Open" ? d with { Status = "NonReportableReviewed" } : d).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> Awareness(string id, AwarenessCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "AwarenessRecorded", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            Guard.Text(input.Source, "Belegquelle", 5);
            Guard.Text(input.Reason, "Begruendung", 40);
            Guard.Against(input.At > clock.GetUtcNow(), "future_awareness", "Kenntnis liegt in der Zukunft.");
            var first = value.FirstAwarenessAt;
            DateTimeOffset? reportability = value.ReportabilityAwarenessAt;
            if (input.NewReportabilityInformation)
            {
                var previous = value.Decisions.LastOrDefault();
                var confirmed = value.Approvals.LastOrDefault(a => a.ScopeHash == previous?.ScopeHash && a.Role == Roles.Regulatory);
                Guard.Against(value.Disposition is not (Disposition.NonReportable or Disposition.Undetermined) || previous?.Disposition != Disposition.NonReportable ||
                    confirmed is null || input.At <= confirmed.At || value.Awareness.Any(a => a.Kind == "NewReportabilityInformation" && a.Source == input.Source)
                    || !value.Evidence.Any(e => e.Id == input.Source && e.Kind is not ("Classification" or "Reopening") &&
                        e.RecordedAt > confirmed.At && input.At <= e.RecordedAt),
                    "awareness_exception", "Neuer Fristbeginn nur mit tatsaechlich neuer Evidenz nach begruendeter Nichtmeldepflicht.");
                reportability = input.At;
            }
            else
            {
                Guard.Against(input.At > first, "no_clock_reset", "Spaete Bearbeitung oder Reklassifizierung setzt die Kenntnis nicht zurueck.");
                Guard.Against(value.OccurredAt > input.At, "awareness_order", "Kenntniskorrektur darf einem dokumentierten Ereignis nicht vorausgehen.");
                first = input.At;
            }
            var deadline = DeadlineCalculator.Calculate(value.Jurisdiction, value.Seriousness,
                reportability ?? first, policies.Calendar(value.Jurisdiction));
            Deadline[] deadlines = input.NewReportabilityInformation
                ? [.. value.Deadlines, deadline]
                : value.Deadlines.Select(d => d.TriggerAt == value.FirstAwarenessAt
                    ? DeadlineCalculator.ProtectEarlier(d, DeadlineCalculator.Calculate(d.Jurisdiction, d.Seriousness, first,
                        policies.Calendar(d.Jurisdiction))) with { Id = d.Id } : d).ToArray();
            var e = new AwarenessEvent(Guid.NewGuid().ToString("N"), input.NewReportabilityInformation ? "NewReportabilityInformation" : "Correction",
                input.At, clock.GetUtcNow(), input.Source, input.Reason, actor.Id);
            return Invalidate(value) with { Version = value.Version + 1, FirstAwarenessAt = first,
                ReportabilityAwarenessAt = reportability, Awareness = [.. value.Awareness, e], Deadlines = deadlines,
                Disposition = Disposition.Undetermined, Stage = CaseStage.AwaitingMedicalReview };
        }, ct);
    }

    public async Task<SafetyCase> Link(string id, LinkEvidenceCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var evidence = await store.Get<Evidence>("evidence", input.EvidenceId, ct)
            ?? throw new DomainException("evidence_missing", "Evidenz nicht gefunden.", 404);
        actor.RequireProduct(evidence.ProductId);
        if (input.EvidenceVersion is { } version)
            evidence = (await store.History<Evidence>("evidence", [evidence.ProductId], ct))
                .SingleOrDefault(e => e.Id == input.EvidenceId && e.Version == version)
                ?? throw new DomainException("evidence_version_missing", "Evidenzversion fehlt.", 404);
        Guard.Against(evidence.ProductId != c.ProductId || !evidence.Approved, "evidence_scope", "Nicht freigegebene oder produktfremde Evidenz.");
        EvidenceRules.RequireIntegrity(evidence);
        await objects.Read(evidence.Hash, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "EvidenceLinked", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            Guard.Against(value.Evidence.Any(e => e.Id == evidence.Id && e.Version == evidence.Version), "evidence_duplicate", "Diese Evidenzversion ist bereits verknuepft.");
            return Invalidate(value) with { Version = value.Version + 1, Evidence = [.. value.Evidence.Where(e => e.Id != evidence.Id), evidence],
                Stage = CaseStage.AwaitingMedicalReview };
        }, ct);
    }

    public async Task<SafetyCase> AddInformationRequest(string id, RequestInformationCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        Guard.Text(input.Question, "Fragestellung", 20);
        Guard.Text(input.MinimalData, "Minimaldatensatz", 15, 1500);
        Guard.Text(input.Relevance, "Medizinisch-technische Relevanz", 30);
        Guard.Text(input.ManufacturerBasis, "Hersteller-Rechtsgrundlage", 20);
        Guard.Text(input.DisclosureBasis, "Eigenstaendige Offenlegungsbefugnis", 20);
        Guard.Against(input.MinimalData.Contains("vollstaendige Patientenakte", StringComparison.OrdinalIgnoreCase)
            || input.MinimalData.Contains("full patient record", StringComparison.OrdinalIgnoreCase), "data_minimization", "Pauschale Vollaktenanforderung ist nicht zugelassen.");
        Guard.Against(input.DueAt <= clock.GetUtcNow(), "request_due", "Interne Rueckmeldefrist muss in der Zukunft liegen.");
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationRequestDrafted",
            input.Relevance, actor, input), current =>
        {
            var value = Require(current);
            Guard.Against(value.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
            var request = new InformationRequest(Guid.NewGuid().ToString("N"), input.Question, input.MinimalData, input.Relevance,
                input.RecipientRole, input.ManufacturerBasis, input.DisclosureBasis, input.DueAt, Canonical.Hash(input),
                [], "Draft", null, true);
            return value with { Version = value.Version + 1, Stage = CaseStage.AwaitingInformation,
                InformationRequests = [.. value.InformationRequests, request] };
        }, ct);
    }

    public async Task<SafetyCase> ApproveInformation(string id, string requestId, ApprovalCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationRequestApproved", input.Reason, actor, new { requestId, input }), current =>
        {
            var value = Require(current);
            var request = value.InformationRequests.SingleOrDefault(r => r.Id == requestId)
                ?? throw new DomainException("request_missing", "Anfrage fehlt.", 404);
            var role = actor.Roles.Contains(Roles.Medical) ? Roles.Medical : Roles.Regulatory;
            Guard.Against(request.Status != "Draft" || request.Approvals.Any(a => a.Role == role || a.ActorId == actor.Id),
                "request_gate", "Anfrage braucht zwei verschiedene berechtigte Personen und Rollen.");
            if (role == Roles.Regulatory)
                Guard.Against(!request.Approvals.Any(a => a.Role == Roles.Medical), "medical_required", "Medizinische Notwendigkeit zuerst genehmigen.");
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, request.ScopeHash,
                clock.GetUtcNow(), "Ausschliesslich simulierte minimale Informationsanforderung", Guard.Text(input.Reason, "Begruendung", 20), null);
            var next = request with { Approvals = [.. request.Approvals, approval], Status = role == Roles.Regulatory ? "SimulatedSent" : "Draft" };
            return value with { Version = value.Version + 1, InformationRequests = value.InformationRequests.Select(r => r.Id == requestId ? next : r).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> RespondInformation(string id, RespondInformationCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var text = Guard.Text(input.Response, "Synthetische Antwort", 30, 5000);
        var hash = await objects.Put(text, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationResponseReceived", input.Reason, actor, input), current =>
        {
            var value = Require(current);
            var request = value.InformationRequests.SingleOrDefault(r => r.Id == input.RequestId)
                ?? throw new DomainException("request_missing", "Anfrage fehlt.", 404);
            Guard.Against(request.Status != "SimulatedSent", "request_not_sent", "Erst nach beiden Freigaben simuliert beantworten.");
            var evidence = new Evidence("response-" + request.Id, 1, c.ProductId, "Synthetischer Befundruecklauf", "ClinicalFollowUp",
                text, hash, "cohors:simulated-clinic", "Simulierter gezielter Befund: Antwort", "Synthetic", "Product",
                value.OccurredAt ?? value.FirstAwarenessAt, null, clock.GetUtcNow(), true, true, hash);
            return Invalidate(value) with { Version = value.Version + 1,
                InformationRequests = value.InformationRequests.Select(r => r.Id == request.Id ? r with { Status = "SimulatedAnswered", Response = text } : r).ToArray(),
                Evidence = [.. value.Evidence, evidence], Stage = CaseStage.AwaitingMedicalReview };
        }, ct);
    }

    public async Task<SafetyCase> RecordCoding(string id, RecordCodingCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        var c = await Get(id, actor, ct);
        var catalog = policies.Terminology;
        Guard.Against(input.ReleaseId != catalog.Reference.ReleaseId, "terminology_release", "Explizit den kontrollierten Mappingstand waehlen.");
        Guard.Text(input.Rationale, "Medizinische Kodierungsbegruendung mit Grenzen und Belegen", 50);
        Guard.Against(input.Codes is not { Length: > 0 and <= 12 } || input.Codes.Any(string.IsNullOrWhiteSpace) ||
            input.Codes.Distinct(StringComparer.Ordinal).Count() != input.Codes.Length, "terminology_codes", "Eindeutige A/C/D/F-Kodes erforderlich.");
        Guard.Against(input.HistoricalAsOf is { } historical && (c.OccurredAt is null ||
            historical < PortfolioRules.UtcDate(c.OccurredAt.Value)), "terminology_history",
            "Historische Originalkodierung braucht ein bekanntes, nicht spaeteres Ereignisdatum und einen konkreten Beleg.");
        var assigned = input.Codes.Select(code => catalog.Select(code, input.HistoricalAsOf)).ToArray();
        Guard.Against(assigned.GroupBy(a => a.Source.Annex).Any(g => g.Count() > 3),
            "terminology_codes", "Im Pilot hoechstens drei begruendete Kodes pro Anhang.");
        Guard.Against(input.EvidenceIds is not { Length: > 0 } || input.EvidenceIds.Any(e =>
            !c.Evidence.Any(source => source.Id == e && source.Approved && source.Kind != "Coding")),
            "coding_evidence", "Konkrete freigegebene Fallbelege erforderlich; die eigene Kodierung ist kein unabhaengiger Beleg.");
        var proofs = input.HistoricalProofs ?? [];
        Guard.Against(proofs.Any(p => p is null || !input.Codes.Contains(p.Code, StringComparer.Ordinal)) ||
            proofs.Select(p => p.Code).Distinct(StringComparer.Ordinal).Count() != proofs.Length ||
            proofs.Length > 0 && input.HistoricalAsOf is null, "coding_original_proof", "Historische Belege muessen eindeutig auf die behaupteten historischen Kodes passen.");
        foreach (var proof in proofs)
        {
            var index = Array.FindIndex(assigned, code => code.Source.Code == proof.Code);
            assigned[index] = CodingRules.BindOriginal(assigned[index], proof, input.HistoricalAsOf, c.Evidence, input.EvidenceIds);
            await objects.Read(proof.EvidenceHash, ct);
        }
        var citations = c.Evidence.Where(e => input.EvidenceIds.Contains(e.Id, StringComparer.Ordinal))
            .Select(e => new Citation(e.Id, e.Hash, e.Locator, e.Content[..Math.Min(e.Content.Length, 400)])).ToArray();
        var coding = new CaseCoding((c.Coding?.Version ?? 0) + 1, catalog.Reference.ReleaseId, catalog.Hash,
            assigned, citations, input.Rationale, actor.Id, clock.GetUtcNow(), input.HistoricalAsOf,
            input.HistoricalAsOf is null ? "CurrentRetrospective" : "ClaimedHistoricalOriginal",
            CodingRules.HistoricalFindings(assigned, input.HistoricalAsOf));
        var content = Canonical.Serialize(coding);
        var hash = await objects.Put(content, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "CaseCodingRecorded", input.Rationale, actor, input), value =>
        {
            var current = Require(value);
            var evidence = new Evidence("coding-" + id, coding.Version, c.ProductId, "Menschlich begruendete A/C/D/F-Kodierung",
                "Coding", content, hash, catalog.Reference.Source.Page, "Kodierung und Originalbelege",
                "FDA mapping reference; human synthetic case coding", "Product", clock.GetUtcNow(), null,
                clock.GetUtcNow(), true, true, hash);
            return Invalidate(current) with { Version = current.Version + 1, Coding = coding,
                Evidence = [.. current.Evidence.Where(e => e.Id != evidence.Id), evidence], Stage = CaseStage.AwaitingMedicalReview };
        }, ct);
    }

    public async Task<SafetyCase> Reclassify(string id, ReclassifyCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        Guard.Against(!Enum.IsDefined(input.Seriousness), "seriousness", "Unbekannte Dringlichkeitsklasse.");
        Guard.Text(input.Reason, "Begruendete medizinische Reklassifizierung", 40);
        var c = await Get(id, actor, ct);
        var text = $"SYNTHETISCHE REKLASSIFIZIERUNG: {c.Seriousness} -> {input.Seriousness}. {input.Reason}";
        var hash = await objects.Put(text, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "SeriousnessReclassified", input.Reason, actor, input), value =>
        {
            var current = Require(value);
            var evidence = new Evidence("classification-" + Canonical.Hash(new { id, key })[..24], 1, c.ProductId,
                "Begruendete medizinische Reklassifizierung", "Classification", text, hash, "cohors:human-review",
                "Medizinische Reklassifizierungsbegruendung", "Synthetic", "Product", clock.GetUtcNow(), null, clock.GetUtcNow(), true, true, hash);
            return Invalidate(current) with { Version = current.Version + 1, Seriousness = input.Seriousness,
                Disposition = Disposition.Undetermined, Stage = CaseStage.AwaitingMedicalReview,
                Evidence = [.. current.Evidence, evidence],
                Deadlines = current.Deadlines.Select(d => DeadlineCalculator.ProtectEarlier(d,
                    DeadlineCalculator.Calculate(d.Jurisdiction, input.Seriousness, d.TriggerAt, policies.Calendar(d.Jurisdiction))) with { Id = d.Id }).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> Close(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        Guard.Text(input.Reason, "Abschlussbegruendung und verbleibende Produktbeobachtung", 40);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "CaseClosed", input.Reason, actor, input), value =>
        {
            var current = Require(value);
            var assessment = current.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
            AssessmentRules.RequireReviewable(assessment, false, null);
            Guard.Against(current.Stage == CaseStage.Closed || current.Deadlines.Any(d => d.Status == "Open") ||
                (current.FollowUps ?? []).Any(f => f.Status == "Open") ||
                current.Submissions.Any(s => s.Status == "TransmissionUnknown") ||
                current.InformationRequests.Any(r => r.Status != "SimulatedAnswered") ||
                (current.Investigations ?? []).Any(i => i.Status is not ("SimulatedExamined" or "Cancelled")) ||
                current.AgentRuns.Any(r => r.Status is "Queued" or "Running") || assessment.EvidenceHash != Canonical.Hash(current.Evidence),
                "case_unresolved", "Offene Verpflichtungen, Nachforderungen, unklare Laeufe oder veraltete Evidenz verhindern den Abschluss.");
            var final = current.CurrentReport;
            var nonReportable = current.Disposition == Disposition.NonReportable &&
                current.Decisions.Any(d => d.Disposition == Disposition.NonReportable && d.AssessmentVersion == assessment.Version &&
                    current.Approvals.Any(a => a.Role == Roles.Regulatory && a.ActorId != d.ActorId && a.ScopeHash == d.ScopeHash));
            Guard.Against(!nonReportable && !(final?.Kind == "Final" && final.State == ReportState.SimulatedAccepted &&
                final.ScopeHash == ReportRules.Scope(assessment, final)), "case_final_required",
                "Gueltig zweifach gepruefte Nichtmeldepflicht oder abgeglichene Abschlussmeldung erforderlich. CAPA-/Produktbeobachtung bleibt separat.");
            return current with { Version = current.Version + 1, Stage = CaseStage.Closed, ClosedAt = clock.GetUtcNow() };
        }, ct);
    }

    public async Task<SafetyCase> AbandonRun(string id, string runId, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        Guard.Text(input.Reason, "Begruendung des kontrollierten Laufabbruchs", 40);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "AgentRunAbandoned", input.Reason, actor, new { runId, input }), value =>
        {
            var current = Require(value);
            var run = current.AgentRuns.SingleOrDefault(r => r.Id == runId) ?? throw new DomainException("run_missing", "Lauf fehlt.");
            Guard.Against(run.Status is not ("Queued" or "Running") || clock.GetUtcNow() - run.StartedAt < TimeSpan.FromMinutes(10),
                "run_active", "Nur nach kontrollierter Pruefung eines seit mindestens zehn Minuten ungeklaerten Laufs abbrechen.", 409);
            return current with { Version = current.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                AgentRuns = current.AgentRuns.Select(r => r.Id == runId ? r with { Status = "Abandoned", FinishedAt = clock.GetUtcNow(),
                    ErrorCode = "human_abandoned_no_result_acceptance" } : r).ToArray(),
                Alerts = [.. current.Alerts, "Unklarer Agentenlauf menschlich abgebrochen; kein Ergebnis angenommen. Neuer Auftrag oder manueller Weg erforderlich."] };
        }, ct);
    }

    public async Task<SafetyCase> Reopen(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var reason = Guard.Text(input.Reason, "Begruendung der Wiederaufnahme ohne Fristreset", 40);
        var c = await Get(id, actor, ct);
        var text = "SYNTHETISCHE WIEDERAUFNAHME: " + reason;
        var hash = await objects.Put(text, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "CaseReopened", reason, actor, input), value =>
        {
            var current = Require(value);
            Guard.Against(current.Stage != CaseStage.Closed, "case_not_closed", "Fall ist nicht abgeschlossen.");
            var evidence = new Evidence("reopen-" + Canonical.Hash(new { id, key })[..24], 1, c.ProductId,
                "Begruendete Wiederaufnahme", "Reopening", text, hash, "cohors:human-review", "Wiederaufnahmebegruendung",
                "Synthetic", "Product", clock.GetUtcNow(), null, clock.GetUtcNow(), true, true, hash);
            return current with { Version = current.Version + 1, ClosedAt = null, Stage = CaseStage.AwaitingMedicalReview,
                Evidence = [.. current.Evidence, evidence], Reports = Supersede(current.Reports),
                Alerts = [.. current.Alerts, "Wiederaufnahme benoetigt erneute aktuelle Bewertung; keine Originalpflicht automatisch neu geoeffnet."] };
        }, ct);
    }

    private async Task RequireRunCurrent(SafetyCase snapshot, string runId, string owner, CancellationToken ct)
    {
        var current = await Get(snapshot.Id, Workflow, ct);
        Guard.Against(current.Version != snapshot.Version || !current.AgentRuns.Any(r => r.Id == runId && r.Status == "Running" && r.ExecutionId == owner),
            "agent_snapshot_stale", "Lauf abgebrochen oder Fallversion geaendert; keine weitere Modellarbeit auf altem Snapshot.", 409);
    }

    public static SafetyCase Require(SafetyCase? c) => c ?? throw new DomainException("not_found", "Fall nicht gefunden.", 404);
    private static ReportDraft[] Supersede(ReportDraft[] reports) => reports.Select(r =>
        r.State is ReportState.Draft or ReportState.MedicalApproved or ReportState.Frozen ? r with { State = ReportState.Superseded } : r).ToArray();
    private static SafetyCase Invalidate(SafetyCase c)
    {
        Guard.Against(c.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
        return c with { Reports = Supersede(c.Reports) };
    }
}
