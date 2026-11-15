using Cohors.Domain;

namespace Cohors.Application;

public sealed class PublicSourceService(IStateStore store, IObjectStore objects, IPublicSourceReader reader,
    TimeProvider clock, PolicyCatalog policies)
{
    public async Task<PublicSourceRun> Read(string source, ReadPublicSourceCommand input, Actor actor, string key, CancellationToken ct)
    {
        actor.RequireHuman(Roles.Pms);
        actor.RequireProduct(input.ProductId);
        policies.Device(input.ProductId);
        Guard.Against(source is not ("fda" or "pubmed"), "source_not_allowed", "Nur feste FDA-/PubMed-Lesevertraege sind freigegeben.");
        var id = "read-" + Canonical.Hash(new { source, input.ProductId, actor.Id, key })[..32];
        var existing = await store.Get<PublicSourceRun>("source", id, ct);
        if (existing is not null) return existing;
        var owner = Guid.NewGuid().ToString("N");
        var run = new PublicSourceRun(id, 1, source, input.ProductId, "Reading", clock.GetUtcNow(), [], null,
            "public-metadata-readonly-1; max 5 results; no patient narratives; no real-product attribution", owner);
        var claimed = await store.Execute<PublicSourceRun>(new(id, "source", input.ProductId, 0, key, "PublicSourceReadRequested",
            "Lesender Abruf oeffentlicher Vergleichsmetadaten; keine Herstellerfallanlage.", actor, input, clock.GetUtcNow()), _ => run, ct);
        if (claimed.ExecutionId != owner) return claimed;
        PublicSourceItem[] items = [];
        string? error = null;
        try { items = await reader.Read(source, input.ProductId, ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or DomainException or OperationCanceledException)
        {
            error = ex is DomainException d ? d.Code : ex is OperationCanceledException ? "source_timeout_or_cancelled" : ex.GetType().Name;
        }
        return await store.Execute<PublicSourceRun>(new(id, "source", input.ProductId, 1, "result-" + Canonical.HashText(key), "PublicSourceReadCompleted",
            error is null ? "Oeffentliche Metadaten gelesen; nicht als eigene Vorfaelle oder klinische Evidenz freigegeben." :
                "Oeffentlicher Leseabruf fehlgeschlagen; keine Fixtures als Ersatz ausgegeben.", actor,
            new { source, items, error }, clock.GetUtcNow()), _ => run with
            { Version = 2, Status = error is null ? "ReadOnlyMetadata" : "Failed", Items = items, Error = error }, CancellationToken.None);
    }

    public async Task<Evidence> Approve(string runId, ApprovePublicSourceCommand input, Actor actor, string key, CancellationToken ct)
    {
        actor.RequireHuman(Roles.Medical);
        var run = await store.Get<PublicSourceRun>("source", runId, ct)
            ?? throw new DomainException("source_missing", "Quellenlauf fehlt.", 404);
        actor.RequireProduct(run.ProductId);
        Guard.Against(run.Version != input.ExpectedVersion, "version_conflict", "Veraltete Quellenversion.", 409);
        Guard.Against(run.Status != "ReadOnlyMetadata", "source_not_read", "Nur erfolgreich gelesene Metadaten koennen geprueft werden.");
        Guard.Text(input.Reason, "Klinische Vergleichbarkeit, Produktabgrenzung und Quellenrechte", 40);
        var item = run.Items.SingleOrDefault(i => i.ExternalId == input.ExternalId)
            ?? throw new DomainException("source_item_missing", "Quellenobjekt fehlt.", 404);
        var text = $"{item.Title}\n\n{item.Summary}\n\nOeffentliche Vergleichsmetadaten, kein eigener Herstellerfall, kein Kausalitaetsnachweis.\n" +
            $"Quelle: {item.Url}\nAbruf: {run.At:O}\nMenschliche Einordnung: {input.Reason}";
        var hash = await objects.Put(text, ct);
        var id = "public-" + Canonical.Hash(new { run.ProductId, item.ProvenanceHash, input.Reason })[..32];
        var evidence = new Evidence(id, 1, run.ProductId, item.Title, "PublicMetadata", text, hash, item.Url,
            $"{run.Source}: {item.ExternalId}, Metadaten", "Public metadata only; full-text rights not granted", "Product",
            item.PublishedAt ?? run.At, null, clock.GetUtcNow(), true, false, hash);
        return await store.Execute<Evidence>(new(id, "evidence", run.ProductId, 0, key, "PublicSourceApproved",
            input.Reason, actor, new { runId, input }, clock.GetUtcNow()), _ => evidence, ct);
    }
}
