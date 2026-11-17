using System.Xml.Linq;
using System.Text.Json;
using Cohors.Domain;
using Cohors.Infrastructure;
using Xunit.Abstractions;

namespace Cohors.Tests;

public sealed class EudamedCompositionTests(ITestOutputHelper output)
{
    private static readonly XNamespace Base = "https://ec.europa.eu/tools/eudamed/dtx/datamodel/Entity/Vigilance";
    private static readonly XNamespace V1 = Base.NamespaceName + "/v1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-29T22:30:00Z");
    private const string Reason = "SYNTHETISCH: Die Originalquelle und medizinisch-technische Einordnung bleiben versioniert nachvollziehbar. Unsicherheit wird ausgewiesen; keine autonome Risikoannahme und keine amtliche Uebermittlung.";
    private readonly EudamedComposer composer = new();

    [Theory]
    [InlineData(EudamedDossierKind.MIR)]
    [InlineData(EudamedDossierKind.MTR)]
    [InlineData(EudamedDossierKind.PSR)]
    [InlineData(EudamedDossierKind.PSRP)]
    [InlineData(EudamedDossierKind.PSUR)]
    [InlineData(EudamedDossierKind.FSCA)]
    [InlineData(EudamedDossierKind.FSN)]
    public void DossierProjectionUsesPinnedTypesAndRealFieldStructure(EudamedDossierKind kind)
    {
        var fixture = Create(kind);
        var before = Canonical.Hash(fixture.Sources);
        var result = Compose(fixture);
        Assert.True(result.Validation.Errors.Length == 0, string.Join("\n", result.Validation.Errors));
        var root = XDocument.Parse(result.Xml).Root!;
        Assert.Equal(fixture.References.ActorIdentity, root.Element(Base + "Fields")!.Element(V1 + "mf_srn")!.Value);
        Assert.Equal(before, Canonical.Hash(fixture.Sources));
        Assert.DoesNotContain(root.Descendants(), e => e.Name.LocalName is "SubmissionDate" or "DossierVersion" or "psrp_period_start" or "psrp_period_end");
        Assert.Equal(64, result.Validation.SchemaFingerprint.Length);
        Assert.Equal(64, result.Validation.EnumerationFingerprint.Length);
        Assert.Equal(64, result.Validation.RuntimeSchemaFingerprint.Length);
        if (kind is EudamedDossierKind.FSCA or EudamedDossierKind.FSN) ContractCase(kind, result.Xml, true);
        if (kind == EudamedDossierKind.PSUR)
            Assert.Contains(root.Descendants(Base + "FileURI"), e => e.Value.Contains(fixture.Attachments[0].Hash, StringComparison.Ordinal));
        if (kind == EudamedDossierKind.MTR)
            Assert.Contains("approved exposure=1000 Implants", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicEnumerationValuesAndOutputOnlyFieldsAreCheckedBeyondXsdStrings()
    {
        var xml = XDocument.Parse(Compose(Create(EudamedDossierKind.MTR)).Xml);
        xml.Descendants(V1 + "trendReportBasis").Single().Value = "INVENTED_ENUM_VALUE";
        Assert.Contains(composer.Validate(xml.ToString(), EudamedDossierKind.MTR), e => e.Contains("Katalog", StringComparison.Ordinal));
        xml.Descendants(V1 + "trendReportBasis").Single().Value = "INCIDENT";
        xml.Root!.Element(Base + "DossierExtId")!.AddAfterSelf(new XElement(Base + "DossierVersion", 17));
        Assert.Contains(composer.Validate(xml.ToString(), EudamedDossierKind.MTR), e => e.Contains("Output-only", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeXmlWrongKindAndExtraPayloadAreRejectedWithoutExternalResolution()
    {
        var xml = Compose(Create(EudamedDossierKind.MTR)).Xml;
        Assert.NotEmpty(composer.Validate("<!DOCTYPE Dossier [<!ENTITY x SYSTEM 'https://example.invalid/never-fetch'>]>" + xml, EudamedDossierKind.MTR));
        Assert.NotEmpty(composer.Validate(xml, EudamedDossierKind.MIR));
        var duplicate = XDocument.Parse(xml);
        duplicate.Root!.Element(Base + "Data")!.Add(new XElement(V1 + "mtr"));
        Assert.NotEmpty(composer.Validate(duplicate.ToString(), EudamedDossierKind.MTR));
    }

    [Fact]
    public void MirUsesFirstAwarenessInBoundCalendarAndNeverSystemArrival()
    {
        var fixture = Create(EudamedDossierKind.MIR);
        var result = Compose(fixture);
        var xml = XDocument.Parse(result.Xml);
        Assert.Equal("2026-03-30", xml.Descendants(V1 + "mfAwarenessDate").Single().Value);
        Assert.DoesNotContain("2026-04-02", result.Xml, StringComparison.Ordinal);
        Assert.Equal("ALL_OTHER", xml.Descendants(V1 + "eventClassification").Single().Value);
    }

    [Theory]
    [InlineData(EudamedDossierKind.MIR)]
    [InlineData(EudamedDossierKind.MTR)]
    public void LegacyFlagDoesNotInventMddOrMdrLegislation(EudamedDossierKind kind)
    {
        var fixture = Create(kind);
        var references = fixture.References with { RegulatoryDevice = fixture.References.RegulatoryDevice with { Legacy = true } };
        fixture = fixture with { References = references,
            Binding = fixture.Binding with { ReferencesSource = EudamedContracts.Bind(references) } };
        Assert.Equal("eudamed_unsupported_device_regime", Assert.Throws<DomainException>(() => Compose(fixture)).Code);
    }

    [Fact]
    public void PsrNeedsExplicitCalendarTermsAndDoesNotConvertLegacyDays()
    {
        var fixture = Create(EudamedDossierKind.PSR);
        var xml = XDocument.Parse(Compose(fixture).Xml);
        Assert.Equal("3", xml.Descendants(V1 + "frequencyOfReporting").Single().Value);
        Assert.Equal(59, EudamedRules.Source<SummaryAgreement>(fixture.Sources, "summaryAgreement").FrequencyDays);
        Assert.Equal("eudamed_psr_terms", Assert.Throws<DomainException>(() =>
            Compose(fixture with { Content = fixture.Content with { PsrTerms = null } })).Code);
    }

    [Fact]
    public void PsrpReplySeparatesItsCorrespondenceTargetFromTheCapturedPsrAgreement()
    {
        var fixture = Create(EudamedDossierKind.PSRP);
        var parent = Parent(EudamedDossierKind.PSRP, fixture);
        var reply = fixture with
        {
            Content = fixture.Content with { IntendedAction = "Reply", ParentId = parent.Id,
                ParentVersion = parent.Version, ReplyToCorrespondenceId = "original-correspondence" },
            Sources = [fixture.Sources.Single(s => s.Kind == "summaryReport"),
                Source("eudamedDossier", parent.Id, parent.Version, parent)]
        };
        var originals = Canonical.Hash(reply.Sources);
        var result = Compose(reply);
        Assert.True(result.Validation.Errors.Length == 0, string.Join("\n", result.Validation.Errors));
        var xml = XDocument.Parse(result.Xml);
        Assert.Equal("DEMO-DTX-PSRP", xml.Root!.Element(Base + "DossierCode")!.Value);
        Assert.Equal("DEMO-DTX-PSR", xml.Descendants(V1 + "psr_ref").Single().Value);
        Assert.Equal(fixture.Content.Period, reply.Content.Period);
        Assert.Equal(originals, Canonical.Hash(reply.Sources));
        Assert.Equal(EudamedRules.PsrAgreement(fixture.Content, fixture.Sources).ScopeHash,
            EudamedRules.PsrAgreement(reply.Content, reply.Sources).ScopeHash);
    }

    [Theory]
    [InlineData("Period", "eudamed_period_source")]
    [InlineData("Source", "eudamed_psrp_reply_source")]
    [InlineData("Agreement", "eudamed_psrp_agreement")]
    [InlineData("DraftAgreement", "eudamed_remote_reference")]
    public void PsrpReplyCannotReplaceItsOriginalPeriodSourceOrAcceptedAgreement(string change, string code)
    {
        var fixture = Create(EudamedDossierKind.PSRP);
        var parent = Parent(EudamedDossierKind.PSRP, fixture);
        if (change is "Agreement" or "DraftAgreement")
        {
            var psr = EudamedRules.Source<EudamedDossier>(parent.Package.Sources, "eudamedDossier");
            if (change == "Agreement")
            {
                var agreement = EudamedRules.Source<SummaryAgreement>(psr.Package.Sources, "summaryAgreement") with
                    { Version = 2, AgreementText = Reason + " Different agreement revision." };
                var package = psr.Package with { Sources = [Source("summaryAgreement", agreement.Id, agreement.Version, agreement)] };
                psr = psr with { Package = package, ScopeHash = Canonical.Hash(package) };
            }
            else psr = psr with { BusinessResponses = psr.BusinessResponses.Select(r => r with { Status = "DraftCreated" }).ToArray() };
            var packageParent = parent.Package with { Sources = [fixture.Sources.Single(s => s.Kind == "summaryReport"),
                Source("eudamedDossier", psr.Id, psr.Version, psr)] };
            parent = parent with { Package = packageParent, ScopeHash = Canonical.Hash(packageParent) };
        }
        var source = fixture.Sources.Single(s => s.Kind == "summaryReport");
        if (change == "Source")
        {
            var periodic = Canonical.Read<PeriodicSummaryReport>(source.Json) with { Version = source.Source.Version + 1 };
            source = Source("summaryReport", periodic.Id, periodic.Version, periodic);
        }
        var reply = fixture with
        {
            Content = fixture.Content with { IntendedAction = "Reply", ParentId = parent.Id, ParentVersion = parent.Version,
                ReplyToCorrespondenceId = "original-correspondence",
                Period = change == "Period" ? fixture.Content.Period! with { Start = fixture.Content.Period.Start.AddDays(1) } : fixture.Content.Period },
            Sources = [source, Source("eudamedDossier", parent.Id, parent.Version, parent)]
        };
        Assert.Equal(code, Assert.Throws<DomainException>(() => Compose(reply)).Code);
    }

    [Fact]
    public void ChangedSourceBytesOrReferencesCannotProduceAnApprovalScope()
    {
        var fixture = Create(EudamedDossierKind.MTR);
        Assert.Equal("eudamed_source_hash", Assert.Throws<DomainException>(() =>
            Compose(fixture with { Sources = [fixture.Sources[0] with { Json = "{}" }] })).Code);
        Assert.Equal("eudamed_mapping_scope", Assert.Throws<DomainException>(() =>
            Compose(fixture with { References = fixture.References with { MappingRationale = "Changed" } })).Code);
    }

    [Fact]
    public void FsnRevisionLineageIsSeparateFromItsFscaReference()
    {
        var fixture = Create(EudamedDossierKind.FSN);
        var revised = fixture.Content with { IntendedAction = "Revision", ParentId = "eudamed-fsn-prior", ParentVersion = 8 };
        EudamedRules.RequireContent(EudamedDossierKind.FSN, revised);
        Assert.NotEqual(revised.ParentId, revised.Fsn!.FscaId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PsurGroupUsesEachMembersConfirmedIssuerAndNeverCopiesTheLeadingIssuer(bool includeReferences)
    {
        var fixture = Create(EudamedDossierKind.PSUR);
        var psur = EudamedRules.Source<Psur>(fixture.Sources, "psur");
        var leading = psur.Snapshot.Series!.Governance;
        var second = leading with { Id = "cardio", ProductId = "cardio",
            Device = leading.Device with { BasicUdi = "DEMO-BASIC-CARDIO" } };
        var definition = new PsurGroupDefinition("group-codec", 1, "ortho", "DEMO-PSUR-GROUP-CODEC",
            [PsurGroupRules.Product(leading), PsurGroupRules.Product(second)], psur.Snapshot.Start, Reason, Reason, Now);
        var group = new PsurGrouping(PsurGroupRules.Contract,
            new(definition.Id, definition.Version, Canonical.Hash(definition)), definition,
            [new(PsurGroupRules.Product(leading), leading, psur.Snapshot),
                new(PsurGroupRules.Product(second), second, psur.Snapshot with { ProductId = "cardio" })], Reason);
        var snapshot = psur.Snapshot with { Group = group };
        psur = psur with { Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot) };
        psur = psur with { ScopeHash = PortfolioRules.PsurScope(psur) };
        var mapped = fixture.References with { Id = "eudamed-references-cardio", ProductId = "cardio",
            GovernanceSource = PsurGroupRules.Product(second).GovernanceSource, RegulatoryDevice = second.Device,
            Device = fixture.References.Device with { Id = "cardio", BasicUdi = second.Device.BasicUdi },
            UdiIssuingAgency = "HIBCC" };
        var source = Source("psur", psur.Id, psur.Version, psur);
        fixture = fixture with
        {
            Sources = includeReferences ? [source, Source("eudamedReferences", mapped.Id, mapped.Version, mapped)] : [source],
            Attachments = [fixture.Attachments[0] with { Source = source.Source }]
        };
        if (!includeReferences)
        {
            Assert.Equal("eudamed_group_references", Assert.Throws<DomainException>(() => Compose(fixture)).Code);
            return;
        }
        var result = Compose(fixture);
        Assert.True(result.Validation.Errors.Length == 0, string.Join("\n", result.Validation.Errors));
        var xml = XDocument.Parse(result.Xml);
        var additional = xml.Descendants(V1 + "psurDeviceChild").Single().ToString();
        Assert.Contains("HIBCC", additional, StringComparison.Ordinal);
        Assert.DoesNotContain("GS1", additional, StringComparison.Ordinal);
        Assert.Contains("GS1", xml.Descendants(V1 + "psurLeadingDevice").Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalAndNestedDossierProductScopesCannotDisappearFromReads()
    {
        var groupedHistory = Parent(EudamedDossierKind.PSUR, Create(EudamedDossierKind.PSUR))
            with { AccessProducts = ["ortho", "cardio"] };
        var narrowed = new Actor("one-product", "Medical", [Roles.Medical], ["ortho"], true);
        var complete = narrowed with { Products = ["ortho", "cardio"] };
        Assert.False(EudamedRules.CanRead(groupedHistory, narrowed));
        Assert.Throws<DomainException>(() => EudamedRules.RequireActorScope(groupedHistory, narrowed));
        Assert.True(EudamedRules.CanRead(groupedHistory, complete));
        var linked = Parent(EudamedDossierKind.PSUR, Create(EudamedDossierKind.PSUR));
        linked = linked with { Package = linked.Package with { Sources = [.. linked.Package.Sources,
            Source("eudamedDossier", groupedHistory.Id, groupedHistory.Version, groupedHistory)] } };
        Assert.False(EudamedRules.CanRead(linked, narrowed));
        Assert.Throws<DomainException>(() => EudamedRules.RequireActorScope(linked, narrowed));
        Assert.Contains("cardio", EudamedRules.AccessProducts(linked.Package));
    }

    [Theory]
    [InlineData("FscaCountry", false)]
    [InlineData("FscaCountry", true)]
    [InlineData("FscaSubaction", false)]
    [InlineData("FscaSubaction", true)]
    [InlineData("FsnCountry", false)]
    [InlineData("FsnCountry", true)]
    [InlineData("FsnFsca", false)]
    [InlineData("FsnFsca", true)]
    public void KeyScopeProjectionStillRejectsEveryDanglingOrDuplicateKey(string scope, bool duplicate)
    {
        var kind = scope.StartsWith("Fsca", StringComparison.Ordinal) ? EudamedDossierKind.FSCA : EudamedDossierKind.FSN;
        var document = XDocument.Parse(Compose(Create(kind)).Xml);
        if (duplicate)
        {
            var name = scope switch
            { "FscaSubaction" => "generalSubactionDescription", "FsnFsca" => "linkedFSCAFormDetails", _ => "affectedCountry" };
            var key = document.Descendants(V1 + name).First();
            key.AddAfterSelf(new XElement(key));
        }
        else if (scope == "FscaCountry")
            document.Descendants(V1 + "fsca_2").Single().Add(new XElement(V1 + "fscaCountrySpecificChildTemplate", new XAttribute("ordinal", 999)));
        else if (scope == "FscaSubaction")
            document.Descendants(V1 + "fsca_2").Single().Add(
                new XElement(V1 + "fscaCountrySpecificChildTemplate", new XAttribute("ordinal", 1),
                    new XElement(V1 + "countrySpecificInfoSection",
                        new XElement(V1 + "competentAuthority", "DEMO-CA"),
                        new XElement(V1 + "numberOfDevices", "not asserted in this structural fixture"),
                        new XElement(V1 + "countrySpecificSubactionSection",
                            new XElement(V1 + "countrySpecificSubactions", new XAttribute("ordinal", 999))))));
        else if (scope == "FsnCountry")
            document.Descendants(V1 + "fsnCountrySpecificChildTemplate").Single().SetAttributeValue("ordinal", 999);
        else
            document.Descendants(V1 + "fsnDeviceSection").Single().Add(
                new XElement(V1 + "fsnCorrectiveActionSection",
                    new XElement(V1 + "fscaCorrectiveAction", new XAttribute("ordinal", 999))));
        var xml = document.ToString(SaveOptions.DisableFormatting);
        var errors = composer.Validate(xml, kind);
        Assert.Contains(errors, error => error.Contains("key", StringComparison.OrdinalIgnoreCase));
        ContractCase(kind, xml, false);
    }

    private void ContractCase(EudamedDossierKind kind, string xml, bool expectedValid) =>
        output.WriteLine("COHORS_PINNED_CONTRACT_CASE:" + JsonSerializer.Serialize(new { kind, xml, expectedValid }));

    private (string Xml, EudamedValidation Validation) Compose(Projection fixture) =>
        composer.Compose(fixture.Kind, "eudamed-projection", fixture.Content, fixture.Binding, fixture.References,
            fixture.Calendar, fixture.Sources, fixture.Attachments);

    private static Projection Create(EudamedDossierKind kind)
    {
        var psur = WorkspaceTestData.Psur("psur-projection", new(2026, 12, 1));
        var governance = psur.Snapshot.Series!.Governance;
        var device = new Device("ortho", "DEMO implant", "Implant", governance.Device.BasicUdi, Reason, "DEMO model", ["DE"]);
        var actorSource = new VersionReference("eudamed-actor-projection", 3, Canonical.HashText("actor"));
        var references = new EudamedReferences("eudamed-references-ortho", 1, "ortho", actorSource,
            new("DEMO manufacturer", "MF", "DE", "SYNTHETISCH: Beispielstrasse 0",
                new("Demo", "Contact", "contact@example.invalid", "+49-000-0000"), true),
            "DEMO-EUDAMED-MF-TEST", device, psur.Snapshot.Series.GovernanceSource, governance.Device,
            new("DEMO-CERT", "1", "DEMO-NB", device.BasicUdi, new(2025, 1, 1), new(2027, 12, 31),
                Canonical.HashText("certificate"), Reason, "SyntheticMappingConfirmed", true),
            "DEMO-NB-ACTOR", Reason, "regulatory-test", Now, true, "GS1");
        var profile = new EudamedProfile(EudamedMode.Simulator, EudamedEnvironment.Simulation, actorSource.Id, actorSource.Version,
            references.Id, references.Version, "DEMO-CA-DE", EudamedContracts.Business, EudamedContracts.Gateway, null);
        var binding = new EudamedBinding("eudamed-channel-ortho", 1, 1, Canonical.Hash(profile), profile, actorSource,
            EudamedContracts.Bind(references), references.ActorIdentity, EudamedContracts.WsdlSha256, true);
        var calendar = new CalendarProfile("EU-DE", "synthetic-test", "Europe/Berlin", new(2025, 1, 1), new(2027, 12, 31), [], false);
        var content = new EudamedDossierContent("Synthetic source-bound dossier", Reason, Reason, "de", "EU-DE", ["DE"],
            "Submit", null, null, null, null, null);
        var evidence = WorkspaceTestData.Evidence(Now);
        var agreement = new SummaryAgreement("agreement-projection", 1, "ortho", "DEMO-PSR", device, "DEMO authority", calendar,
            SummaryReportingRules.Recipient("EU-DE"), "de", SummaryReportingRules.ReportContract, Reason, Reason, 59,
            new(2025, 1, 1), new(2027, 12, 31), Now, "RootCauseIdentified", Reason,
            new(evidence.Id, evidence.Version, Canonical.Hash(evidence)), evidence, "Active", "regulatory-test", Now, true);
        EudamedSource[] sources;
        EudamedAttachment[] attachments = [];
        switch (kind)
        {
            case EudamedDossierKind.MIR:
                var facts = new MirFacts { EventDateFrom = new(2026, 3, 28), EventDateTo = new(2026, 3, 28),
                    EventClassification = "All other reportable incidents", DeviceProblemCode = "A0401", DeviceLocation = "Manufacturer",
                    PreliminaryAnalysis = Reason, InitialCorrectiveAction = Reason, FurtherInvestigation = Reason, Rationale = Reason };
                var artifact = new MirArtifact("Initial", "codec-fixture", "codec-fixture", "codec-fixture", "", "",
                    true, [], ["Codec fixture only; no source approval is asserted."], facts, Now, true);
                var report = new ReportDraft(1, 1, "Initial", "EU-DE", "cohors:simulator:EU-DE", "de", "7.3.1", "7.3.1",
                    Reason, true, Reason, "test", "", [], ReportState.Draft, "", null, Mir: artifact);
                var c = WorkspaceTestData.Case("case-projection", Now) with
                { SystemReceivedAt = DateTimeOffset.Parse("2026-04-02T00:00:00Z"), Reports = [report] };
                sources = [Source("case", c.Id, c.Version, c)];
                break;
            case EudamedDossierKind.MTR:
                var exposure = new Exposure("exposure", 1, "ortho", "DE", new(2025, 1, 1), new(2025, 12, 31),
                    "Implants", 1000, Reason, Reason, true);
                var input = new TrendInput("ortho", "DE", exposure.Start, exposure.End, [], exposure, .001m, "poisson-upper-tail-1", []);
                var trend = new TrendRun("trend-projection", 1, "ortho", input, Statistics.Calculate(input), Now, "Reviewed", Reason, "medical-test", Reason, Canonical.Hash(input));
                content = content with { Period = new(exposure.Start, exposure.End, Reason), Mtr = new(new(2026, 3, 29), false, null, Reason) };
                sources = [Source("trend", trend.Id, trend.Version, trend)];
                break;
            case EudamedDossierKind.PSR:
                content = content with { PsrTerms = new(3, new(2025, 1, 1), Reason) };
                sources = [Source("summaryAgreement", agreement.Id, agreement.Version, agreement)];
                break;
            case EudamedDossierKind.PSRP:
                var periodic = new PeriodicSummaryReport("periodic-projection", 1, "ortho", "DEMO-PSRP",
                    SummaryReportingRules.Bind(agreement), agreement, new(2025, 1, 1), new(2025, 3, 31), [],
                    new(Reason, Reason, Reason, Reason), "Draft", "", [], null, null, null, true);
                var psrParent = Parent(EudamedDossierKind.PSR, Create(EudamedDossierKind.PSR));
                content = content with { Period = new(periodic.Start, periodic.End, Reason), ParentId = psrParent.Id, ParentVersion = psrParent.Version };
                sources = [Source("summaryReport", periodic.Id, periodic.Version, periodic), Source("eudamedDossier", psrParent.Id, psrParent.Version, psrParent)];
                break;
            case EudamedDossierKind.PSUR:
                content = content with { Period = new(psur.Snapshot.Start, psur.Snapshot.End, Reason) };
                sources = [Source("psur", psur.Id, psur.Version, psur)];
                attachments = [new("psur.pdf", "application/pdf", Canonical.HashText("pdf fixture"), sources[0].Source)];
                break;
            case EudamedDossierKind.FSCA:
                var capa = new Capa("capa-projection", 1, "ortho", "case-projection", "Synthetic action", Reason, "quality-test",
                    Reason, Reason, Now.AddDays(10), "Open", null, null, null);
                content = content with { Fsca = new(["DEMO-BATCH"], [], "Synthetic field correction", Reason, Reason,
                    "Quality reviewer", Reason, Now.AddDays(10), [capa.Id], new(2026, 3, 29), Reason, Reason, "NO_INV_ONGOING",
                    Reason, ["CUSTOMER_INFORMATION_ONLY"], ["IDENTIFY_DEVICE"], Reason) };
                sources = [Source("capa", capa.Id, capa.Version, capa)];
                attachments = [new("fsca.pdf", "application/pdf", Canonical.HashText("pdf fixture"), sources[0].Source)];
                break;
            case EudamedDossierKind.FSN:
                var fscaParent = Parent(EudamedDossierKind.FSCA, Create(EudamedDossierKind.FSCA));
                var hash = Canonical.HashText("pdf fixture");
                content = content with { Fsn = new(fscaParent.Id, fscaParent.Version, Reason, Reason, hash, "fsn.pdf", "1", "Draft",
                    true, "PRELIMINARY", new(2026, 3, 29)) };
                sources = [Source("eudamedDossier", fscaParent.Id, fscaParent.Version, fscaParent)];
                attachments = [new("fsn.pdf", "application/pdf", hash, new("document", 1, hash))];
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return new(kind, binding, references, calendar, content, sources, attachments);
    }

    private static EudamedDossier Parent(EudamedDossierKind kind, Projection value)
    {
        var id = "eudamed-parent-" + kind;
        var projection = new EudamedComposer().Compose(kind, id, value.Content, value.Binding, value.References,
            value.Calendar, value.Sources, value.Attachments);
        var package = new EudamedPackage(EudamedContracts.Business, kind, id, 1, "ortho", value.Binding,
            value.References, value.Content, value.Calendar, value.Sources, projection.Xml, Canonical.HashText(projection.Xml),
            value.Attachments, projection.Validation, true);
        return new(id, 4, "ortho", kind, 1, package, Canonical.Hash(package), "Draft", [], null, [], [],
            [new("inbox-codec", "message-codec", "attempt-codec", Canonical.HashText("original"), 1, "BusinessAccepted",
                "DEMO-DTX-" + kind, 1, [], null, Now, true, true)], [], [], "codec-test", Now);
    }

    private static EudamedSource Source<T>(string kind, string id, int version, T value) =>
        new(kind, new(id, version, Canonical.Hash(value)), Canonical.Serialize(value));
    private sealed record Projection(EudamedDossierKind Kind, EudamedBinding Binding, EudamedReferences References,
        CalendarProfile Calendar, EudamedDossierContent Content, EudamedSource[] Sources, EudamedAttachment[] Attachments);
}
