using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class EudamedService
{
    private static readonly string[] SourceKinds =
        ["case", "trend", "summaryAgreement", "summaryReport", "psur", "capa", "eudamedDossier", "eudamedDocument"];

    public async Task<EudamedDossier> GetDossier(string id, Actor actor, CancellationToken ct = default)
    {
        Reader(actor);
        var value = await store.Get<EudamedDossier>("eudamedDossier", id, ct)
            ?? throw new DomainException("eudamed_dossier_missing", "Dossier fehlt.", 404);
        EudamedRules.RequireActorScope(value, actor);
        return value;
    }

    public async Task<EudamedDossier[]> DossierHistory(string id, Actor actor, CancellationToken ct = default)
    {
        var value = await GetDossier(id, actor, ct);
        return (await store.History<EudamedDossier>("eudamedDossier", [value.ProductId], ct))
            .Where(d => d.Id == id && EudamedRules.CanRead(d, actor)).OrderByDescending(d => d.Version).ToArray();
    }

    public async Task<EudamedSourceOption[]> SourceOptions(string product, Actor actor, CancellationToken ct = default)
    {
        Reader(actor);
        actor.RequireProduct(product);
        var result = new List<EudamedSourceOption>();
        foreach (var kind in SourceKinds)
            foreach (var value in await store.List<JsonElement>(kind, [product], ct))
            {
                if (!SourceVisible(kind, value, actor)) continue;
                var id = value.GetProperty("id").GetString()!;
                var label = value.TryGetProperty("title", out var title) ? title.GetString() : null;
                if (kind == "eudamedDossier")
                    label = value.GetProperty("package").GetProperty("content").GetProperty("title").GetString();
                if (kind == "eudamedDocument") label = value.GetProperty("name").GetString();
                result.Add(new(kind, id, value.GetProperty("version").GetInt32(), product, label ?? id));
            }
        return result.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Label, StringComparer.Ordinal).ToArray();
    }

    public async Task<EudamedDocument> RecordDocument(EudamedDocumentCommand input, Actor actor, string key,
        CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        _ = policies.Device(input.ProductId);
        Guard.Text(input.Name, "PDF-Originalname", 5, 150);
        Guard.Against(input.Name != Path.GetFileName(input.Name) ||
            input.Name.Any(c => c is '/' or '\\' || char.IsControl(c)) ||
            !input.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || input.MediaType != "application/pdf",
            "eudamed_document_type", "Nur ein einfacher PDF-Dateiname und application/pdf sind zugelassen.");
        Guard.Text(input.Provenance, "Synthetische Originalprovenienz", 40, 4000);
        Guard.Against(!input.Provenance.StartsWith("SYNTHETISCH:", StringComparison.Ordinal),
            "eudamed_document_origin", "Der Pilot nimmt hier ausschliesslich ausdruecklich synthetische PDF-Originale entgegen.");
        Guard.Text(input.Reason, "Dokumentaufnahme", 30, 4000);
        Guard.Against(input.ContentBase64 is not { Length: > 0 and <= 2796204 },
            "eudamed_document_size", "Ein synthetisches PDF bis2MiB ist erforderlich.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(input.ContentBase64); }
        catch (FormatException) { throw new DomainException("eudamed_document_encoding", "Ungueltige Base64-Originalbytes."); }
        Guard.Against(bytes.Length is < 16 or > 2 * 1024 * 1024 || !bytes.AsSpan().StartsWith("%PDF-"u8) ||
            bytes.AsSpan(Math.Max(0, bytes.Length - 1024)).IndexOf("%%EOF"u8) < 0,
            "eudamed_document_pdf", "PDF-Signatur und Abschluss fehlen; keine Textdatei als PDF ausgeben.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = "eudamed-document-" + Canonical.Hash(new { actor.Id, key, input.ProductId })[..32];
        var context = Context(id, "eudamedDocument", input.ProductId, 0, key, "EudamedSyntheticDocumentRecorded",
            input.Reason, actor, new { input.ProductId, input.Name, input.MediaType, input.Provenance, input.Reason, hash, Length = bytes.Length });
        if (await store.Receipt<EudamedDocument>(context, ct) is { } replay) return replay;
        Guard.Against(await objects.PutBytes(bytes, ct) != hash, "eudamed_document_integrity", "Originalspeicherung hat einen abweichenden Hash.", 409);
        return await store.Execute<EudamedDocument>(context, _ => new(id, 1, input.ProductId, input.Name,
            input.MediaType, hash, bytes.Length, input.Provenance, actor.Id, clock.GetUtcNow(), true), ct);
    }

    public async Task<EudamedDossier> DraftDossier(string? id, DraftEudamedDossierCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        _ = policies.Device(input.ProductId);
        Guard.Text(input.Reason, "Begruendete Dossierfassung", 50, 4000);
        var content = input.Content ?? throw new DomainException("eudamed_content", "Dossierinhalt fehlt.");
        EudamedRules.RequireContent(input.Kind, content);
        Guard.Against(input.Sources is not { Length: > 0 and <= 64 } ||
            input.Sources.Any(s => s is null || !SourceKinds.Contains(s.Kind, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(s.Id) || s.Version < 1) ||
            input.Sources.Select(s => (s.Kind, s.Id)).Distinct().Count() != input.Sources.Length,
            "eudamed_sources", "Eindeutige ausdruecklich versionierte Originalquellen sind erforderlich.");
        id ??= "eudamed-dossier-" + Canonical.Hash(new { actor.Id, key, input.ProductId })[..32];
        var context = Context(id, "eudamedDossier", input.ProductId, input.ExpectedVersion, key,
            "EudamedDossierDrafted", input.Reason, actor, input);
        if (await store.Receipt<EudamedDossier>(context, ct) is { } replay)
        {
            EudamedRules.RequireActorScope(replay, actor);
            return replay;
        }
        var current = await store.Get<EudamedDossier>("eudamedDossier", id, ct);
        if (current is not null) EudamedRules.RequireActorScope(current, actor);
        Guard.Against((current?.Version ?? 0) != input.ExpectedVersion, "version_conflict", "Dossierfassung hat sich geaendert.", 409);
        Guard.Against(current is not null && (current.ProductId != input.ProductId || current.Kind != input.Kind),
            "eudamed_dossier_identity", "Produkt und Dossiertyp duerfen nicht umetikettiert werden.", 409);
        var channel = await store.Get<EudamedChannel>("eudamedChannel", EudamedContracts.ChannelId(input.ProductId), ct)
            ?? throw new DomainException("eudamed_configuration_missing", "Zuerst ein vollstaendiges Profil ausdruecklich aktivieren.", 409);
        Guard.Against(!channel.Enabled, "eudamed_channel_disabled", "Aktiver Dossierkanal ist deaktiviert.", 409);
        var readiness = await Readiness(channel.Profile, input.ProductId, null, ct);
        Guard.Against(!readiness.CanDispatch, "eudamed_not_ready", string.Join(" ", readiness.Blockers), 409);
        var application = await GetActor(channel.Profile.ActorId, actor, ct);
        var references = await GetReferences(channel.Profile.ReferencesId, actor, ct);
        var binding = new EudamedBinding(channel.Id, channel.Version, channel.DispatchGeneration,
            Canonical.Hash(channel.Profile), channel.Profile, EudamedContracts.Bind(application),
            EudamedContracts.Bind(references), references.ActorIdentity, EudamedContracts.WsdlSha256,
            channel.Profile.Mode == EudamedMode.Simulator);
        var sources = await CaptureSources(input.Sources, input.ProductId, actor, ct);
        RequireSourceSelection(input.Kind, content, sources);
        if (input.Kind == EudamedDossierKind.PSUR)
            sources = await CapturePsurReferences(sources, references, actor, ct);
        var calendar = policies.Calendar(content.Jurisdiction);
        var attachments = await PrepareAttachments(input.Kind, id, content, sources, actor, ct);
        var composed = composer.Compose(input.Kind, id, content, binding, references, calendar, sources, attachments);
        var package = new EudamedPackage(EudamedContracts.Business, input.Kind, id, (current?.Revision ?? 0) + 1,
            input.ProductId, binding, references, content, calendar, sources, composed.Xml,
            Canonical.HashText(composed.Xml), attachments, composed.Validation, binding.Simulated);
        RequireLineage(package);
        Guard.Against(current is not null && EudamedRules.ObligationKey(current.Package) != EudamedRules.ObligationKey(package),
            "eudamed_dossier_obligation", "Ein bestehendes Dossier darf nicht auf eine andere Meldepflicht oder Umgebung umetikettiert werden.", 409);
        Guard.Against(current is { Attempts.Length: > 0 } && current.Package.Binding.ProfileHash != package.Binding.ProfileHash,
            "eudamed_original_profile", "Ein bereits gestarteter Vorgang bleibt seinem urspruenglichen Profil zugeordnet.", 409);
        var ledger = await EnsureLedger(package, actor, ct);
        var previousLedger = current is null ? null : await GetLedger(current.Package, ct);
        RequireNoPending(ledger);
        if (previousLedger is not null) RequireNoPending(previousLedger);
        var dependencies = PackageDependencies(package, channel);
        dependencies.Add(LedgerDependency(ledger));
        if (previousLedger is not null && previousLedger.Id != ledger.Id) dependencies.Add(LedgerDependency(previousLedger));
        var accessProducts = (current?.AccessProducts ?? []).Concat(EudamedRules.AccessProducts(package))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return await store.Execute<EudamedDossier>(context with { Dependencies = dependencies.ToArray() }, stored =>
            new(id, input.ExpectedVersion + 1, input.ProductId, input.Kind, package.Revision, package,
                Canonical.Hash(package), "Draft", [], null, stored?.Attempts ?? [], stored?.PlannedResponses ?? [],
                stored?.BusinessResponses ?? [], stored?.Correspondence ?? [], stored?.Distributions ?? [],
                stored?.CreatedBy ?? actor.Id, stored?.CreatedAt ?? clock.GetUtcNow()) { AccessProducts = accessProducts }, ct);
    }

    public async Task<EudamedDossier> ApproveDossier(string id, ApprovalCommand input, Actor actor, string key,
        CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var value = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Nachvollziehbare medizinische/regulatorische Paketpruefung", 80, 4000);
        var context = Context(id, "eudamedDossier", value.ProductId, input.ExpectedVersion, key,
            "EudamedDossierApproved", input.Reason, actor, input);
        if (await store.Receipt<EudamedDossier>(context, ct) is { } replay)
        {
            EudamedRules.RequireActorScope(replay, actor);
            return replay;
        }
        Guard.Against(value.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
        var role = value.ReviewStatus switch
        {
            "Draft" => Roles.Medical,
            "MedicalApproved" => Roles.Regulatory,
            _ => throw new DomainException("eudamed_review_state", "Keine aktuelle freizugebende Dossierfassung.", 409)
        };
        actor.RequireHuman(role);
        Guard.Against(value.Approvals.Any(a => a.ActorId == actor.Id), "four_eyes", "Getrennte menschliche Konten sind erforderlich.", 403);
        var disposition = role == Roles.Medical ? input.FindingDisposition :
            value.Approvals.SingleOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == value.ScopeHash)?.FindingDisposition;
        EudamedRules.RequirePackage(value.Package, disposition);
        RequireProjection(value.Package);
        Guard.Against(value.ScopeHash != Canonical.Hash(value.Package), "eudamed_package_hash", "Paketbindung wurde veraendert.", 409);
        RequireLineage(value.Package);
        var channel = await CurrentChannel(value.Package.Binding, value.ProductId, ct);
        var ledger = await GetLedger(value.Package, ct);
        RequireNoPending(ledger);
        var dependencies = PackageDependencies(value.Package, channel);
        dependencies.Add(LedgerDependency(ledger));
        if (role == Roles.Regulatory)
        {
            Guard.Against(value.Approvals.Length != 1 || value.Approvals[0].Role != Roles.Medical ||
                value.Approvals[0].ScopeHash != value.ScopeHash, "medical_required", "Aktuelle gebundene medizinische Freigabe fehlt.", 409);
            Guard.Against(await objects.Put(Canonical.Serialize(value.Package), ct) != value.ScopeHash,
                "eudamed_freeze_hash", "Eingefrorenes Original und signierter Scope unterscheiden sich.", 409);
        }
        return await store.Execute<EudamedDossier>(context with { Dependencies = dependencies.ToArray() }, current =>
        {
            var dossier = current ?? throw new DomainException("eudamed_dossier_missing", "Dossier fehlt.", 404);
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, dossier.ScopeHash,
                clock.GetUtcNow(), "Pruefung genau dieses Fach-/Evidenz-/XML-/PDF-/Profilpakets; keine autonome Risikoakzeptanz oder reale Uebermittlung.",
                input.Reason, disposition);
            return dossier with { Version = dossier.Version + 1, Approvals = [.. dossier.Approvals, approval],
                ReviewStatus = role == Roles.Medical ? "MedicalApproved" : "Frozen",
                FrozenPackageHash = role == Roles.Regulatory ? dossier.ScopeHash : null };
        }, ct);
    }

    private async Task<EudamedSource[]> CaptureSources(EudamedSourceSelection[] selections, string product,
        Actor actor, CancellationToken ct)
    {
        var result = new List<EudamedSource>();
        long length = 0;
        foreach (var selection in selections)
        {
            var value = await store.Get<JsonElement>(selection.Kind, selection.Id, ct);
            Guard.Against(value.ValueKind != JsonValueKind.Object, "eudamed_source_missing", "Versionierte Dossierquelle fehlt.", 404);
            var sourceProduct = value.GetProperty("productId").GetString()!;
            actor.RequireProduct(sourceProduct);
            Guard.Against(sourceProduct != product || !SourceVisible(selection.Kind, value, actor),
                "eudamed_source_scope", "Quelle liegt ausserhalb des ausdruecklichen Produkt-/Gruppenscopes.", 403);
            Guard.Against(value.GetProperty("version").GetInt32() != selection.Version ||
                value.GetProperty("id").GetString() != selection.Id, "dependency_version", "Ausgewaehlte Quelle wurde geaendert.", 409);
            var json = Canonical.Serialize(value);
            length += Encoding.UTF8.GetByteCount(json);
            Guard.Against(length > 32L * 1024 * 1024, "eudamed_source_size", "Die vollstaendigen Quelloriginale ueberschreiten die32MiB-Pilotgrenze; keine stille Kuerzung.");
            result.Add(new(selection.Kind, new(selection.Id, selection.Version, Canonical.HashText(json)), json));
        }
        return result.ToArray();
    }

    private static bool SourceVisible(string kind, JsonElement value, Actor actor) => kind switch
    {
        "psur" => PsurGroupRules.CanRead(Canonical.Read<Psur>(value.GetRawText()).Snapshot, actor),
        "eudamedDossier" => EudamedRules.CanRead(Canonical.Read<EudamedDossier>(value.GetRawText()), actor),
        _ => actor.Products.Contains(value.GetProperty("productId").GetString(), StringComparer.Ordinal)
    };

    private async Task<EudamedSource[]> CapturePsurReferences(EudamedSource[] sources, EudamedReferences leading,
        Actor actor, CancellationToken ct)
    {
        var psur = EudamedRules.Source<Psur>(sources, "psur");
        var result = sources.ToList();
        foreach (var member in psur.Snapshot.Group?.Members.Where(m => m.Governance.ProductId != psur.ProductId) ?? [])
        {
            actor.RequireProduct(member.Governance.ProductId);
            var value = await GetReferences("eudamed-references-" + member.Governance.ProductId, actor, ct);
            result.Add(new("eudamedReferences", EudamedContracts.Bind(value), Canonical.Serialize(value)));
        }
        var all = result.ToArray();
        _ = EudamedRules.PsurMemberReferences(psur, leading, all);
        return all;
    }

    private static void RequireSourceSelection(EudamedDossierKind kind, EudamedDossierContent content, EudamedSource[] sources)
    {
        var primary = EudamedRules.PrimaryKind(kind);
        Guard.Against(sources.Any(s => s.Kind != primary && s.Kind != "eudamedDossier" &&
            !(kind == EudamedDossierKind.FSN && s.Kind == "eudamedDocument")),
            "eudamed_source_kind", "Nur fachlich benoetigte Originalquellen und ausdrueckliche Vorfassungen binden.");
        if (kind == EudamedDossierKind.FSCA)
            Guard.Against(!sources.Where(s => s.Kind == "capa").Select(s => s.Source.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(content.Fsca!.CapaIds.Order(StringComparer.Ordinal)), "eudamed_fsca_sources", "CAPA-Auswahl und Massnahmeninhalt unterscheiden sich.");
        else if (kind != EudamedDossierKind.FSN)
            Guard.Against(sources.Count(s => s.Kind == primary) != 1, "eudamed_source", "Genau eine primaere Dossierquelle erforderlich.");
        var linked = new[] { content.ParentId, content.Fsn?.FscaId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        Guard.Against(!sources.Where(s => s.Kind == "eudamedDossier").Select(s => s.Source.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(linked.Order(StringComparer.Ordinal)), "eudamed_parent_sources", "Jede Vorfassung/FSCA muss ausdruecklich und ausschliesslich gebunden sein.");
        if (kind == EudamedDossierKind.FSN)
            Guard.Against(sources.Count(s => s.Kind == "eudamedDocument") != 1, "eudamed_fsn_document", "Genau ein versioniertes FSN-PDF-Original erforderlich.");
    }

    private async Task<EudamedAttachment[]> PrepareAttachments(EudamedDossierKind kind, string id,
        EudamedDossierContent content, EudamedSource[] sources, Actor actor, CancellationToken ct)
    {
        if (kind is EudamedDossierKind.PSUR or EudamedDossierKind.FSCA)
        {
            var file = await documents.Render(kind, id, content, sources, actor, ct);
            Guard.Against(file.MediaType != "application/pdf" || !file.Bytes.AsSpan().StartsWith("%PDF-"u8),
                "eudamed_rendered_document", "Die Dokumentprojektion hat kein PDF geliefert.", 409);
            var hash = await objects.PutBytes(file.Bytes, ct);
            return [new(file.Name, file.MediaType, hash, sources.First(s => s.Kind == EudamedRules.PrimaryKind(kind)).Source)];
        }
        if (kind != EudamedDossierKind.FSN) return [];
        var source = sources.Single(s => s.Kind == "eudamedDocument");
        var document = Canonical.Read<EudamedDocument>(source.Json);
        Guard.Against(!document.Synthetic || document.MediaType != "application/pdf" ||
            document.OriginalHash != content.Fsn!.DocumentHash || document.Name != content.Fsn.DocumentName,
            "eudamed_fsn_original", "Der FSN-Inhalt bindet nicht genau das aufgenommene synthetische PDF-Original.");
        var original = await objects.ReadBytes(document.OriginalHash, ct);
        Guard.Against(original.Length != document.Length || !original.AsSpan().StartsWith("%PDF-"u8),
            "eudamed_fsn_original", "Gespeichertes PDF stimmt nicht mit seiner Metadatenbindung ueberein.", 409);
        return [new(document.Name, document.MediaType, document.OriginalHash, source.Source)];
    }

    private static void RequireLineage(EudamedPackage package)
    {
        if (package.Content.ParentId is not { } parentId) return;
        var source = package.Sources.SingleOrDefault(s => s.Kind == "eudamedDossier" && s.Source.Id == parentId);
        Guard.Against(source is null || source.Source.Version != package.Content.ParentVersion,
            "eudamed_parent_version", "Konkrete Dossier-Vorfassung fehlt.", 409);
        var parent = Canonical.Read<EudamedDossier>(source!.Json);
        EudamedRules.RequireFrozen(parent);
        Guard.Against(parent.Kind != (package.Kind == EudamedDossierKind.PSRP ? EudamedDossierKind.PSR : package.Kind) ||
            parent.ProductId != package.ProductId || parent.Package.Binding.ProfileHash != package.Binding.ProfileHash,
            "eudamed_parent_scope", "Vorfassung muss denselben fachlichen Typ/Produktscope und das urspruengliche Profil treffen.", 409);
        if (package.Kind != EudamedDossierKind.PSRP)
            Guard.Against(EudamedRules.ObligationKey(package) != EudamedRules.ObligationKey(parent.Package),
                "eudamed_parent_obligation", "Eine Vorfassung darf keine andere fachliche Meldepflicht oder Berichtsperiode umetikettieren.", 409);
        if (package.Content.IntendedAction == "Reply")
            Guard.Against(!parent.Correspondence.Any(c => c.Id == package.Content.ReplyToCorrespondenceId &&
                c.Status == "Open" && c.DossierRevision == parent.Revision),
                "eudamed_correspondence", "Die konkret adressierte offene Originalkorrespondenz fehlt.", 409);
    }

    private void RequireProjection(EudamedPackage package)
    {
        var current = composer.Compose(package.Kind, package.DossierId, package.Content, package.Binding,
            package.References, package.Calendar, package.Sources, package.Attachments);
        Guard.Against(current.Xml != package.BusinessXml || Canonical.Hash(current.Validation) != Canonical.Hash(package.Validation),
            "eudamed_projection_changed", "Quell-, Schema-, Enum- oder Mappingprojektion hat sich geaendert; neuer Entwurf und neue Freigaben erforderlich.", 409);
    }

    private async Task<EudamedChannel> CurrentChannel(EudamedBinding binding, string product, CancellationToken ct)
    {
        var channel = await store.Get<EudamedChannel>("eudamedChannel", binding.ChannelId, ct)
            ?? throw new DomainException("eudamed_channel_missing", "Gebundener Kanal fehlt.", 409);
        Guard.Against(channel.ProductId != product, "eudamed_channel_scope", "Gebundener Produktscope stimmt nicht.", 403);
        EudamedContracts.RequireCurrentChannel(binding, channel);
        var readiness = await Readiness(channel.Profile, product, null, ct);
        Guard.Against(!readiness.CanDispatch, "eudamed_not_ready", string.Join(" ", readiness.Blockers), 409);
        return channel;
    }

    private static List<StoreDependency> PackageDependencies(EudamedPackage package, EudamedChannel channel)
    {
        List<StoreDependency> result =
        [
            new("eudamedChannel", EudamedContracts.Bind(channel)),
            new("eudamedActor", package.Binding.ActorSource),
            new("eudamedReferences", package.Binding.ReferencesSource),
            new("productGovernance", package.References.GovernanceSource),
            .. package.Sources.Select(s => new StoreDependency(s.Kind, s.Source))
        ];
        foreach (var source in package.Sources.Where(s => s.Kind == "eudamedReferences"))
        {
            var reference = Canonical.Read<EudamedReferences>(source.Json);
            result.Add(new("productGovernance", reference.GovernanceSource));
            result.Add(new("eudamedActor", reference.ActorApplication));
        }
        return result;
    }

    private static string LedgerId(EudamedPackage package) => "eudamed-delivery-" + EudamedRules.ObligationKey(package);
    private static StoreDependency LedgerDependency(EudamedDeliveryLedger ledger) =>
        new("eudamedDelivery", new(ledger.Id, ledger.Version, Canonical.Hash(ledger)));

    private async Task<EudamedDeliveryLedger> GetLedger(EudamedPackage package, CancellationToken ct) =>
        await store.Get<EudamedDeliveryLedger>("eudamedDelivery", LedgerId(package), ct)
        ?? throw new DomainException("eudamed_ledger_missing", "Gemeinsame versionierte Meldepflicht-Reservierung fehlt.", 409);

    private async Task<EudamedDeliveryLedger> EnsureLedger(EudamedPackage package, Actor actor, CancellationToken ct)
    {
        var id = LedgerId(package);
        var current = await store.Get<EudamedDeliveryLedger>("eudamedDelivery", id, ct);
        if (current is not null) return current;
        var obligation = EudamedRules.ObligationKey(package);
        var context = Context(id, "eudamedDelivery", package.ProductId, 0, "eudamed-ledger:" + actor.Id + ":" + obligation,
            "EudamedDeliveryLedgerCreated", "Leere fachliche Reservierung; noch keine Meldung oder Transportannahme.",
            actor, new { package.ProductId, obligation });
        try
        {
            return await store.Execute<EudamedDeliveryLedger>(context,
                _ => new(id, 1, package.ProductId, obligation, []), ct);
        }
        catch (DomainException error) when (error.Code == "version_conflict")
        {
            return await GetLedger(package, ct);
        }
    }

    private static void RequireNoPending(EudamedDeliveryLedger ledger) =>
        Guard.Against(ledger.Entries.Any(EudamedRules.Pending), "eudamed_delivery_pending",
            "Eine gebundene Meldung ist noch unterwegs oder unklar. Erst urspruenglichen Versuch abgleichen; kein neuer Versand.", 409);

    private async Task<EudamedInbox[]> VisibleInbox(string product, Actor actor, CancellationToken ct)
    {
        var dossiers = (await store.List<EudamedDossier>("eudamedDossier", [product], ct))
            .Where(d => EudamedRules.CanRead(d, actor)).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        return (await store.List<EudamedInbox>("eudamedInbox", [product], ct))
            .Where(i => i.Original is not null && dossiers.Contains(i.Original.DossierId)).ToArray();
    }

    private static EudamedDossierSummary Summarize(EudamedDossier value) => new(value.Id, value.Version,
        value.ProductId, value.Kind, value.Revision, value.Package.Content.Title, value.ReviewStatus, value.ScopeHash,
        value.Package.Binding, value.Approvals, value.Attempts, value.BusinessResponses,
        value.Correspondence.Count(c => c.Status == "Open"), value.Package.Validation);
}
