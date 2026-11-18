using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class EudamedService(IStateStore store, IObjectStore objects, PolicyCatalog policies,
    IDomibusGateway gateway, IEudamedComposer composer, IEudamedDocumentRenderer documents, TimeProvider clock)
{
    private static void Reader(Actor actor) =>
        actor.Require(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);

    public async Task<EudamedWorkspace> Workspace(string product, Actor actor, CancellationToken ct = default)
    {
        Reader(actor);
        actor.RequireProduct(product);
        _ = policies.Device(product);
        var channel = await store.Get<EudamedChannel>("eudamedChannel", EudamedContracts.ChannelId(product), ct);
        var draft = await store.Get<EudamedConfigurationDraft>("eudamedConfiguration", EudamedContracts.DraftId(product), ct);
        var connection = await store.Get<DomibusConnectionCheck>("domibusCheck", "domibus-check-" + product, ct);
        var activeConnection = connection?.DraftSource == channel?.DraftSource ? connection : null;
        var draftConnection = draft is not null && connection?.DraftSource == EudamedContracts.Bind(draft) ? connection : null;
        var activeReadiness = channel is null ? new EudamedReadiness("NotConfigured", "NotRequired", "Blocked",
            false, false, ["Sichere Simulator-Voreinstellung; Actor und Referenzen zuordnen und das Profil ausdruecklich aktivieren."], "None")
            : await Readiness(channel.Profile, product, activeConnection, ct);
        var draftReadiness = draft is null ? null :
            channel?.Profile == draft.Profile && activeConnection == draftConnection ? activeReadiness :
            await Readiness(draft.Profile, product, draftConnection, ct);
        if (channel is { Enabled: false }) activeReadiness = activeReadiness with
        { CanActivate = false, CanDispatch = false, BusinessStatus = "Disabled", Blockers = [.. activeReadiness.Blockers, "Kanal ausdruecklich deaktiviert."] };
        var dossiers = (await store.List<EudamedDossier>("eudamedDossier", [product], ct))
            .Where(d => EudamedRules.CanRead(d, actor)).ToArray();
        return new(channel?.Profile.Mode ?? EudamedMode.Simulator, channel is not null, channel, draft, activeReadiness,
            draftReadiness, connection,
            await store.List<EudamedActorApplication>("eudamedActor", actor.Products, ct),
            await store.List<EudamedReferences>("eudamedReferences", [product], ct),
            dossiers.Select(Summarize).ToArray(),
            await VisibleInbox(product, dossiers, ct),
            EudamedContracts.Capabilities, gateway.ReferenceCatalog,
            new(EudamedContracts.Business, EudamedContracts.Gateway, EudamedContracts.WsdlSha256, EudamedContracts.SchemaRuntime),
            "Simulation ist kein Playground-/Behoerdennachweis. Bestehende Fall-, Nachforderungs- und PSUR-Simulatoren bleiben Legacy-Simulation; kein Moduswechsel leitet sie um.");
    }

    public async Task<EudamedActorApplication> SaveActor(string? id, SaveEudamedActorCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        _ = policies.Device(input.ProductId);
        EudamedContracts.RequireActorDetails(input.Details ?? throw new DomainException("eudamed_actor_details", "Antragsinhalt fehlt."));
        Guard.Text(input.Reason, "Antrags-/Korrekturbegruendung", 30, 4000);
        if (id is not null)
        {
            var existing = await GetActor(id, actor, ct);
            Guard.Against(existing.ProductId != input.ProductId, "scope_conflict", "Bestehender Actor-Antrag hat einen anderen Produktscope.", 403);
        }
        id ??= "eudamed-actor-" + Canonical.Hash(new { actor.Id, key, input.ProductId })[..32];
        var command = Context(id, "eudamedActor", input.ProductId, input.ExpectedVersion, key,
            "EudamedSyntheticActorDrafted", input.Reason, actor, input);
        return await store.Execute<EudamedActorApplication>(command, current =>
        {
            Guard.Against(current?.Status == "Submitted", "eudamed_actor_pending",
                "Der eingereichte Antrag muss zuerst eine zugeordnete Rueckmeldung erhalten.", 409);
            return new(id, input.ExpectedVersion + 1, input.ProductId, input.Details, "Draft", null,
                current?.Rounds ?? [], EudamedContracts.SimulationOrigin, current?.CreatedAt ?? clock.GetUtcNow());
        }, ct);
    }

    public async Task<EudamedActorApplication> SubmitActor(string id, SubmitEudamedActorCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var value = await GetActor(id, actor, ct);
        Guard.Against(input.Scenario is not ("Approve" or "Correction" or "Reject"), "eudamed_actor_scenario",
            "Explizites lokales Registrierungsszenario erforderlich.");
        Guard.Text(input.Reason, "Antragseinreichung", 30, 4000);
        return await store.Execute<EudamedActorApplication>(Context(id, "eudamedActor", value.ProductId,
            input.ExpectedVersion, key, "EudamedActorSubmitted", input.Reason, actor, input), current =>
        {
            var application = current ?? throw new DomainException("eudamed_actor_missing", "Testantrag fehlt.", 404);
            Guard.Against(application.Status != "Draft", "eudamed_actor_state", "Zuerst einen aktuellen Antrag oder eine begruendete Korrektur erstellen.", 409);
            EudamedContracts.RequireActorDetails(application.Details);
            var round = new EudamedActorRound(application.Version + 1, "Submitted", input.Scenario,
                actor.Id, input.Reason, clock.GetUtcNow(), Canonical.Hash(application.Details), null);
            return application with { Version = application.Version + 1, Status = "Submitted", Rounds = [.. application.Rounds, round] };
        }, ct);
    }

    public async Task<EudamedActorApplication> ProcessActor(string id, int submittedVersion, CancellationToken ct = default)
    {
        var value = await store.Get<EudamedActorApplication>("eudamedActor", id, ct)
            ?? throw new DomainException("eudamed_actor_missing", "Gebundener Testantrag fehlt.", 404);
        var actor = Worker(value.ProductId, simulator: true);
        var context = Context(id, "eudamedActor", value.ProductId, submittedVersion,
            $"actor-response:{id}:{submittedVersion}", "EudamedActorSimulationResponded",
            "Lokale Gegenstelle beantwortet nur die gebundene synthetische Antragsrevision.", actor, new { id, submittedVersion });
        if (await store.Receipt<EudamedActorApplication>(context, ct) is { } replay) return replay;
        return await store.Execute<EudamedActorApplication>(context, current =>
        {
            var application = current ?? throw new DomainException("eudamed_actor_missing", "Antrag fehlt.", 404);
            var submitted = application.Rounds.LastOrDefault();
            Guard.Against(application.Status != "Submitted" || submitted is null ||
                submitted.Revision != submittedVersion || submitted.ApplicationHash != Canonical.Hash(application.Details),
                "eudamed_actor_binding", "Rueckmeldung passt nicht zur eingereichten Antragsrevision.", 409);
            var state = submitted!.Scenario switch { "Approve" => "SimulatedApproved", "Correction" => "CorrectionRequired", _ => "Rejected" };
            var identity = state == "SimulatedApproved" ? "DEMO-EUDAMED-" + application.Details.Role + "-" + id["eudamed-actor-".Length..] : null;
            var reason = submitted.Scenario switch
            {
                "Approve" => "SYNTHETISCH: Der Test-Actor ist ausschliesslich fuer den lokalen Simulator freigeschaltet. Keine amtliche SRN oder Modulberechtigung.",
                "Correction" => "SYNTHETISCH: Die lokale Validierungsstelle verlangt eine nachvollziehbare Praezisierung der Organisations-/Kontaktzuordnung und einen neuen Antrag.",
                _ => "SYNTHETISCH: Der gewaehlte negative Testfall lehnt diese Antragsrevision ab. Die Historie bleibt erhalten; Korrektur und erneute Einreichung sind moeglich."
            };
            var decision = new EudamedActorRound(submittedVersion, state, submitted.Scenario,
                "DEMO actor validating authority", reason, clock.GetUtcNow(), submitted.ApplicationHash, identity);
            return application with { Version = application.Version + 1, Status = state, SimulatedIdentity = identity,
                Rounds = [.. application.Rounds, decision] };
        }, ct);
    }

    public async Task<EudamedReferences> MapReferences(MapEudamedReferencesCommand input, Actor actor,
        string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        var application = await GetActor(input.ActorId, actor, ct);
        EudamedContracts.RequireRegistered(application);
        Guard.Against(application.Details.Role != "MF", "eudamed_manufacturer_actor",
            "Eine Herstellerzuordnung braucht einen MF-Testactor; eine AR-Testidentitaet ist kein Herstellerersatz.");
        Guard.Against(application.Version != input.ActorVersion,
            "eudamed_reference_actor", "Die ausgewaehlte aktuelle Testidentitaet muss ausdruecklich gebunden sein.", 409);
        var governance = await store.Get<ProductGovernance>("productGovernance", input.ProductId, ct)
            ?? throw new DomainException("eudamed_governance", "Versioniertes Hersteller-/Geraeteprofil fehlt.", 409);
        GovernanceRules.RequireValid(governance, policies.Device(input.ProductId));
        Guard.Against(governance.Version != input.GovernanceVersion, "version_conflict", "Geraeteprofil wurde geaendert.", 409);
        Guard.Against(input.ValidThrough < input.ValidFrom, "eudamed_certificate_period", "Zertifikatszeitraum ist ungueltig.");
        Guard.Text(input.CertificateRevision, "Synthetische Zertifikatsrevision", 1, 80);
        Guard.Text(input.EvaluatingNotifiedBody, "Ausdruecklich ausgewaehlte evaluierende Test-NB", 8, 200);
        Guard.Text(input.Reason, "Explizite Actor-/Basic-UDI-/Zertifikatszuordnung", 50, 4000);
        Guard.Against(input.UdiIssuingAgency is not ("GS1" or "HIBCC" or "ICCBBA" or "IFA" or "EUDAMED"),
            "eudamed_udi_agency", "Ausdrueckliche issuing agency der synthetischen UDI-Zuordnung erforderlich.");
        Guard.Text(input.CertificateText, "Synthetischer Zertifikatsnachweis", 80, 32000);
        Guard.Against(!input.CertificateText.StartsWith("SYNTHETISCH:", StringComparison.Ordinal),
            "eudamed_certificate_origin", "Hier nur einen ausdruecklich synthetischen Textnachweis erfassen; kein echter Zertifikatsimport wird behauptet.");
        var id = "eudamed-references-" + input.ProductId;
        var command = Context(id, "eudamedReferences", input.ProductId, input.ExpectedVersion, key,
            "EudamedSyntheticReferencesMapped", input.Reason, actor, input);
        if (await store.Receipt<EudamedReferences>(command, ct) is { } replay) return replay;
        var hash = await objects.Put(input.CertificateText, ct);
        var certificate = new EudamedCertificate(governance.Device.CertificateId, input.CertificateRevision,
            governance.Device.NotifiedBodyName + " / " + governance.Device.NotifiedBodyNumber,
            governance.Device.BasicUdi, input.ValidFrom, input.ValidThrough, hash,
            "Explicit synthetic text; no CA/NB API or real certificate verification", "SyntheticMappingConfirmed", true);
        var governanceSource = new VersionReference(governance.Id, governance.Version, Canonical.Hash(governance));
        return await store.Execute<EudamedReferences>(command with { Dependencies =
            [new("eudamedActor", EudamedContracts.Bind(application)), new("productGovernance", governanceSource)] }, _ =>
            new(id, input.ExpectedVersion + 1, input.ProductId, EudamedContracts.Bind(application),
                application.Details, application.SimulatedIdentity!, policies.Device(input.ProductId), governanceSource,
                governance.Device, certificate, input.EvaluatingNotifiedBody, input.Reason, actor.Id, clock.GetUtcNow(), true,
                input.UdiIssuingAgency), ct);
    }

    public async Task<EudamedConfigurationDraft> SaveConfiguration(SaveEudamedConfigurationCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(input.ProductId);
        _ = policies.Device(input.ProductId);
        Guard.Text(input.Reason, "Konfigurationsbegruendung", 30, 4000);
        var profile = input.Profile ?? throw new DomainException("eudamed_profile", "Konfigurationsinhalt fehlt.");
        Guard.Against(!Enum.IsDefined(profile.Mode) || !Enum.IsDefined(profile.Environment),
            "eudamed_mode", "Unbekannte Betriebsart oder Umgebung.");
        Guard.Against(input.Profile.Mode == EudamedMode.Simulator &&
            (input.Profile.Domibus is not null || input.Profile.Environment != EudamedEnvironment.Simulation),
            "eudamed_simulator_network", "Simulatorprofil darf keine externen Endpunkte/Secrets oder reale Umgebung enthalten.");
        if (input.Profile.Domibus is { } domibus)
        {
            RequireSafeReferences(domibus);
            var catalog = gateway.ReferenceCatalog;
            Guard.Against(domibus.EndpointReference.Length > 0 && !catalog.Endpoints.Contains(domibus.EndpointReference, StringComparer.Ordinal) ||
                (domibus.SecretReference.Length > 0 || domibus.SecretVersion.Length > 0) &&
                    !catalog.Credentials.Contains(new(domibus.SecretReference, domibus.SecretVersion)) ||
                (domibus.ModuleKeyReference.Length > 0 || domibus.ModuleKeyVersion.Length > 0) &&
                    !catalog.ModuleKeys.Contains(new(domibus.ModuleKeyReference, domibus.ModuleKeyVersion)) ||
                domibus.OnboardingEvidenceReference.Length > 0 && !catalog.OnboardingEvidence.Contains(domibus.OnboardingEvidenceReference, StringComparer.Ordinal),
                "domibus_unknown_reference", "Nur betreiberseitig registrierte Referenzen auswaehlen; keine frei eingegebenen Geheimnisse oder Endpunkte speichern.");
        }
        var id = EudamedContracts.DraftId(input.ProductId);
        return await store.Execute<EudamedConfigurationDraft>(Context(id, "eudamedConfiguration", input.ProductId,
            input.ExpectedVersion, key, "EudamedConfigurationDrafted", input.Reason, actor, input),
            _ => new(id, input.ExpectedVersion + 1, input.ProductId, input.Profile, actor.Id, clock.GetUtcNow()), ct);
    }

    public async Task<EudamedChannel> Activate(string product, ActivateEudamedConfigurationCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(product);
        Guard.Against(!input.ConfirmImpact, "eudamed_impact_confirmation", "Profilwechsel und Sperre neuer Claims auf alten Generationen ausdruecklich bestaetigen.");
        Guard.Text(input.Reason, "Begruendete Kanalaktivierung", 50, 4000);
        var context = Context(EudamedContracts.ChannelId(product), "eudamedChannel", product, input.ExpectedVersion,
            key, "EudamedChannelActivated", input.Reason, actor, input);
        if (await store.Receipt<EudamedChannel>(context, ct) is { } replay) return replay;
        var draft = await store.Get<EudamedConfigurationDraft>("eudamedConfiguration", EudamedContracts.DraftId(product), ct)
            ?? throw new DomainException("eudamed_configuration_missing", "Gespeicherter Entwurf fehlt.", 409);
        Guard.Against(draft.Version != input.DraftVersion, "version_conflict", "Konfigurationsentwurf hat sich geaendert.", 409);
        var readiness = await Readiness(draft.Profile, product, null, ct);
        Guard.Against(!readiness.CanActivate, "eudamed_not_ready", string.Join(" ", readiness.Blockers), 409);
        var application = await GetActor(draft.Profile.ActorId, actor, ct);
        var references = await GetReferences(draft.Profile.ReferencesId, actor, ct);
        return await store.Execute<EudamedChannel>(context with { Dependencies =
            [new("eudamedConfiguration", EudamedContracts.Bind(draft)), new("eudamedActor", EudamedContracts.Bind(application)),
                new("eudamedReferences", EudamedContracts.Bind(references)), new("productGovernance", references.GovernanceSource)] }, current =>
            new(context.Id, input.ExpectedVersion + 1, product, (current?.DispatchGeneration ?? 0) + 1,
                draft.Profile, EudamedContracts.Bind(draft), actor.Id, clock.GetUtcNow(), true), ct);
    }

    public Task<EudamedChannel> Deactivate(string product, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(product);
        Guard.Text(input.Reason, "Begruendete Deaktivierung", 50, 4000);
        return store.Execute<EudamedChannel>(Context(EudamedContracts.ChannelId(product), "eudamedChannel", product,
            input.ExpectedVersion, key, "EudamedChannelDeactivated", input.Reason, actor, input), current =>
        {
            var value = current ?? throw new DomainException("eudamed_configuration_missing", "Aktiver Kanal fehlt.", 409);
            return value with { Version = value.Version + 1, DispatchGeneration = value.DispatchGeneration + 1,
                Enabled = false, ActivatedAt = clock.GetUtcNow(), ActivatedBy = actor.Id };
        }, ct);
    }

    public async Task<DomibusConnectionCheck> CheckConnection(string product, CheckDomibusCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(product);
        Guard.Text(input.Reason, "Ausdrueckliche Backend-Verbindungsdiagnose", 30, 4000);
        var draft = await store.Get<EudamedConfigurationDraft>("eudamedConfiguration", EudamedContracts.DraftId(product), ct)
            ?? throw new DomainException("eudamed_configuration_missing", "Entwurf fehlt.", 409);
        Guard.Against(draft.Version != input.DraftVersion, "version_conflict", "Entwurf wurde geaendert.", 409);
        Guard.Against(draft.Profile.Mode != EudamedMode.Domibus || draft.Profile.Domibus is null,
            "domibus_explicit_only", "Keine Netzwerkdiagnose aus einem Simulatorprofil.");
        var id = "domibus-check-" + product;
        var command = Context(id, "domibusCheck", product, input.ExpectedVersion, key,
            "DomibusConnectionCheckRequested", input.Reason, actor, input);
        if (await store.Receipt<DomibusConnectionCheck>(command, ct) is { } replay) return replay;
        return await store.Execute<DomibusConnectionCheck>(command with {
            Dependencies = [new("eudamedConfiguration", EudamedContracts.Bind(draft))] }, _ =>
            new(id, command.ExpectedVersion + 1, product, EudamedContracts.Bind(draft), draft.Profile.Domibus!, "Pending", null,
                clock.GetUtcNow(), actor.Id, "NotExecuted"), ct);
    }

    public async Task<DomibusConnectionCheck> ProcessConnectionCheck(string id, int version, CancellationToken ct = default)
    {
        var pending = await store.Get<DomibusConnectionCheck>("domibusCheck", id, ct)
            ?? throw new DomainException("domibus_check_missing", "Diagnoseauftrag fehlt.", 404);
        var context = Context(pending.Id, "domibusCheck", pending.ProductId, version,
            "connection-result:" + pending.Id + ":" + version, "DomibusConnectionCheckCompleted",
            "Backend-Erreichbarkeit ist getrennt von Actor-/AP-Onboarding und fachlicher Servicefreigabe.",
            Worker(pending.ProductId, simulator: false), new { id, version });
        if (await store.Receipt<DomibusConnectionCheck>(context, ct) is { } replay) return replay;
        Guard.Against(pending.Version != version || pending.Status != "Pending", "domibus_check_stale", "Diagnoseauftrag ist ueberholt.", 409);
        var draft = await store.Get<EudamedConfigurationDraft>("eudamedConfiguration", pending.DraftSource.Id, ct);
        string? error = null;
        var availability = gateway.Availability(pending.Profile);
        if (draft is null || EudamedContracts.Bind(draft) != pending.DraftSource) error = "domibus_configuration_changed";
        else if (!availability.Configured) error = "domibus_configuration_missing";
        else
        {
            try { await gateway.Check(pending.Profile, ct); }
            catch (DomibusException exception) { error = exception.Code; }
        }
        return await store.Execute<DomibusConnectionCheck>(context, current => current! with
            { Version = current.Version + 1, Status = error is null ? "Reachable" : "Failed", ErrorCode = error,
                CheckedAt = clock.GetUtcNow(), EvidenceLevel = error is null ? availability.EvidenceLevel : "NotConfirmed" }, ct);
    }

    private async Task<EudamedReadiness> Readiness(EudamedProfile profile, string product,
        DomibusConnectionCheck? connection, CancellationToken ct)
    {
        var blockers = EudamedContracts.ProfileErrors(profile).ToList();
        var actor = string.IsNullOrWhiteSpace(profile.ActorId) ? null : await store.Get<EudamedActorApplication>("eudamedActor", profile.ActorId, ct);
        var references = string.IsNullOrWhiteSpace(profile.ReferencesId) ? null : await store.Get<EudamedReferences>("eudamedReferences", profile.ReferencesId, ct);
        if (profile.Mode == EudamedMode.Simulator)
        {
            if (actor is null || actor.Version != profile.ActorVersion ||
                actor.Status != "SimulatedApproved" || actor.Origin != EudamedContracts.SimulationOrigin)
                blockers.Add("Aktuelle freigeschaltete synthetische Actor-Version fehlt.");
            if (references is null || references.ProductId != product || references.Version != profile.ReferencesVersion ||
                actor is null || references.ActorApplication != EudamedContracts.Bind(actor) || !references.Synthetic)
                blockers.Add("Bestaetigte Referenzversion passt nicht zum aktuellen Actor und Produkt.");
            if (references is not null)
            {
                var governance = await store.Get<ProductGovernance>("productGovernance", references.GovernanceSource.Id, ct);
                if (governance is null || new VersionReference(governance.Id, governance.Version, Canonical.Hash(governance)) != references.GovernanceSource)
                    blockers.Add("Das zugeordnete Produkt-/Herstellerprofil wurde geaendert; Referenzen erneut ausdruecklich bestaetigen.");
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                if (references.Certificate.ValidFrom > today || references.Certificate.ValidThrough < today)
                    blockers.Add("Synthetischer Zertifikatszeitraum deckt den aktuellen UTC-Prueftag nicht ab.");
            }
            return new(blockers.Count == 0 ? "Valid" : "Incomplete", "NotRequired", "SimulatorOnly",
                blockers.Count == 0, blockers.Count == 0, blockers.ToArray(), "LocalSimulation");
        }
        if (actor is not null || references?.Synthetic == true)
            blockers.Add("Simulierte Actor-/Zertifikatsidentitaeten duerfen nicht in einen realen Kanal uebernommen werden.");
        blockers.Add("Extern bestaetigtes EUDAMED-Actor-/AP-Onboarding und konkrete Modul-/Rollenfreigabe fehlen; keine Registrierung ueber AS4.");
        blockers.Add("Fachliche EUDAMED-Service-/Mapping-End-to-End-Abnahme ist nicht erteilt; Backend-Erreichbarkeit reicht nicht.");
        if (profile.Environment == EudamedEnvironment.Production)
            blockers.Add("Produktive Vigilanzoperationen sind nicht aktiviert.");
        if (profile.Domibus is { } domibus) blockers.AddRange(gateway.Availability(domibus).Blockers);
        return new("DraftOnly", connection?.Status ?? "NotChecked", "Blocked", false, false,
            blockers.Distinct(StringComparer.Ordinal).ToArray(), connection?.EvidenceLevel ?? "None");
    }

    private async Task<EudamedActorApplication> GetActor(string id, Actor actor, CancellationToken ct)
    {
        var value = await store.Get<EudamedActorApplication>("eudamedActor", id, ct)
            ?? throw new DomainException("eudamed_actor_missing", "Actor-Antrag fehlt.", 404);
        actor.RequireProduct(value.ProductId);
        return value;
    }

    public async Task<AuditEvent[]> IntegrationAudit(string kind, string id, Actor actor, CancellationToken ct = default)
    {
        Reader(actor);
        Guard.Against(kind is not ("eudamedActor" or "eudamedReferences" or "eudamedConfiguration" or "eudamedChannel" or
            "eudamedDossier" or "eudamedDelivery" or "eudamedInbox" or "eudamedDocument" or "domibusCheck"),
            "eudamed_audit_kind", "Nur ein konkreter versionierter Integrationsvorgang ist hier zugelassen.");
        var value = await store.Get<System.Text.Json.JsonElement>(kind, id, ct);
        Guard.Against(value.ValueKind != System.Text.Json.JsonValueKind.Object, "eudamed_audit_missing", "Vorgang fehlt.", 404);
        actor.RequireProduct(value.GetProperty("productId").GetString()!);
        if (kind == "eudamedDossier")
        {
            var dossier = Canonical.Read<EudamedDossier>(value.GetRawText());
            EudamedRules.RequireActorScope(dossier, actor);
            await RequireFsnPublicationAccess(dossier, actor, ct);
        }
        if (kind == "eudamedDelivery")
            foreach (var entry in Canonical.Read<EudamedDeliveryLedger>(value.GetRawText()).Entries)
                EudamedRules.RequireActorScope(await ApprovedDossier(entry, ct), actor);
        if (kind == "eudamedInbox" && Canonical.Read<EudamedInbox>(value.GetRawText()).Original is { } original)
            _ = await GetDossier(original.DossierId, actor, ct);
        var audit = await store.Audit(id, ct);
        if (kind == "eudamedDossier")
            foreach (var entry in audit.Where(a => a.Action is "FsnPublicationDrafted" or "FsnPublicationApproved" or "FsnPublicationSimulated"))
            {
                var revision = await store.GetVersion<EudamedDossier>(kind, id, entry.Version, ct)
                    ?? throw new DomainException("fsn_audit_version", "Originalfassung der gesonderten Veroeffentlichungsentscheidung fehlt.", 409);
                EudamedRules.RequireActorScope(revision, actor);
                await RequireFsnPublicationAccess(revision, actor, ct);
            }
        return audit;
    }

    private async Task<EudamedReferences> GetReferences(string id, Actor actor, CancellationToken ct)
    {
        var value = await store.Get<EudamedReferences>("eudamedReferences", id, ct)
            ?? throw new DomainException("eudamed_references_missing", "Referenzzuordnung fehlt.", 404);
        actor.RequireProduct(value.ProductId);
        return value;
    }

    private CommandContext Context(string id, string kind, string product, int version, string key,
        string action, string reason, Actor actor, object request) =>
        new(id, kind, product, version, key, action, reason, actor, request, clock.GetUtcNow());

    private static Actor Worker(string product, bool simulator) =>
        new(simulator ? "eudamed-simulator" : "domibus-receiver", simulator ? "Cohors local simulator" : "Domibus backend worker",
            [Roles.Workflow], [product], false);

    private static void RequireSafeReferences(DomibusProfile profile)
    {
        foreach (var value in new[] { profile.EndpointReference, profile.SecretReference, profile.SecretVersion,
            profile.ModuleKeyReference, profile.ModuleKeyVersion, profile.OnboardingEvidenceReference })
            Guard.Against(value is null || value.Length > 120 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')),
                "domibus_reference", "Nur kurze Referenzkennungen, keine URLs, Geheimniswerte oder EU-Login-Zugangsdaten erfassen.");
        foreach (var value in new[] { profile.Domain, profile.AccessPointPartyId, profile.ReceiverPartyId,
            profile.PartyIdType, profile.FromRole, profile.ToRole, profile.Service, profile.ServiceType,
            profile.Action, profile.OriginalSender, profile.FinalRecipient })
            Guard.Against(value is null || value.Length > 200 || value.Any(char.IsControl),
                "domibus_profile_field", "Nur begrenzte nicht geheime Profilkennungen ohne Steuerzeichen speichern.");
    }
}
