using System.Text;
using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class PublicSourceService
{
    public async Task<SignalDocumentImportResult> ImportDocument(ImportSignalDocumentCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms);
        actor.RequireProduct(input.ProductId);
        _ = policies.Device(input.ProductId);
        Guard.Against(!input.MetadataConfirmed || !input.RightsConfirmed || !input.ContentEligibilityConfirmed,
            "signal_document_confirmation", "Produkt, Vergleichsscope, fruehere Kenntnis, Nutzungsrechte und Ausschluss echter Patienten-/Klinikdaten ausdruecklich bestaetigen.");
        Guard.Against(input.Scope is not ("Synthetic" or "PublicAuthorized"),
            "signal_document_scope", "Nur synthetische oder ausdruecklich zur Nutzung berechtigte oeffentliche Vergleichsinhalte.");
        Guard.Text(input.ExternalId, "Externe Dokument-/Quellreferenz", 3, 100);
        Guard.Text(input.Title, "Dokumenttitel", 5, 200);
        Guard.Text(input.Provenance, "Dokumentherkunft", 40, 4000);
        Guard.Text(input.Rights, "Konkrete Nutzungsrechte und erlaubter Umfang", 40, 4000);
        Guard.Text(input.KnowledgeReason, "Beleg oder offene Grenze der frueheren Kenntnis", 40, 4000);
        Guard.Text(input.Reason, "Begruendete Signalaufnahme", 40, 4000);
        var now = clock.GetUtcNow();
        Guard.Against(input.KnowledgeStatus is not ("Known" or "NotEstablished") ||
            input.KnowledgeStatus == "Known" && (input.FirstKnownAt is null || input.FirstKnownAt > now) ||
            input.KnowledgeStatus == "NotEstablished" && input.FirstKnownAt is not null,
            "signal_document_knowledge", "Fruehere Kenntnis belegt eingeben oder ausdruecklich als nicht belegt bestaetigen. Datei-/Uploadzeit sind kein Ersatz.");
        if (input.Scope == "Synthetic")
        {
            Guard.Against(!input.Provenance.StartsWith("SYNTHETISCH:", StringComparison.Ordinal) || input.OriginUri is not null,
                "signal_document_origin", "Synthetische Herkunft mit SYNTHETISCH: kennzeichnen; keine oeffentliche Originaladresse behaupten.");
        }
        else if (!Uri.TryCreate(input.OriginUri, UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.OriginalString.Length > 2000)
        {
            throw new DomainException("signal_document_origin",
                "Stabile oeffentliche HTTPS-Originaladresse ohne Zugangsdaten, Query oder Fragment erforderlich. Sie wird nicht abgerufen.");
        }
        Guard.Against(input.ContentBase64 is not { Length: > 0 and <= 174764 },
            "signal_document_size", "Originaldatei bis 128 KiB erforderlich.", 413);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(input.ContentBase64); }
        catch (FormatException) { throw new DomainException("signal_document_encoding", "Ungueltige Base64-Originalbytes."); }
        var text = SignalDocumentRules.Extract(input.Name, input.MediaType, bytes);
        var originalHash = SignalDocumentRules.Hash(bytes);
        var textHash = Canonical.HashText(text);
        var id = "document-" + Canonical.Hash(new { input.ProductId, actor.Id, key })[..32];
        SignalDocumentCaseReference? comparison = null;
        Guard.Against((input.ComparisonCaseId is null) != (input.ComparisonCaseVersion is null) ||
            input.ComparisonCaseId is null && input.ComparisonReason is not null,
            "signal_document_comparison", "Vergleichsfall mit genauer Version und Begruendung angeben oder bewusst ohne Fallbezug importieren.");
        if (input.ComparisonCaseId is not null)
        {
            var comparisonReason = Guard.Text(input.ComparisonReason, "Vergleichsbezug, keine Vorfallidentitaet", 40, 4000);
            var comparisonVersion = input.ComparisonCaseVersion
                ?? throw new DomainException("signal_document_comparison", "Genaue Vergleichsfallfassung fehlt.");
            var value = await store.GetVersion<SafetyCase>("case", input.ComparisonCaseId,
                comparisonVersion, ct)
                ?? throw new DomainException("signal_document_case", "Die genaue Vergleichsfallfassung fehlt.", 404);
            actor.RequireProduct(value.ProductId);
            Guard.Against(value.ProductId != input.ProductId, "signal_document_comparison",
                "Vergleichsfall und bestaetigter Produktscope muessen uebereinstimmen.", 409);
            comparison = new(new(value.Id, value.Version, Canonical.Hash(value)), value.Reference, comparisonReason);
        }
        var context = new CommandContext(id, "source", input.ProductId, 0, key, "SignalDocumentImported",
            input.Reason, actor, new
            {
                input.ProductId, input.Name, input.MediaType, input.ExternalId, input.Title, input.Scope,
                input.OriginUri, input.Provenance, input.Rights, input.KnowledgeStatus, input.FirstKnownAt,
                input.KnowledgeReason, input.MetadataConfirmed, input.RightsConfirmed, input.ContentEligibilityConfirmed,
                input.ComparisonCaseId, input.ComparisonCaseVersion, input.ComparisonReason, input.Reason,
                comparison, originalHash, textHash, byteLength = bytes.Length, extractionVersion = SignalDocumentRules.ExtractionVersion
            }, now)
        {
            Dependencies = comparison is null ? [] : [new("case", comparison.Source)]
        };
        if (await store.Receipt<PublicSourceRun>(context, ct) is { } receipt)
            return new(receipt, await DocumentMatches(receipt, ct), true);
        Guard.Against(await objects.PutBytes(bytes, ct) != originalHash || await objects.Put(text, ct) != textHash,
            "signal_document_storage", "Original- oder Textspeicherung hat einen abweichenden Hash; kein Import bestaetigt.", 409);
        var document = new ImportedSignalDocument(SignalDocumentRules.Contract, input.Name, input.MediaType,
            new("cohors:signal-original:" + originalHash, originalHash, bytes.Length),
            new("cohors:signal-text:" + textHash, textHash, Encoding.UTF8.GetByteCount(text)),
            SignalDocumentRules.ExtractionVersion, input.Scope, input.Provenance, input.Rights,
            input.KnowledgeStatus, input.FirstKnownAt, input.KnowledgeReason, actor.Id, comparison);
        var run = await store.Execute<PublicSourceRun>(context, _ => new(id, 1, "document", input.ProductId,
            SignalDocumentRules.Status, now,
            [new(input.ExternalId, input.Title, input.OriginUri ?? "cohors:signal-document:" + id, null, text, originalHash)],
            null, SignalDocumentRules.Contract + "; " + SignalDocumentRules.ExtractionVersion + "; no automatic case or AI",
            SignalReviews: [SignalReviewRules.Pending("source", id, input.ExternalId, originalHash)], Document: document), ct);
        return new(run, await DocumentMatches(run, ct), false);
    }

    private async Task<SignalDocumentMatch[]> DocumentMatches(PublicSourceRun run, CancellationToken ct)
    {
        var documents = SignalDocumentLookup.ForSource(run, await store.List<PublicSourceRun>("source", [run.ProductId], ct));
        return documents.Matches(documents.Get(run)
            ?? throw new DomainException("signal_document_missing", "Kein importiertes Signaldokument.", 404));
    }

    public async Task<SignalDocumentOriginal> ReadDocument(string id, int version, Actor actor, CancellationToken ct = default)
    {
        var current = await store.Get<PublicSourceRun>("source", id, ct)
            ?? throw new DomainException("source_missing", "Quellenakte fehlt.", 404);
        actor.RequireProduct(current.ProductId);
        var run = await store.GetVersion<PublicSourceRun>("source", id, version, ct)
            ?? throw new DomainException("source_version_missing", "Die genaue Quellenfassung fehlt.", 404);
        actor.RequireProduct(run.ProductId);
        var document = SignalDocumentRules.RequireIntegrity(run);
        return new(await ReadDocumentBytes(run, ct), document.Name, document.MediaType);
    }

    private async Task<byte[]> ReadDocumentBytes(PublicSourceRun run, CancellationToken ct)
    {
        var document = SignalDocumentRules.RequireIntegrity(run);
        var bytes = await objects.ReadBytes(document.Original.Hash, ct);
        Guard.Against(bytes.Length != document.Original.Length || SignalDocumentRules.Hash(bytes) != document.Original.Hash,
            "signal_document_integrity", "Originalbytes stimmen nicht mit der gebundenen Quelle ueberein.", 409);
        var text = SignalDocumentRules.Extract(document.Name, document.MediaType, bytes);
        Guard.Against(text != run.Items[0].Summary || await objects.Read(document.Text.Hash, ct) != text,
            "signal_document_integrity", "Gelesener Text und unveraenderte Originaldatei unterscheiden sich.", 409);
        return bytes;
    }

    private async Task<Evidence> ApproveDocument(PublicSourceRun run, ApprovePublicSourceCommand input,
        Actor actor, string key, CancellationToken ct)
    {
        var document = SignalDocumentRules.RequireIntegrity(run);
        Guard.Text(input.Reason, "Medizinische Vergleichbarkeit, Quellenrechte und Grenzen", 40);
        Guard.Against(input.ExternalId != run.Items[0].ExternalId, "source_item_missing", "Originaldokument fehlt.", 404);
        var review = SignalReviewRules.Find(run.SignalReviews, input.ExternalId, document.Original.Hash);
        Guard.Against(review?.Status != SignalReviewRules.Relevant ||
            review.History.LastOrDefault() is not { Action: "ConfirmRelevance", Origin: "Reviewer" },
            "signal_document_review", "Zuerst die Relevanz der konkreten Originalquelle menschlich bestaetigen.", 409);
        await ReadDocumentBytes(run, ct);
        var item = run.Items[0];
        var source = new VersionReference(run.Id, run.Version, Canonical.Hash(run));
        var id = "signal-evidence-" + Canonical.Hash(new { source, input })[..32];
        var evidence = new Evidence(id, 1, run.ProductId, item.Title, "SignalDocument",
            item.Summary, document.Text.Hash, item.Url,
            $"Import {run.Id}@{run.Version}; Original {document.Original.Hash}; {document.ExtractionVersion}",
            document.Rights, "Product", run.At, null, clock.GetUtcNow(), true,
            document.Scope == "Synthetic", document.Text.Hash);
        return await store.Execute<Evidence>(new(id, "evidence", run.ProductId, 0, key, "SignalDocumentEvidenceApproved",
            input.Reason, actor, new { source, input }, clock.GetUtcNow())
        { Dependencies = [new("source", source)] }, _ => evidence, ct);
    }
}

internal sealed class SignalDocumentLookup
{
    internal sealed record Source(string ProductId, VersionReference Reference, string ExternalId,
        string Title, ImportedSignalDocument Document);

    private readonly Dictionary<(string Product, string Id, int Version), Source> sources;
    private readonly ILookup<(string Product, string Hash), Source> byHash;
    private readonly ILookup<(string Product, string ExternalId), Source> byReference;

    public SignalDocumentLookup(IEnumerable<PublicSourceRun> runs)
    {
        var prepared = runs
            .Where(IsDocument)
            .Select(run =>
            {
                var document = SignalDocumentRules.RequireIntegrity(run);
                return new Source(run.ProductId, new(run.Id, run.Version, Canonical.Hash(run)),
                    run.Items[0].ExternalId, run.Items[0].Title, document);
            }).ToArray();
        sources = prepared.ToDictionary(source => (source.ProductId, source.Reference.Id, source.Reference.Version));
        byHash = prepared.ToLookup(source => (source.ProductId, source.Document.Original.Hash));
        byReference = prepared.ToLookup(source => (source.ProductId, source.ExternalId));
    }

    public static bool IsDocument(PublicSourceRun run) =>
        run.Document is not null || run.Source == "document" || run.Status == SignalDocumentRules.Status;

    public static SignalDocumentLookup ForSource(PublicSourceRun selected, IEnumerable<PublicSourceRun> runs) =>
        new(runs.Where(run => run.ProductId == selected.ProductId && run.Id != selected.Id).Append(selected));

    public Source? Get(PublicSourceRun run) => sources.GetValueOrDefault((run.ProductId, run.Id, run.Version));

    public SignalDocumentMatch[] Matches(Source source) =>
        byHash[(source.ProductId, source.Document.Original.Hash)]
            .Concat(byReference[(source.ProductId, source.ExternalId)])
            .Where(other => other.Reference.Id != source.Reference.Id)
            .DistinctBy(other => other.Reference.Id, StringComparer.Ordinal)
            .OrderBy(other => other.Reference.Id, StringComparer.Ordinal)
            .Select(other => new SignalDocumentMatch(other.Reference, other.Title,
                other.Document.Original.Hash == source.Document.Original.Hash
                    ? "IdenticalBytes" : "SameReferenceDifferentBytes", other.Document.Original.Hash))
            .ToArray();
}
