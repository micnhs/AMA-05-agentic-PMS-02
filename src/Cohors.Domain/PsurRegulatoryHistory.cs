namespace Cohors.Domain;

public sealed record PsurRegulatoryHistory(string Contract, string[] Products, DateTimeOffset CutoffExclusiveUtc,
    PsurActionState<PsurReview>[] Reviews, string Coverage);

public static class PsurRegulatoryRules
{
    public const string Contract = "cohors-psur-regulatory-history-1";

    public static PsurRegulatoryHistory Capture(PortfolioSnapshotInput input, string[] products)
    {
        var reviews = input.RegulatoryReviews
            ?? throw new DomainException("psur_regulatory_source", "SQL-Schnitt der BS-Vorgaenge fehlt; nicht als leere Historie ausgeben.");
        var selected = reviews.Where(r => r.Products.All(products.Contains)).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        foreach (var review in selected) _ = PsurReviewRules.Products(review);
        var result = new PsurRegulatoryHistory(Contract, products.Order(StringComparer.Ordinal).ToArray(), PsurSources.EndExclusive(input.End),
            selected.Select(r => PsurSupplement.FreezeState(input.Start, input.End, input.History ?? [], "psurReview", r,
                v => v.Id, v => v.Version, v => v.Status, "QuestionClosed")).ToArray(),
            "Complete SQL cut only for correspondence wholly inside the frozen product membership, across its earlier series. " +
            "Wider-group correspondence is not copied into a narrower report and is not declared closed or absent. " +
            "Technical receipts, questions, replies, assessments and linked action versions remain distinct synthetic records. " +
            "CurrentAtFreeze is not the period-end state; full original correspondence is retained in the snapshot/JSON/ZIP.");
        Guard.Against(Products(result).Any(p => !products.Contains(p)), "psur_regulatory_scope",
            "Historische BS-Mitgliedschaft ueberschreitet den angeforderten Snapshot-Scope; nicht still verengen.", 409);
        return result;
    }

    public static PsurRegulatoryHistory? For(PsurSnapshot snapshot) =>
        snapshot.Group is { } group ? group.RegulatoryHistory : snapshot.RegulatoryHistory;

    public static string[] Products(PsurRegulatoryHistory? history) => history is null ? [] :
        history.Products.Concat(history.Reviews.SelectMany(r => PsurReviewRules.Products(r.CurrentAtFreeze)
            .Concat(r.AtCutoff is null ? [] : PsurReviewRules.Products(r.AtCutoff))))
            .Distinct(StringComparer.Ordinal).ToArray();

    public static IEnumerable<VersionReference> References(PsurRegulatoryHistory? history) =>
        (history?.Reviews ?? []).SelectMany(r => new[] { r.CurrentSource, r.CutoffSource }.OfType<VersionReference>()).Distinct();

    public static PsurDataSource[] Directory(PsurRegulatoryHistory? history) =>
        (history?.Reviews ?? []).SelectMany(r => new[] {
            new PsurDataSource("psurReview", r.CurrentSource, r.CurrentAtFreeze.Reference, "Synthetic notified-body correspondence",
                "SQL correspondence history", r.Selection, r.Reason)
        }.Concat(r.CutoffSource is { } prior && prior != r.CurrentSource ? [
            new PsurDataSource("psurReview", prior, r.AtCutoff!.Reference, "Synthetic notified-body correspondence at cutoff",
                "SQL correspondence history", "AtCutoff", "Konkrete historische BS-Version vor dem exklusiven N-Stichtag.")
        ] : [])).ToArray();

    public static void RequireSources(PsurRegulatoryHistory history, AggregateRevision[] revisions, DateOnly start, DateOnly end)
    {
        foreach (var state in history.Reviews)
        {
            var revision = revisions.SingleOrDefault(r => r.Kind == "psurReview" && r.Id == state.CurrentSource.Id &&
                r.Version == state.CurrentSource.Version);
            Guard.Against(revision is null || revision.Hash != state.CurrentSource.Hash ||
                Canonical.HashText(revision.Json) != state.CurrentSource.Hash, "psur_bs_source",
                "Eine eingefrorene BS-Quellversion fehlt oder wurde veraendert.", 409);
            var expected = PsurSupplement.FreezeState(start, end, revisions, "psurReview", Canonical.Read<PsurReview>(revision!.Json),
                r => r.Id, r => r.Version, r => r.Status, "QuestionClosed");
            Guard.Against(Canonical.Hash(state) != Canonical.Hash(expected), "psur_bs_cutoff",
                "BS-Inhalt, Auswahl oder Stichtagsstatus widerspricht der tatsaechlich gebundenen SQL-Historie.", 409);
        }
    }

    public static QualityFinding[] Check(PsurRegulatoryHistory? history, string[] products, DateOnly end)
    {
        if (history is null) return [new("psur_bs_history_unknown", "Minor",
            "Dieser historische Snapshot bindet keinen gesonderten BS-Korrespondenzschnitt; daraus folgt kein Nullbestand.",
            "Bei der naechsten kontrollierten Fassung die aktuelle und historische BS-Historie erfassen; alte Bytes unveraendert lassen.", null)];
        QualityFinding Invalid(string text) => new("psur_bs_history_integrity", "Critical", text,
            "BS-Versionen und Stichtagsstatus aus dem berechtigten SQL-Schnitt neu einfrieren, nicht rueckdatieren.", null);
        if (history.Contract != Contract || history.CutoffExclusiveUtc != PsurSources.EndExclusive(end) ||
            !history.Products.Order(StringComparer.Ordinal).SequenceEqual(products.Order(StringComparer.Ordinal)) ||
            history.Reviews.Select(r => r.CurrentSource.Id).Distinct().Count() != history.Reviews.Length)
            return [Invalid("BS-Historie hat einen abweichenden Produkt-/Stichtagsscope oder doppelte Vorgaenge.")];
        foreach (var state in history.Reviews)
        {
            try
            {
                if (PsurReviewRules.Products(state.CurrentAtFreeze).Any(p => !products.Contains(p)) ||
                    state.AtCutoff is { } cutoff && PsurReviewRules.Products(cutoff).Any(p => !products.Contains(p)))
                    return [Invalid("BS-Daten einer nicht eingefrorenen Produktmitgliedschaft duerfen nicht in diese Lesekopie gelangen.")];
            }
            catch (DomainException error) { return [Invalid(error.Message)]; }
            if (state.Selection is not ("OpenCarryOver" or "OpenInPeriod" or "UpdatedInPeriod" or "ExcludedClosedBeforePeriod" or
                    "PostCutoffAddendum" or "UnknownHistoricalState") || string.IsNullOrWhiteSpace(state.Reason) ||
                state.Selection == "ExcludedClosedBeforePeriod" && state.StatusAtCutoff != "QuestionClosed" ||
                state.CurrentSource != new VersionReference(state.CurrentAtFreeze.Id, state.CurrentAtFreeze.Version, Canonical.Hash(state.CurrentAtFreeze)) ||
                (state.AtCutoff is null
                    ? state.CutoffSource is not null || state.RecordedAtCutoff is not null || state.StatusAtCutoff != "NotRecordedAtCutoff"
                    : state.CutoffSource != new VersionReference(state.AtCutoff.Id, state.AtCutoff.Version, Canonical.Hash(state.AtCutoff)) ||
                        state.AtCutoff.Id != state.CurrentAtFreeze.Id || state.AtCutoff.Version > state.CurrentAtFreeze.Version ||
                        state.RecordedAtCutoff is null || state.RecordedAtCutoff >= history.CutoffExclusiveUtc ||
                        state.StatusAtCutoff != state.AtCutoff.Status))
                return [Invalid("Aktuelle/historische BS-Version, Hash oder behaupteter Stichtagsstatus ist inkonsistent.")];
        }
        return [];
    }
}
