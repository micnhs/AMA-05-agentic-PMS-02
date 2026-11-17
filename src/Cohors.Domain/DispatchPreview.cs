namespace Cohors.Domain;

public sealed record DispatchBinding(string Route, string AggregateId, int AggregateVersion,
    int PackageVersion, string ScopeHash, string PayloadHash, int? LedgerVersion);
public sealed record DispatchItem(string Name, string Kind, string Hash, string SourceId, int? SourceVersion);
public sealed record DispatchPreview(DispatchBinding Binding, string ProductId, string BusinessRecipient,
    string TechnicalRecipient, string Jurisdiction, string[] Countries, string Channel, string Environment,
    string Language, string Narrative, string? BusinessXml, string FrozenPayload,
    DispatchItem[] Originals, DispatchItem[] Attachments, Approval[] Approvals, string[] Boundaries);

public static class DispatchPreviews
{
    public static DispatchBinding CaseBinding(SafetyCase value)
    {
        var report = value.CurrentReport ?? throw new DomainException("report_required", "Bericht fehlt.");
        var json = report.FrozenJson ?? throw new DomainException("frozen_payload", "Eingefrorenes Paket fehlt.");
        return new("CaseSimulator", value.Id, value.Version, report.Version, report.ScopeHash, Canonical.HashText(json), null);
    }

    public static DispatchBinding DossierBinding(EudamedDossier value, int ledgerVersion) =>
        new("DossierSimulator", value.Id, value.Version, value.Revision, value.ScopeHash,
            value.FrozenPackageHash ?? throw new DomainException("eudamed_approval", "Eingefrorenes Paket fehlt."), ledgerVersion);

    public static void RequireConfirmation(DispatchBinding? actual, DispatchBinding expected) =>
        Guard.Against(actual != expected, "dispatch_preview_changed",
            "Die bestaetigte Vorschau passt nicht zu Version, Paket, Originalhash oder Versandreservierung. Erneut lesen und bewusst bestaetigen.", 409);

    public static DispatchPreview ForCase(SafetyCase value, SafetyCase? preparedSource, Actor actor)
    {
        ReportRules.RequireSendable(value, actor);
        var frozen = Canonical.Read<ReportDraft>(value.CurrentReport!.FrozenJson!);
        ReportTargets.RequireBound(frozen);
        var preparation = frozen.Preparation ?? throw new DomainException("dispatch_preview_source",
            "Altbericht ohne gebundenen Quellfall: Originalmetadaten sind nicht reproduzierbar. Kein Live-Ersatz; neue Fassung vorbereiten und freigeben.", 409);
        Guard.Against(preparedSource is null || preparation.CaseSource != PortfolioRules.Reference(preparedSource) ||
            preparedSource.Id != value.Id || preparedSource.ProductId != value.ProductId ||
            Canonical.Hash(preparedSource.Evidence) != frozen.EvidenceHash,
            "dispatch_preview_source", "Die genaue freigegebene Quellfallfassung fehlt oder ist inkonsistent; keine aktuellen Fallfelder als Ersatz.", 409);
        var source = preparedSource!;
        var attachments = frozen.AttachmentHashes.Select(hash =>
        {
            if (frozen.Mir is { } mir && mir.XmlHash == hash)
                return new DispatchItem("MIR XML", "application/xml", hash, value.Id, frozen.Version);
            var matches = source.Evidence.Where(e => e.Hash == hash).ToArray();
            Guard.Against(matches.Length == 0, "dispatch_preview_attachment", "Anhanghash hat keinen gebundenen Originalbezug.", 409);
            return matches.Length == 1
                ? new DispatchItem(matches[0].Title, matches[0].Kind, hash, matches[0].Id, matches[0].Version)
                : new DispatchItem(string.Join(" / ", matches.Select(e => e.Title)), "Mehrere Originalbezuege mit identischem Bytehash",
                    hash, string.Join(", ", matches.Select(e => $"{e.Id}@{e.Version}")), null);
        }).ToArray();
        return new(CaseBinding(value), preparation.ProductId, preparation.Target.Authority, preparation.Target.Simulator,
            frozen.Jurisdiction, [source.Country], "CaseService-Simulator / ohne AS4", "Simulation / synthetischer Pilot",
            frozen.Language, frozen.Narrative + "\n\nOffene Fragen:\n" + frozen.OpenQuestions, frozen.Mir?.Xml,
            value.CurrentReport!.FrozenJson!,
            source.Sources.Select(s => new DispatchItem(s.Title, s.Kind, s.Hash, s.Id, null)).ToArray(),
            attachments, value.Approvals.Where(a => a.ScopeHash == frozen.ScopeHash).ToArray(),
            ["Inhalt stammt ausschliesslich aus FrozenJson und dessen hashgebundener Quellfallfassung. Vorschau ist keine neue Freigabe.",
             "Originalbezuege sind Quellenreferenzen, nicht automatisch zusaetzliche Dateianhaenge. Die Anhangliste zeigt genau die genehmigten Anhanghashes.",
             "Laenderangabe: Fallland der gebundenen Quelle; Zieljurisdiktion und fachlicher/technischer Empfaenger sind separat ausgewiesen.",
             "Korrelations-ID und Versuchsdaten entstehen erst beim ausdruecklichen Abschluss. Dieser Simulator erzeugt keine AS4-Transporthuelle und sendet nicht an Behoerden.",
             .. preparation.Target.ExternalRequirements]);
    }

    public static DispatchPreview ForDossier(EudamedDossier value, int ledgerVersion, string frozenJson, Actor actor)
    {
        actor.RequireHuman(Roles.Regulatory);
        EudamedRules.RequireActorScope(value, actor);
        EudamedRules.RequireFrozen(value);
        Guard.Against(!value.Package.Binding.Simulated, "eudamed_external_not_activated",
            "Keine Versandvorschau fuer einen nicht freigegebenen externen Kanal.", 409);
        Guard.Against(Canonical.HashText(frozenJson) != value.FrozenPackageHash ||
            frozenJson != Canonical.Serialize(value.Package),
            "dispatch_preview_original", "Gespeichertes Originalpaket und freigegebener Inhalt stimmen nicht ueberein.", 409);
        var package = value.Package;
        var binding = package.Binding;
        var profile = binding.Profile;
        return new(DossierBinding(value, ledgerVersion), package.ProductId, profile.Recipient,
            profile.Domibus?.ReceiverPartyId ?? "Lokaler Dossier-Simulator", package.Content.Jurisdiction,
            package.Content.Countries, $"{profile.Mode} / {binding.ChannelId}@{binding.ChannelVersion} / Generation {binding.DispatchGeneration}",
            profile.Environment.ToString(), package.Content.Language,
            package.Content.Title + "\n\n" + package.Content.ClinicalRationale + "\n\n" + package.Content.RegulatoryRationale,
            package.BusinessXml, frozenJson,
            [.. package.Sources.Select(s => new DispatchItem(s.Source.Id, s.Kind + " / Quell-Snapshot",
                s.Source.Hash, s.Source.Id, s.Source.Version)),
                new("Zertifikatsoriginal (Referenz)", "Certificate", package.References.Certificate.OriginalHash,
                    package.References.Id, package.References.Version)],
            package.Attachments.Select(a => new DispatchItem(a.Name, a.MediaType, a.Hash, a.Source.Id, a.Source.Version)).ToArray(),
            value.Approvals,
            ["Fachinhalt, Business-XML, Anhanganordnung und Profil sind bereits eingefroren. Quell-Snapshots und Zertifikatsreferenz sind keine zusaetzlichen Transportanhaenge.",
             $"Business-XML SHA256: {package.BusinessXmlHash}. Profil SHA256: {binding.ProfileHash}.",
             "MessageId, ConversationId, Transportzeitstempel und AS4-Sicherheits-/Signaturheader entstehen gegebenenfalls erst je Transportversuch. Sie sind keine jetzt schon feststehende Fachpaket-Huelle.",
             "Aktuell ausschliesslich lokaler Simulator: keine AS4-Aussendung, keine Behoerdenkommunikation und keine fachliche Annahme durch diese Vorschau."]);
    }
}
