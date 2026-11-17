using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class DispatchPreviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
    private static readonly Actor Ra = new("ra-preview", "Synthetic RA", [Roles.Regulatory], ["ortho"], true);
    private const string Reason = WorkspaceTestData.Reason;

    [Fact]
    public void CasePreviewBindsFrozenContentAndOriginalsInsteadOfLaterUnapprovedCaseFields()
    {
        var (value, source) = Case();
        value = value with { Title = "UNAPPROVED LIVE TITLE", Description = "UNAPPROVED LIVE CONTENT", Country = "FR",
            Sources = [source.Sources[0] with { Title = "UNAPPROVED LIVE SOURCE" }] };
        var before = Canonical.Serialize(value);
        var preview = DispatchPreviews.ForCase(value, source, Ra);
        Assert.Equal(value.CurrentReport!.FrozenJson, preview.FrozenPayload);
        Assert.Equal(Canonical.HashText(preview.FrozenPayload), preview.Binding.PayloadHash);
        Assert.Equal(value.CurrentReport.ScopeHash, preview.Binding.ScopeHash);
        Assert.Equal("GB", preview.Jurisdiction);
        Assert.Equal(["GB"], preview.Countries);
        Assert.Equal("MHRA-GB-SIMULATOR", preview.TechnicalRecipient);
        Assert.Contains("Great Britain", preview.BusinessRecipient);
        Assert.Equal(source.Evidence[0].Hash, Assert.Single(preview.Attachments).Hash);
        Assert.Equal(source.Sources[0].Title, Assert.Single(preview.Originals).Name);
        Assert.DoesNotContain("UNAPPROVED", Canonical.Serialize(preview));
        Assert.Equal(before, Canonical.Serialize(value));
        Assert.Empty(value.Submissions);
        DispatchPreviews.RequireConfirmation(preview.Binding, DispatchPreviews.CaseBinding(value));
    }

    [Theory]
    [InlineData("source", "dispatch_preview_source")]
    [InlineData("frozen", "frozen_tampered")]
    [InlineData("identity", "four_eyes")]
    [InlineData("unknown", "reconciliation_required")]
    [InlineData("role", "forbidden")]
    [InlineData("scope", "product_scope")]
    public void PreviewCannotBypassFrozenBytesOriginalSourceApprovalsOrUnknown(string failure, string code)
    {
        var (value, source) = Case();
        var actor = Ra;
        switch (failure)
        {
            case "source": source = source with { Version = source.Version + 1 }; break;
            case "frozen":
                value = value with { Reports = [value.CurrentReport! with
                    { FrozenJson = Canonical.Serialize(value.CurrentReport! with { Narrative = "Changed" }) }] };
                break;
            case "identity": value = value with { Approvals = value.Approvals.Select(a => a with { ActorId = Ra.Id }).ToArray() }; break;
            case "unknown":
                value = value with { Submissions = [new("prior", value.CurrentReport!.ScopeHash, 1, "original-correlation",
                    "TransmissionUnknown", "Unknown", Now, null, null, true, 1, "Unknown")] };
                break;
            case "role": actor = Ra with { Roles = [Roles.Medical] }; break;
            case "scope": actor = Ra with { Products = ["cardio"] }; break;
        }
        Assert.Equal(code, Assert.Throws<DomainException>(() => DispatchPreviews.ForCase(value, source, actor)).Code);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("payload")]
    [InlineData("ledger")]
    public void FinalConfirmationRejectsChangedSelectionWithoutChangingOldCommandBytes(string changed)
    {
        var (value, _) = Case();
        var selected = DispatchPreviews.CaseBinding(value);
        var current = changed switch
        {
            "revision" => selected with { AggregateVersion = selected.AggregateVersion + 1 },
            "scope" => selected with { ScopeHash = Canonical.HashText("other approved package") },
            "payload" => selected with { PayloadHash = Canonical.HashText("other frozen bytes") },
            _ => selected with { LedgerVersion = 2 }
        };
        Assert.Equal("dispatch_preview_changed", Assert.Throws<DomainException>(() =>
            DispatchPreviews.RequireConfirmation(selected, current)).Code);
        Assert.Equal("dispatch_preview_changed", Assert.Throws<DomainException>(() =>
            DispatchPreviews.RequireConfirmation(null, selected)).Code);
        Assert.Equal(Canonical.Serialize(new { ExpectedVersion = 4, Scenario = "Unknown", Reason }),
            Canonical.Serialize(new SubmissionCommand(4, "Unknown", Reason)));
        Assert.Equal(Canonical.Serialize(new { ExpectedVersion = 4, ExpectedLedgerVersion = 2, Scenario = "Unknown", Reason }),
            Canonical.Serialize(new SubmitEudamedDossierCommand(4, 2, "Unknown", Reason)));
    }

    [Fact]
    public void DossierPreviewSeparatesFrozenBusinessXmlFromDynamicTransportAndRetainsProfileFencing()
    {
        var dossier = Dossier();
        var json = Canonical.Serialize(dossier.Package);
        var before = Canonical.Serialize(dossier);
        var preview = DispatchPreviews.ForDossier(dossier, 7, json, Ra);
        Assert.Equal(json, preview.FrozenPayload);
        Assert.Equal(dossier.ScopeHash, preview.Binding.PayloadHash);
        Assert.Equal(7, preview.Binding.LedgerVersion);
        Assert.Equal(dossier.Package.BusinessXml, preview.BusinessXml);
        Assert.Equal(dossier.Package.Attachments[0].Hash, Assert.Single(preview.Attachments).Hash);
        Assert.Equal("DEMO-CA-DE", preview.BusinessRecipient);
        Assert.Equal("Simulation", preview.Environment);
        Assert.Contains(preview.Boundaries, b => b.Contains("MessageId", StringComparison.Ordinal) && b.Contains("AS4", StringComparison.Ordinal));
        Assert.Equal(before, Canonical.Serialize(dossier));
        Assert.Empty(dossier.Attempts);
        var binding = dossier.Package.Binding;
        var channel = new EudamedChannel(binding.ChannelId, binding.ChannelVersion, "ortho", binding.DispatchGeneration + 1,
            binding.Profile, new("draft", 1, binding.ProfileHash), Ra.Id, Now, true);
        Assert.Equal("eudamed_channel_fenced", Assert.Throws<DomainException>(() =>
            EudamedContracts.RequireCurrentChannel(binding, channel)).Code);
        Assert.Equal("dispatch_preview_original", Assert.Throws<DomainException>(() =>
            DispatchPreviews.ForDossier(dossier, 7, json + " ", Ra)).Code);
    }

    private static (SafetyCase Value, SafetyCase Source) Case()
    {
        var evidence = WorkspaceTestData.Evidence(Now);
        var source = WorkspaceTestData.Assessed(WorkspaceTestData.Case("preview-case", Now) with
        {
            Country = "GB", Jurisdiction = "GB", Evidence = [evidence],
            Sources = [new("original", "synthetic-external", "Complaint", "Frozen original", "cohors:synthetic",
                evidence.Hash, Now, Now, "Synthetic fixture")],
            Deadlines = [WorkspaceTestData.Deadline("gb-obligation", Now.AddDays(15), Now.AddDays(14)) with { Jurisdiction = "GB" }]
        });
        var report = new ReportDraft(1, 1, "Initial", "GB", "MHRA-GB-SIMULATOR", "en", "GB-SIM-1", "GB-SIM-1",
            "SYNTHETIC approved narrative, no real report.", true, "Synthetic uncertainty remains.", "test",
            source.CurrentAssessment!.EvidenceHash, [evidence.Hash], ReportState.Frozen, "", null, [source.Deadlines[0].Id],
            Preparation: new("cohors-report-prefill-2", "ortho", PortfolioRules.Reference(source),
                new("ortho", "Synthetic device", "Implant", "DEMO-BASIC", Reason, "DEMO-MODEL", ["GB"]),
                null, ReportTargets.For("GB"), "Human", "synthetic-run", [], []));
        report = report with { ScopeHash = ReportRules.Scope(source.CurrentAssessment!, report) };
        report = report with { FrozenJson = Canonical.Serialize(report) };
        return (source with { Version = 4, Reports = [report], Approvals = Approvals(report.ScopeHash) }, source);
    }

    private static Approval[] Approvals(string hash) =>
        [new("medical-preview", "medical-preview", "Synthetic Medical", Roles.Medical, hash, Now, Reason, Reason, Reason),
         new("ra-preview", Ra.Id, Ra.Name, Roles.Regulatory, hash, Now, Reason, Reason, null)];

    private static EudamedDossier Dossier()
    {
        var governance = WorkspaceTestData.Psur("preview-profile", new(2026, 12, 1)).Snapshot.Series!.Governance;
        var actorSource = new VersionReference("synthetic-actor", 1, Canonical.HashText("actor"));
        var references = new EudamedReferences("synthetic-references", 1, "ortho", actorSource,
            new("DEMO manufacturer", "MF", "DE", "SYNTHETIC address", new("Demo", "Contact", "demo@example.invalid", "0000"), true),
            "DEMO-EUDAMED-MF", new("ortho", "Synthetic implant", "Implant", governance.Device.BasicUdi, Reason, "DEMO", ["DE"]),
            new(governance.Id, governance.Version, Canonical.Hash(governance)), governance.Device,
            new("DEMO-CERT", "1", "DEMO-NB", "synthetic", new(2025, 1, 1), new(2027, 12, 31),
                Canonical.HashText("certificate"), Reason, "SyntheticMappingConfirmed", true), "DEMO-NB", Reason, Ra.Id, Now, true, "GS1");
        var profile = new EudamedProfile(EudamedMode.Simulator, EudamedEnvironment.Simulation, actorSource.Id, actorSource.Version,
            references.Id, references.Version, "DEMO-CA-DE", EudamedContracts.Business, EudamedContracts.Gateway, null);
        var binding = new EudamedBinding("synthetic-channel", 1, 1, Canonical.Hash(profile), profile, actorSource,
            EudamedContracts.Bind(references), references.ActorIdentity, EudamedContracts.WsdlSha256, true);
        var capa = new Capa("synthetic-capa", 1, "ortho", "synthetic-case", "Synthetic action", Reason, "owner",
            Reason, Reason, Now.AddDays(1), "Open", null, null, null);
        var source = new EudamedSource("capa", new(capa.Id, capa.Version, Canonical.Hash(capa)), Canonical.Serialize(capa));
        var content = new EudamedDossierContent("Synthetic approved FSCA", Reason, Reason, "de", "EU-DE", ["DE"], "Submit",
            null, null, null, new(["DEMO-BATCH"], [], "Synthetic action", Reason, Reason, "Synthetic responsible role",
                Reason, Now.AddDays(1), [capa.Id], new(2026, 11, 17), Reason, Reason, "NO_INV_ONGOING", Reason,
                ["OTHER"], ["OTHER"], Reason), null);
        var xml = "<SyntheticBusinessFixture />";
        var package = new EudamedPackage(EudamedContracts.Business, EudamedDossierKind.FSCA, "synthetic-dossier", 1, "ortho",
            binding, references, content, new("EU-DE", "test", "UTC", new(2025, 1, 1), new(2027, 12, 31), [], true),
            [source], xml, Canonical.HashText(xml), [new("Synthetic attachment.txt", "text/plain", Canonical.HashText(Reason), source.Source)],
            new(EudamedContracts.Business, "synthetic", "synthetic", EudamedContracts.Mapping,
                Canonical.HashText("schema fixture"), Canonical.HashText("enum fixture"), EudamedContracts.SchemaRuntime,
                Canonical.HashText("runtime fixture"), [], ["Only a synthetic model observation; no native schema or transport acceptance."], "Synthetic"),
            true);
        var hash = Canonical.Hash(package);
        return new(package.DossierId, 3, "ortho", package.Kind, 1, package, hash, "Frozen",
            Approvals(hash), hash, [], [], [], [], [], Ra.Id, Now);
    }
}
