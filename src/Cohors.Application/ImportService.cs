using Cohors.Domain;

namespace Cohors.Application;

public sealed class ImportService(IStateStore store, IObjectStore objects, PolicyCatalog policies, CaseService cases,
    TimeProvider clock)
{
    public const string IntakeKind = "intakeImport";
    public const string DistributionKind = "distributionImport";
    public const string Scenario = "channel-import";

    public async Task<IntakeImport> ImportIntake(ImportIntakeCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms);
        actor.RequireProduct(input.ProductId);
        var device = policies.Device(input.ProductId);
        Guard.Against(!input.Synthetic, "synthetic_only", "Der Pilot nimmt ausschliesslich synthetische Kanaldaten an.");
        Guard.Against(!Enum.IsDefined(input.Channel), "import_channel", "Unbekannter synthetischer Eingangskanal.");
        var reason = Guard.Text(input.Reason, "Importbegruendung", 30, 4000);
        var original = ImportRules.Original("intake", input.FileName, input.Content);
        var id = ImportRules.IntakeId(input.ProductId, input.Channel, original.Hash);
        var now = clock.GetUtcNow();
        var context = new CommandContext(id, IntakeKind, input.ProductId, 0, key, "IntakeImportReceived", reason, actor, input, now);
        if (await store.Receipt<IntakeImport>(context, ct) is not null)
        {
            var stored = await store.Get<IntakeImport>(IntakeKind, id, ct)
                ?? throw new DomainException("import_state", "Quittierter Kanalimport fehlt.", 409);
            return stored.Status == IntakeImportStatus.Processed ? stored : await Process(stored, actor, ct);
        }
        if (await store.Get<IntakeImport>(IntakeKind, id, ct) is { } existing)
        {
            Guard.Against(existing.Status == IntakeImportStatus.Processed, "import_duplicate_file",
                "Diese Datei wurde fuer Produktfamilie und Kanal bereits verarbeitet; keine doppelte Uebernahme.", 409);
            Guard.Against(existing.ImportedBy != actor.Id, "import_in_progress",
                "Dieser Kanalimport wird von der importierenden Person abgeschlossen.", 409);
            return await Process(existing, actor, ct);
        }
        var evaluation = ImportRules.EvaluateIntake(
            SyntheticCsv.Parse(input.Content, ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows), device, input.Channel, now);
        var rows = await Classify(evaluation, input.ProductId, id, ct);
        Guard.Against(await objects.Put(input.Content, ct) != original.Hash, "import_object",
            "Originaldatei und Objektablage stimmen nicht ueberein.", 409);
        var received = await store.Execute<IntakeImport>(context, _ => new IntakeImport(id, 1, input.ProductId, input.Channel,
            original, now, actor.Id, reason, ImportRules.IntakeContract, ImportRules.IntakeBoundary, IntakeImportStatus.Received,
            rows, ImportRules.Count(rows), null), ct);
        return received.Status == IntakeImportStatus.Processed ? received : await Process(received, actor, ct);
    }

    public async Task<IntakeImport[]> ListIntake(Actor actor, CancellationToken ct = default)
    {
        RequireReader(actor);
        return (await store.List<IntakeImport>(IntakeKind, actor.Products, ct)).OrderByDescending(i => i.ReceivedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IntakeImport> GetIntake(string id, Actor actor, CancellationToken ct = default)
    {
        RequireReader(actor);
        var value = await store.Get<IntakeImport>(IntakeKind, id, ct)
            ?? throw new DomainException("not_found", "Kanalimport nicht gefunden.", 404);
        actor.RequireProduct(value.ProductId);
        return value;
    }

    public async Task<DistributionImport> ImportDistribution(ImportDistributionCommand input, Actor actor, string key,
        CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Quality);
        actor.RequireProduct(input.ProductId);
        var device = policies.Device(input.ProductId);
        var reason = Guard.Text(input.Reason, "Importbegruendung", 30, 4000);
        var original = ImportRules.Original("distribution", input.FileName, input.Content);
        var id = ImportRules.DistributionId(input.ProductId, original.Hash);
        var now = clock.GetUtcNow();
        var context = new CommandContext(id, DistributionKind, input.ProductId, 0, key, "DistributionImported", reason, actor, input, now);
        if (await store.Receipt<DistributionImport>(context, ct) is { } receipt) return receipt;
        Guard.Against(await store.Get<DistributionImport>(DistributionKind, id, ct) is not null, "import_duplicate_file",
            "Diese Lieferdatei wurde fuer die Produktfamilie bereits importiert; keine doppelte Zaehlung.", 409);
        var records = SyntheticCsv.Parse(input.Content, ImportRules.DistributionHeader, ImportRules.MaximumDistributionRows);
        var evaluation = ImportRules.EvaluateDistribution(records, device, PortfolioRules.UtcDate(now));
        Guard.Against(await objects.Put(input.Content, ct) != original.Hash, "import_object",
            "Originaldatei und Objektablage stimmen nicht ueberein.", 409);
        var status = evaluation.Findings.Length == 0 ? DistributionImportStatus.Validated : DistributionImportStatus.Rejected;
        return await store.Execute<DistributionImport>(context, _ => new DistributionImport(id, 1, input.ProductId, original, now,
            actor.Id, reason, ImportRules.DistributionContract, ImportRules.DistributionBoundary, status, evaluation.Rows,
            evaluation.Findings, ImportRules.Totals(records.Length, evaluation)), ct);
    }

    public async Task<DistributionImport[]> ListDistribution(Actor actor, CancellationToken ct = default)
    {
        RequireReader(actor);
        return (await store.List<DistributionImport>(DistributionKind, actor.Products, ct)).OrderByDescending(i => i.ReceivedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<DistributionImport> GetDistribution(string id, Actor actor, CancellationToken ct = default)
    {
        RequireReader(actor);
        var value = await store.Get<DistributionImport>(DistributionKind, id, ct)
            ?? throw new DomainException("not_found", "Lieferimport nicht gefunden.", 404);
        actor.RequireProduct(value.ProductId);
        return value;
    }

    public async Task<DistributionAffected> Affected(string id, string? batches, string? jurisdictions, Actor actor,
        CancellationToken ct = default)
    {
        var import = await GetDistribution(id, actor, ct);
        return ImportRules.Affected(import, Reference(import), policies.Device(import.ProductId), Split(batches), Split(jurisdictions));
    }

    public async Task<DenominatorPreview> Proposal(string id, string region, DateOnly start, DateOnly end, Actor actor,
        CancellationToken ct = default)
    {
        var import = await GetDistribution(id, actor, ct);
        var proposal = ImportRules.Propose(import, Reference(import), region, start, end, PortfolioRules.UtcDate(clock.GetUtcNow()));
        var target = Target(await store.List<Exposure>("exposure", [import.ProductId], ct), proposal);
        return new(proposal, target?.Id ?? ImportRules.ExposureId(import.ProductId, region, start, end), target?.Version ?? 0,
            target?.Count, target?.Source?.ProposalHash == proposal.Hash);
    }

    public async Task<Exposure> ApproveDenominator(ApproveDenominatorCommand input, Actor actor, string key,
        CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var import = await store.Get<DistributionImport>(DistributionKind, input.ImportId, ct)
            ?? throw new DomainException("not_found", "Lieferimport nicht gefunden.", 404);
        actor.RequireProduct(import.ProductId);
        var reason = Guard.Text(input.Reason, "Freigabebegruendung", 40, 4000);
        Guard.Against(input.ImportVersion != import.Version, "import_version",
            "Lieferimport hat sich seit der Anzeige geaendert; bitte erneut pruefen.", 409);
        Guard.Against(import.Status != DistributionImportStatus.Validated, "import_rejected",
            "Abgewiesener Import ist keine Nennerquelle.", 409);
        Guard.Against(import.ImportedBy == actor.Id, "four_eyes",
            "Nennerfreigabe nur durch eine andere Person als die importierende.", 403);
        PortfolioRules.RequireRegion(input.Region);
        var reference = Reference(import);
        var now = clock.GetUtcNow();
        var proposal = ImportRules.Propose(import, reference, input.Region, input.Start, input.End, PortfolioRules.UtcDate(now));
        var exposures = await store.List<Exposure>("exposure", [import.ProductId], ct);
        var id = Target(exposures, proposal)?.Id ?? ImportRules.ExposureId(import.ProductId, input.Region, input.Start, input.End);
        var context = new CommandContext(id, "exposure", import.ProductId, input.ExpectedVersion, key,
            "ExposureDenominatorApproved", reason, actor, input, now)
        {
            Dependencies = [new StoreDependency(DistributionKind, reference),
                .. ImportRules.PeriodDependencies(exposures, proposal, id).Select(d => new StoreDependency("exposure", d))]
        };
        if (await store.Receipt<Exposure>(context, ct) is { } receipt) return receipt;
        try
        {
            return await store.Execute<Exposure>(context, current =>
                ImportRules.Approve(current, id, proposal, input.ExpectedCount, exposures, actor.Id, now, reason), ct);
        }
        catch (DomainException error) when (error.Code == "dependency_version")
        {
            throw new DomainException("dependency_version",
                "Lieferimport oder ein anderer Nenner derselben Periode hat sich seit dem Lesen geaendert; bitte neu lesen und erneut pruefen.", 409);
        }
    }

    private async Task<IntakeImport> Process(IntakeImport import, Actor actor, CancellationToken ct)
    {
        var content = await objects.Read(import.Original.Hash, ct);
        Guard.Against(Canonical.HashText(content) != import.Original.Hash, "import_object",
            "Originaldatei und Objektablage stimmen nicht ueberein.", 409);
        var evaluation = ImportRules.EvaluateIntake(
            SyntheticCsv.Parse(content, ImportRules.IntakeHeader, ImportRules.MaximumIntakeRows),
            policies.Device(import.ProductId), import.Channel, import.ReceivedAt);
        var candidates = evaluation.Candidates.ToDictionary(c => c.Line);
        var known = await store.List<SafetyCase>("case", [import.ProductId], ct);
        var registrations = await Registrations(known, evaluation.Candidates, import.ProductId, ct);
        var others = await Others(import.ProductId, import.Id, ct);
        var rows = import.Rows.ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            if (rows[index].Outcome != IntakeRowOutcome.Pending) continue;
            Guard.Against(!candidates.TryGetValue(rows[index].Line, out var candidate) || candidate.RowHash != rows[index].RowHash,
                "import_state", "Gespeicherte Importzeile passt nicht zur Originaldatei.", 409);
            var key = RegistrationKey(import.Id, rows[index].Line);
            var target = known.SingleOrDefault(c => c.Id == CaseIdentity.For(import.ProductId, candidate!.IncidentKey));
            // A resumed run settles the registration it already recorded instead of judging it against later imports.
            var own = target is not null && target.Sources.Any(s => s.ExternalId == candidate!.SourceId) &&
                await OwnRegistration(target.Id, key, ct);
            var row = own ? rows[index]
                : ImportRules.Reclassify(rows[index], candidate!, import.ProductId, known, others, registrations);
            if (row.Outcome != IntakeRowOutcome.Pending)
            {
                rows[index] = row;
                continue;
            }
            try
            {
                var registered = await cases.Register(new RegisterComplaint(candidate!.IncidentKey, candidate.SourceId,
                    candidate.Title, candidate.Description, import.ProductId, candidate.Udi, candidate.Batch, candidate.Country,
                    candidate.Jurisdiction, candidate.Seriousness, candidate.OccurredAt, candidate.AwarenessAt, Scenario, true),
                    actor, key, ct);
                own = own || await OwnRegistration(registered.Id, key, ct);
                // Registration returned someone else's source unchanged: compare with the imports as they are now,
                // because the registering import may have been received after this run read its snapshot.
                rows[index] = own ? ImportRules.Settle(row, candidate, registered, others, own)
                    : ImportRules.Settle(row, candidate, registered, await Others(import.ProductId, import.Id, ct), own,
                        await Registration(registered, ct));
            }
            catch (DomainException error) when (error.Status == 422 || error.Code == "source_revision")
            {
                rows[index] = ImportRules.Refuse(row, error);
            }
        }
        rows = ImportRules.Propagate(rows);
        var now = clock.GetUtcNow();
        return await store.Execute<IntakeImport>(new CommandContext(import.Id, IntakeKind, import.ProductId, import.Version,
            "intake-processed:" + import.Id, "IntakeImportProcessed",
            "Validierte Zeilen ueber den bestehenden Eingangspfad uebernommen; Ergebnis je Zeile gebunden.", actor,
            new { importId = import.Id, import.Version }, now), current => current! with
            {
                Version = current.Version + 1, Status = IntakeImportStatus.Processed, Rows = rows,
                Counts = ImportRules.Count(rows), ProcessedAt = now
            }, ct);
    }

    private async Task<IntakeImportRow[]> Classify(IntakeEvaluation evaluation, string productId, string importId,
        CancellationToken ct)
    {
        var known = await store.List<SafetyCase>("case", [productId], ct);
        var registrations = await Registrations(known, evaluation.Candidates, productId, ct);
        var others = await Others(productId, importId, ct);
        var candidates = evaluation.Candidates.ToDictionary(c => c.Line);
        return ImportRules.Propagate(evaluation.Rows.Select(row => row.Outcome == IntakeRowOutcome.Pending
            ? ImportRules.Classify(row, candidates[row.Line], productId, known, others, registrations) : row).ToArray());
    }

    // Version 1 of each targeted case whose first source is a row source and that changed since (for example by a
    // medical reclassification): the facts recorded for that source are those of the first registration.
    private async Task<SafetyCase[]> Registrations(IReadOnlyCollection<SafetyCase> known,
        IEnumerable<IntakeCandidate> candidates, string productId, CancellationToken ct)
    {
        var sources = candidates.Select(c => (CaseIdentity.For(productId, c.IncidentKey), c.SourceId)).ToHashSet();
        var registrations = new List<SafetyCase>();
        foreach (var target in known.Where(c => c.Version > 1 && c.Sources.FirstOrDefault() is { } first &&
            sources.Contains((c.Id, first.ExternalId))))
            if (await Registration(target, ct) is { } registration) registrations.Add(registration);
        return [.. registrations];
    }

    private async Task<SafetyCase?> Registration(SafetyCase target, CancellationToken ct) =>
        target.Version == 1 ? null : await store.GetVersion<SafetyCase>("case", target.Id, 1, ct);

    private async Task<IntakeImport[]> Others(string productId, string importId, CancellationToken ct) =>
        (await store.List<IntakeImport>(IntakeKind, [productId], ct)).Where(i => i.Id != importId).ToArray();

    private static string RegistrationKey(string importId, int line) =>
        "intake-import:" + Canonical.Hash(new { importId, line })[..40];

    private async Task<bool> OwnRegistration(string caseId, string key, CancellationToken ct) =>
        (await store.Audit(caseId, ct)).Any(e => e.Action == "ComplaintRegistered" && e.CorrelationId == key);

    private static Exposure? Target(Exposure[] exposures, DenominatorProposal proposal)
    {
        var matches = exposures.Where(e => e.ProductId == proposal.ProductId && e.Region == proposal.Region &&
            e.Start == proposal.Start && e.End == proposal.End).ToArray();
        Guard.Against(matches.Length > 1, "denominator_ambiguous",
            "Mehrere Nenner fuer Produkt, Region und Periode; keine stille Auswahl.", 409);
        return matches.SingleOrDefault();
    }

    private static VersionReference Reference(DistributionImport import) => new(import.Id, import.Version, Canonical.Hash(import));

    private static string[] Split(string? values) =>
        string.IsNullOrWhiteSpace(values) ? [] : values.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void RequireReader(Actor actor) =>
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);
}
