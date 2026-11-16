namespace Cohors.Domain;

public sealed record PsurActionState<T>(VersionReference CurrentSource, T CurrentAtFreeze,
    VersionReference? CutoffSource, T? AtCutoff, DateTimeOffset? RecordedAtCutoff,
    string StatusAtCutoff, string Selection, string Reason) where T : class;
public sealed record PsurActionManifest(string Contract, DateTimeOffset CutoffExclusiveUtc,
    PsurActionState<Capa>[] Capas, PsurActionState<ChangeRequest>[] Changes);
public sealed record PsurDataSource(string Kind, VersionReference Source, string Name, string SourceType,
    string Origin, string Selection, string Reason);
public sealed record PsurDataDirectory(string Contract, DateTimeOffset DocumentsEffectiveAt,
    PsurDataSource[] Entries, string Coverage);

public static class PsurSupplement
{
    public static PsurActionManifest Actions(PortfolioSnapshotInput input)
    {
        return new("cohors-psur-actions-1", PsurSources.EndExclusive(input.End),
            input.Capas.Where(c => c.ProductId == input.ProductId).OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => FreezeState(input.Start, input.End, input.History ?? [], "capa", c, v => v.Id, v => v.Version, v => v.Status, "Verified")).ToArray(),
            input.Changes.Where(c => c.ProductId == input.ProductId).OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => FreezeState(input.Start, input.End, input.History ?? [], "change", c, v => v.Id, v => v.Version, v => v.Status, "Implemented")).ToArray());
    }

    internal static PsurActionState<T> FreezeState<T>(DateOnly periodStart, DateOnly periodEnd, AggregateRevision[] revisions, string kind, T current,
        Func<T, string> id, Func<T, int> version, Func<T, string> status, string terminal) where T : class
    {
        var cutoff = PsurSources.EndExclusive(periodEnd);
        var start = new DateTimeOffset(periodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var history = revisions.Where(h => h.Kind == kind && h.Id == id(current) && h.Version <= version(current)).ToArray();
        var revision = history.Where(h => h.At < cutoff).OrderByDescending(h => h.Version).FirstOrDefault();
        var original = revision is null ? null : Canonical.Read<T>(revision.Json);
        var selected = original is null
            ? history.Length == 0 ? "UnknownHistoricalState" : "PostCutoffAddendum"
            : status(original) != terminal
                ? history.Min(h => h.At) < start ? "OpenCarryOver" : "OpenInPeriod"
                : history.Any(h => h.At >= start && h.At < cutoff) ? "UpdatedInPeriod" : "ExcludedClosedBeforePeriod";
        return new(new(id(current), version(current), Canonical.Hash(current)), current,
            revision is null ? null : new(revision.Id, revision.Version, revision.Hash), original, revision?.At,
            original is null ? "NotRecordedAtCutoff" : status(original), selected,
            selected switch
            {
                "OpenCarryOver" => "Am N-Stichtag offene Altmassnahme; ein spaeterer Abschluss ersetzt diesen Status nicht.",
                "OpenInPeriod" => "In N erfasste, am Stichtag offene Massnahme.",
                "UpdatedInPeriod" => "In N dokumentierter Massnahmenstand, auch wenn am Stichtag abgeschlossen.",
                "ExcludedClosedBeforePeriod" => "Schon vor N abgeschlossen; nur im vollstaendigen Auswahlverzeichnis, nicht als offene Altmassnahme.",
                "PostCutoffAddendum" => "Erst nach dem Stichtag in SQL dokumentiert; separat gekennzeichneter Nachtrag, kein erfundener historischer Status.",
                _ => "Keine historische SQL-Version vorliegend; aktueller Zustand ist kein Stichtagsnachweis."
            });
    }

    public static PsurDataDirectory Directory(PortfolioSnapshotInput input, PsurSnapshot snapshot)
    {
        var entries = new List<PsurDataSource>();
        var numerators = snapshot.Periods.SelectMany(p => p.Regions).SelectMany(r => r.NumeratorSources ?? [])
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var narrative = snapshot.Cases.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var addenda = (snapshot.SourceManifest?.PostCutoffCases ?? []).Select(c => c.Source.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in input.Cases)
            entries.Add(new("case", PortfolioRules.Reference(c), c.Reference, "Synthetic manufacturer complaint",
                "SQL case history", numerators.Contains(c.Id) ? "Numerator" : narrative.Contains(c.Id) ? "NarrativeContext" :
                    addenda.Contains(c.Id) ? "PostCutoffAddendum" : "Excluded",
                numerators.Contains(c.Id) ? "Mindestens ein expliziter Perioden-/Regionszaehler bindet diese Quellversion." :
                    narrative.Contains(c.Id) ? "Offene/aktuelle Kontextakte oder unbekanntes Ereignisdatum; kein automatischer N-Zaehler." :
                    addenda.Contains(c.Id) ? "Nach Periodenende bekannt geworden; separat, nicht im Basiszaehler." :
                    "Ausserhalb der vier Ereignisperioden und der offenen/aktuellen Kontextauswahl."));
        foreach (var e in input.Documents)
        {
            var selected = snapshot.Documents.Any(d => d.Id == e.Id && d.Version == e.Version);
            var decision = selected ? "CurrentAtFreeze" : !e.Approved ? "ExcludedUnapproved" :
                e.ValidFrom > input.FrozenAt ? "ExcludedNotYetEffective" : e.ValidTo <= input.FrozenAt ? "ExcludedExpired" : "ExcludedSuperseded";
            entries.Add(new("evidence", new(e.Id, e.Version, Canonical.Hash(e)), e.Title, e.Kind, e.Origin, decision,
                selected ? "Einzige wirksame freigegebene Revision dieser Dokument-ID am Freeze-Zeitpunkt." :
                    "Nicht als aktuelles Dokument verwendet; Originalversion bleibt mit Hash und Ausschlussgrund nachweisbar."));
        }
        foreach (var exposure in input.Exposure)
            entries.Add(new("exposure", new(exposure.Id, exposure.Version, Canonical.Hash(exposure)), exposure.Population,
                exposure.Unit, exposure.Provenance, snapshot.Exposure.Any(e => e.Id == exposure.Id && e.Version == exposure.Version) ? "Denominator" : "Excluded",
                "Auswahl verlangt Produkt, Region, Einheit, exakte Periode und Freigabe; unpassende Daten sind kein Ersatznenner."));
        foreach (var trend in input.Trends)
            entries.Add(new("trend", new(trend.Id, trend.Version, Canonical.Hash(trend)), trend.Id, "Deterministic trend",
                trend.Input.MethodVersion, snapshot.Trends.Any(t => t.Id == trend.Id) ? "InPeriod" : "Excluded",
                "Nur passende Trendfenster als Kontext; kein Addieren von Trend- und Fallzaehlern."));
        if (snapshot.Actions is { } actions)
        {
            AddActions("capa", actions.Capas, c => c.Title);
            AddActions("change", actions.Changes, c => c.ProposedChange);
        }
        entries.AddRange(PsurRegulatoryRules.Directory(snapshot.RegulatoryHistory));
        void AddActions<T>(string kind, PsurActionState<T>[] actions, Func<T, string> title) where T : class
        {
            foreach (var action in actions)
            {
                entries.Add(new(kind, action.CurrentSource, title(action.CurrentAtFreeze), "Controlled PMS action",
                    "SQL action history", action.Selection, action.Reason));
                if (action.CutoffSource is { } prior && prior != action.CurrentSource)
                    entries.Add(new(kind, prior, title(action.AtCutoff!), "Controlled PMS action at cutoff",
                        "SQL action history", "AtCutoff", "Konkrete historische Version vor dem exklusiven N-Stichtag."));
            }
        }
        foreach (var run in input.SourceRuns ?? [])
            entries.Add(new("source", new(run.Id, run.Version, Canonical.Hash(run)), run.Source == "fda" ? "FDA / MAUDE" : "PubMed",
                run.Source == "fda" ? "Public adverse-event metadata repository" : "Bibliographic metadata database",
                run.QueryContract, run.Status == "ReadOnlyMetadata" ? "MetadataContextOnly" : "ExcludedSourceFailureOrIncomplete",
                $"Abrufstatus {run.Status}; weder eigene Herstellerfaelle noch Inzidenznenner. Einzelne Evidenzfreigaben sind separat gebunden."));
        if (snapshot.Series is { } series)
        {
            entries.Add(new("productGovernance", series.GovernanceSource, series.Reference, "Controlled product governance",
                "SQL product profile", "SeriesScope", "Hersteller, Basic-UDI, Zertifikat, Benannte Stelle, Verantwortung und Zeitplan."));
            if (series.PreviousReport is { } previous)
                entries.Add(new("psur", previous.Source, series.Reference, "Previous PSUR edition",
                    "SQL PSUR history", "PreviousReport", "Vorberichtsversion mit damaligem Status und simulierter Quittung, nicht heutiger Rueckinterpretation."));
        }
        return new("cohors-psur-directory-1", input.FrozenAt,
            entries.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Source.Id, StringComparer.Ordinal)
                .ThenBy(e => e.Source.Version).ToArray(),
            "All captured synthetic cases, document revisions, denominator candidates, trends, actions and public-read runs are listed, including exclusions. No clinical registry adapter or registry completeness claim. Public metadata are not manufacturer incidence. Regulatory field actions are a separately controlled pilot path, not inferred from CAPA titles.");
    }

    public static QualityFinding[] Check(PsurSnapshot snapshot)
    {
        QualityFinding Invalid(string text) => new("psur_supplement_integrity", "Critical", text,
            "Neuen vollstaendigen Snapshot erstellen; historische Staende/Ausschluesse nicht still reparieren.", null);
        if (snapshot.Actions is not { Contract: "cohors-psur-actions-1" } actions ||
            actions.CutoffExclusiveUtc != PsurSources.EndExclusive(snapshot.End) || actions.Capas is null || actions.Changes is null ||
            snapshot.Directory is not { Contract: "cohors-psur-directory-1" } directory ||
            directory.DocumentsEffectiveAt != snapshot.FrozenAt || directory.Entries is null)
            return [Invalid("Historischer Massnahmenstand oder vollstaendiges Quellen-/Auswahlverzeichnis fehlt.")];
        var findings = new List<QualityFinding>();
        CheckActions(actions.Capas, c => c.Id, c => c.Version, c => c.Status, snapshot.Capas);
        CheckActions(actions.Changes, c => c.Id, c => c.Version, c => c.Status, snapshot.Changes);
        void CheckActions<T>(PsurActionState<T>[] values, Func<T, string> id, Func<T, int> version,
            Func<T, string> status, T[] selected) where T : class
        {
            foreach (var value in values)
                if (value.CurrentSource != new VersionReference(id(value.CurrentAtFreeze), version(value.CurrentAtFreeze), Canonical.Hash(value.CurrentAtFreeze)) ||
                    (value.AtCutoff is null ? value.CutoffSource is not null || value.RecordedAtCutoff is not null || value.StatusAtCutoff != "NotRecordedAtCutoff" :
                        value.CutoffSource != new VersionReference(id(value.AtCutoff), version(value.AtCutoff), Canonical.Hash(value.AtCutoff)) ||
                        value.RecordedAtCutoff is null || value.RecordedAtCutoff >= actions.CutoffExclusiveUtc || value.StatusAtCutoff != status(value.AtCutoff)))
                    findings.Add(Invalid("Aktueller/historischer Massnahmeninhalt, Status oder Quellhash widerspricht der gebundenen Version."));
            if (!values.Where(v => v.Selection != "ExcludedClosedBeforePeriod").Select(v => Canonical.Hash(v.CurrentAtFreeze))
                .Order(StringComparer.Ordinal).SequenceEqual(selected.Select(Canonical.Hash).Order(StringComparer.Ordinal)))
                findings.Add(Invalid("Massnahmenauswahl und offen ausgewiesene Alt-/Periodenmassnahmen widersprechen dem Manifest."));
        }
        var keys = directory.Entries.Select(e => (e.Kind, e.Source)).ToHashSet();
        if (keys.Count != directory.Entries.Length ||
            snapshot.Documents.Any(e => !keys.Contains(("evidence", new(e.Id, e.Version, Canonical.Hash(e))))) ||
            (snapshot.SourceManifest?.Cases ?? []).Any(c => !keys.Contains(("case", c.Reference))) ||
            PsurRegulatoryRules.Directory(snapshot.RegulatoryHistory).Any(e => !directory.Entries.Contains(e)))
            findings.Add(Invalid("Ausgewaehlte Dokument-/Fallversionen fehlen im eindeutigen Quellenverzeichnis."));
        return findings.ToArray();
    }
}
