using System.Data.Common;
using Cohors.Domain;

namespace Cohors.Application;

public sealed class RetentionService(IStateStore store, IRetentionStateStore references, IObjectStore objects,
    PolicyCatalog policies, RetentionOptions options, TimeProvider clock)
{
    public bool LocalPhysicalProfile => options.EnableSyntheticRawDeletion && objects is IRawQuarantineStore;
    private IRawQuarantineStore RawStore => LocalPhysicalProfile
        ? (IRawQuarantineStore)objects
        : throw new DomainException("retention_profile_disabled", "Physische Loeschung ist nur im expliziten isolierten synthetischen Lokalprofil verfuegbar.", 409);

    private async Task RequireProduct(string id, Actor actor, CancellationToken ct)
    {
        actor.RequireProduct(id);
        var device = policies.Device(id);
        var profile = await store.Get<ProductGovernance>("productGovernance", id, ct)
            ?? throw new DomainException("retention_product", "Kontrollierte synthetische Produktakte fehlt.");
        GovernanceRules.RequireValid(profile, device);
    }

    public async Task<RetentionRecord> Get(string id, Actor actor, CancellationToken ct = default)
    {
        var value = await store.Get<RetentionRecord>("retention", id, ct)
            ?? throw new DomainException("not_found", "Aufbewahrungseintrag fehlt.", 404);
        actor.RequireProduct(value.ProductId);
        RetentionRules.RequireObject(value.Object);
        return value;
    }

    public async Task<RetentionView> Evaluate(string id, Actor actor, CancellationToken ct = default)
    {
        var value = await Get(id, actor, ct);
        var dependencies = await references.ObjectDependencies(value.Object.Hash, id, ct);
        var lifecycle = await store.Get<ProductLifecycle>("productLifecycle", "lifecycle-" + value.ProductId, ct);
        return new(value, RetentionRules.Evaluate(value, lifecycle, clock.GetUtcNow(), dependencies.Length, LocalPhysicalProfile),
            dependencies.Where(r => actor.Products.Contains(r.ProductId)).ToArray());
    }

    public Task<RetentionRecord[]> List(Actor actor, CancellationToken ct = default) =>
        store.List<RetentionRecord>("retention", actor.Products, ct);

    public Task<ProductLifecycle[]> Lifecycles(Actor actor, CancellationToken ct = default) =>
        store.List<ProductLifecycle>("productLifecycle", actor.Products, ct);

    public async Task<ProductLifecycle> RecordLifecycle(RecordLifecycleCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        await RequireProduct(input.ProductId, actor, ct);
        Guard.Text(input.SourceReference, "Synthetischer Lifecycle-Beleg", 10, 400);
        Guard.Text(input.Reason, "Begruendete Lifecycle-Bestaetigung/Korrektur", 40, 4000);
        Guard.Against(!input.SourceReference.StartsWith("DEMO-", StringComparison.Ordinal),
            "synthetic_only", "Nur ausdruecklich mit DEMO- gekennzeichnete Lifecycle-Belege.");
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById(RetentionRules.LifecycleTimeZone)).DateTime);
        Guard.Against(input.Confirmed && input.LastPlacingOnMarketLocalDate is null ||
            input.LastPlacingOnMarketLocalDate > localToday, "retention_lifecycle",
            "Bestaetigung braucht ein nicht zukuenftiges Datum des letzten Inverkehrbringens; Upload ersetzt es nicht.");
        var id = "lifecycle-" + input.ProductId;
        return await store.Execute<ProductLifecycle>(new(id, "productLifecycle", input.ProductId, input.ExpectedVersion, key,
            "LifecycleRecorded", input.Reason, actor, input, clock.GetUtcNow()), _ =>
            new(id, input.ExpectedVersion + 1, input.ProductId, input.LastPlacingOnMarketLocalDate, input.Confirmed,
                RetentionRules.LifecycleTimeZone, RetentionRules.PolicyVersion, input.SourceReference, input.Reason,
                input.Confirmed ? actor.Id : "", clock.GetUtcNow(), true), ct);
    }

    public async Task<RetentionRecord> CreateRaw(CreateRawRetentionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        await RequireProduct(input.ProductId, actor, ct);
        var raw = RawStore;
        Guard.Text(input.Reference, "Synthetische Testreferenz", 10, 120);
        Guard.Text(input.Reason, "Begruendung des isolierten Sieben-Tage-Rohdatentests", 40, 4000);
        Guard.Against(!input.Reference.StartsWith("DEMO-RAW-", StringComparison.Ordinal) ||
            input.SyntheticReceivedAt > clock.GetUtcNow(), "retention_raw_test",
            "DEMO-RAW-Referenz und expliziter nicht zukuenftiger synthetischer Empfangszeitpunkt erforderlich.");
        var id = "retention-raw-" + Canonical.Hash(new { actor.Id, key })[..32];
        var context = new CommandContext(id, "retention", input.ProductId, 0, key, "RawQuarantineCreated",
            input.Reason, actor, input, clock.GetUtcNow());
        if (await store.Receipt<RetentionRecord>(context, ct) is { } saved) return await Get(saved.Id, actor, ct);
        var binding = await raw.CreateSyntheticRaw(Canonical.Serialize(new {
            contract = "cohors-synthetic-raw-test-1", synthetic = true, input.ProductId, input.Reference,
            receivedAt = input.SyntheticReceivedAt.ToUniversalTime(), nonce = id,
            content = "SYNTHETIC TEST RAW INPUT. No patient, medical finding or production deletion policy."
        }), ct);
        return await store.Execute<RetentionRecord>(context, _ => new(id, 1, input.ProductId, binding, RetentionRules.PolicyVersion,
            input.Reference, input.SyntheticReceivedAt.ToUniversalTime(), clock.GetUtcNow(), true, false, "", "Registered",
            null, 0, null, null, null), ct);
    }

    public async Task<RetentionRecord> Track(TrackRetentionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        Guard.Text(input.Reason, "Begruendung der synthetischen Dokumentklassifikation", 40, 4000);
        var evidence = (await store.History<Evidence>("evidence", actor.Products, ct))
            .SingleOrDefault(e => e.Id == input.EvidenceId && e.Version == input.EvidenceVersion)
            ?? throw new DomainException("not_found", "Berechtigte konkrete Evidenzversion fehlt.", 404);
        await RequireProduct(evidence.ProductId, actor, ct);
        Guard.Against(!evidence.Synthetic || !evidence.Approved, "retention_evidence",
            "Nur ausdruecklich freigegebene synthetische Demo-Evidenz klassifizieren.");
        EvidenceRules.RequireIntegrity(evidence);
        await objects.Read(evidence.ObjectKey, ct);
        var binding = new RetentionObjectBinding(RetentionRules.ObjectContract, RetentionRules.ImplantDocumentation,
            evidence.ObjectKey, evidence.Hash);
        RetentionRules.RequireObject(binding);
        var id = "retention-evidence-" + Canonical.Hash(new { evidence.Id, evidence.Version, evidence.Hash })[..32];
        return await store.Execute<RetentionRecord>(new(id, "retention", evidence.ProductId, 0, key, "RetentionEvidenceClassified",
            input.Reason, actor, input, clock.GetUtcNow()), _ => new(id, 1, evidence.ProductId, binding,
            RetentionRules.PolicyVersion, $"{evidence.Id}@{evidence.Version}", evidence.RecordedAt, clock.GetUtcNow(),
            true, false, "", "Registered", null, 0, null, null, null), ct);
    }

    public async Task<RetentionRecord> Hold(string id, RetentionHoldCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var current = await Get(id, actor, ct);
        Guard.Text(input.Reason, "Hold setzen/aufheben und Folgen begruenden", 40, 4000);
        return await store.Execute<RetentionRecord>(new(id, "retention", current.ProductId, input.ExpectedVersion, key,
            "RetentionHoldChanged", input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status is "Deleting" or "DeletionFailed" or "Deleted", "retention_in_flight",
                "Begonnene physische Schritte verlangen kontrollierten Abgleich; kein unwirksames nachtraegliches Hold versprechen.", 409);
            return value with { Version = value.Version + 1, LegalHold = input.LegalHold, HoldReason = input.Reason,
                Status = input.LegalHold ? "Held" : "Registered", Authorization = null };
        }, ct);
    }

    public async Task<RetentionRecord> Authorize(string id, AuthorizeDeletionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var view = await Evaluate(id, actor, ct);
        Guard.Text(input.Reason, "Ausdrueckliche Loeschfreigabe", 40, 4000);
        Guard.Text(input.ExternalCopiesReview, "Pruefung von Backups, Exporten, Caches und Agentenspeichern", 60, 4000);
        Guard.Against(!input.ExternalCopiesReviewed, "retention_copy_review", "Externe/abgeleitete Kopien muessen ausdruecklich geprueft sein.");
        Guard.Against(!view.Evaluation.EligibleForAuthorization, "retention_blocked",
            "Keine Loeschfreigabe: " + string.Join(", ", view.Evaluation.Blockers), 409);
        return await store.Execute<RetentionRecord>(new(id, "retention", view.Record.ProductId, input.ExpectedVersion, key,
            "RawDeletionAuthorized", input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status is "Deleting" or "DeletionFailed" or "Deleted", "retention_in_flight",
                "Begonnenen Vorgang mit seiner vorhandenen Autorisierung abgleichen, nicht ersetzen.", 409);
            Guard.Against(RetentionRules.Scope(value, null) != view.Evaluation.ScopeHash, "retention_scope_changed",
                "Aufbewahrungs-/Objektbindung hat sich geaendert.", 409);
            return value with { Version = value.Version + 1, Status = "Authorized", ErrorCode = null, ObjectIoFailures = null,
                Authorization = new(Canonical.Hash(new { id, key, actorId = actor.Id, view.Evaluation.ScopeHash }), actor.Id,
                    view.Evaluation.ScopeHash, clock.GetUtcNow(), input.Reason, true, input.ExternalCopiesReview) };
        }, ct);
    }

    public async Task<RetentionRecord> Delete(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var raw = RawStore;
        var current = await Get(id, actor, ct);
        Guard.Text(input.Reason, "Ausfuehrung/Abgleich der physischen synthetischen Loeschung", 40, 4000);
        var start = new CommandContext(id, "retention", current.ProductId, input.ExpectedVersion, key,
            "RawDeletionStarted", input.Reason, actor, input, clock.GetUtcNow());
        var started = await store.Receipt<RetentionRecord>(start, ct);
        if (started is null)
        {
            var view = await Evaluate(id, actor, ct);
            RequireDeletable(view.Evaluation);
            started = await store.Execute<RetentionRecord>(start, value => value! with {
                Version = value.Version + 1, Status = "Deleting", Attempts = value.Attempts + 1,
                LastAttemptAt = clock.GetUtcNow(), ErrorCode = null, ObjectIoFailures = null }, ct);
        }
        var completion = start with { ExpectedVersion = started.Version, IdempotencyKey = Canonical.Hash(new { key, phase = "raw-delete-complete" }),
            Action = "RawDeletionCompleted", Request = new { input, started.Authorization!.Id, started.Object.Hash } };
        if (await store.Receipt<RetentionRecord>(completion, ct) is { } completed) return completed;
        try
        {
            return await references.ExecuteDeletion(completion, started.Object.Hash, async (value, dependencies) =>
            {
                RequireDeletable(RetentionRules.Evaluate(value, null, clock.GetUtcNow(), dependencies.Length, LocalPhysicalProfile));
                var receipt = await raw.DeleteRaw(value, clock.GetUtcNow(), ct);
                RetentionRules.RequireDeletionReceipt(value, receipt);
                return value with { Version = value.Version + 1, Status = "Deleted", Deletion = receipt, ErrorCode = null, ObjectIoFailures = null };
            }, ct);
        }
        catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException or DbException or OperationCanceledException)
        {
            if (await store.Receipt<RetentionRecord>(completion, CancellationToken.None) is { } committed) return committed;
            var error = ex is DomainException domain ? domain.Code : ex is DbException ? "retention_sql" :
                ex is OperationCanceledException ? "retention_cancelled" : "retention_object_io";
            ObjectIoFailure[]? ioFailures = ex is ObjectStoreIoException io ? io.Failures :
                ex is IOException or UnauthorizedAccessException
                    ? [ObjectStoreIoException.Describe(RawDeletionIoStep.DeletionTransactionBoundary, ex)] : null;
            var observed = await Get(id, actor, CancellationToken.None);
            if (observed.Version == started.Version && observed.Status == "Deleting")
                await store.Execute<RetentionRecord>(start with { ExpectedVersion = started.Version, IdempotencyKey = Canonical.Hash(new { key, phase = "raw-delete-failed" }),
                    Action = "RawDeletionFailed", Request = new { error, started.Authorization!.Id, ioFailures }, At = clock.GetUtcNow() },
                    value => value! with { Version = value.Version + 1, Status = "DeletionFailed", ErrorCode = error,
                        ObjectIoFailures = ioFailures }, CancellationToken.None);
            throw new DomainException("retention_incomplete",
                $"Kein vollstaendiger Loeschnachweis ({error}). Aktuellen Zustand laden und denselben autorisierten Vorgang kontrolliert abgleichen.", 409, ex);
        }
    }

    private static void RequireDeletable(RetentionEvaluation value) =>
        Guard.Against(!value.CanDeleteNow, "retention_blocked", "Loeschsperren: " + string.Join(", ", value.Blockers), 409);
}
