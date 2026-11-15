using Cohors.Domain;

namespace Cohors.Application;

public sealed class EvidenceService(IStateStore store, IObjectStore objects, TimeProvider clock)
{
    public async Task<Evidence> Revise(string id, ReviseEvidenceCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        var original = await store.Get<Evidence>("evidence", id, ct)
            ?? throw new DomainException("evidence_missing", "Dokument fehlt.", 404);
        actor.RequireProduct(original.ProductId);
        var context = new CommandContext(id, "evidence", original.ProductId, input.ExpectedVersion, key,
            "EvidenceVersionPublished", input.Reason, actor, input, clock.GetUtcNow());
        var receipt = await store.Receipt<Evidence>(context, ct);
        if (receipt is not null) return receipt;
        var change = await store.Get<ChangeRequest>("change", input.ChangeId, ct)
            ?? throw new DomainException("change_required", "Genehmigter kontrollierter Aenderungsauftrag fehlt.");
        Guard.Against(change.Status != "Approved" || change.DocumentId != id ||
            change.DocumentVersion != input.ExpectedVersion || change.ProductId != original.ProductId,
            "change_scope", "Auftrag muss genau diese Dokumentrevision im Produktscope genehmigen.");
        Guard.Against(!original.Synthetic || original.Kind == "Complaint", "controlled_document",
            "Nur kontrollierte synthetische Produktdokumente werden so revidiert; Originalquellen bleiben unveraendert.");
        Guard.Against(input.ValidFrom <= original.ValidFrom, "document_validity",
            "Eine Folgeversion braucht einen nachvollziehbaren spaeteren fachlichen Gueltigkeitsbeginn.");
        Guard.Text(input.Title, "Titel", 10, 200);
        Guard.Text(input.Content, "Dokumentinhalt", 50, 30000);
        Guard.Text(input.Reason, "Versionsbegruendung", 30);
        var hash = await objects.Put(input.Content, ct);
        return await store.Execute<Evidence>(context, current => current! with
        {
            Version = current.Version + 1, Title = input.Title, Content = input.Content, Hash = hash, ObjectKey = hash,
            ValidFrom = input.ValidFrom, ValidTo = null, RecordedAt = clock.GetUtcNow(),
            Locator = $"Kontrollierte Revision {current.Version + 1}; Aenderungsauftrag {change.Id}", ChangeId = change.Id
        }, ct);
    }
}
