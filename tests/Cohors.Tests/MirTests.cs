using System.Xml.Linq;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class MirTests(SqlFixture fixture)
{
    [Fact]
    public async Task T14_RequiredFactsAreNotInventedAndInvalidXmlCannotBeApprovedOrExported()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess();
        s.Case = await s.Service.Prepare(s.Case.Id, new(s.Case.Version, "Initial", "EU-IE", "de", true,
            "Direkte Befunde werden nachgefordert.", "Begruendeten vorlaeufigen Entwurf ohne erfundene Pflichtdaten erstellen."), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("mir_required", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Approve(s.Case.Id,
            new(s.Case.Version, "Medizinischen Entwurf vorlaeufig beurteilen.", "Offene Ursachen werden parallel belegt und fristgerecht weiter untersucht."),
            SqlFixture.Medical, CaseScenario.Key(), true))).Code);
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version, new(),
            "Fehlende Pflichtdaten ausdruecklich pruefen statt sie durch Ersatzwerte auszufuellen."), SqlFixture.Medical, CaseScenario.Key());
        var invalid = s.Case.CurrentReport!.Mir!;
        Assert.False(invalid.Valid);
        Assert.Contains(invalid.Errors, e => e.Contains("Ereigniszeitraum", StringComparison.Ordinal));
        var xml = XDocument.Parse(invalid.Xml);
        Assert.Null(xml.Root!.Element("admin_info")!.Element("adverseEventDateFrom"));
        Assert.Null(xml.Root!.Element("incident_info")!.Element("clinical_event_info")!.Element("imdrfCodeChoice1"));
        await Assert.ThrowsAsync<DomainException>(() => s.Regulatory.Export(s.Case.Id, SqlFixture.Ra));
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version, s.MirFacts(),
            "Explizite synthetische Pflichtangaben aus dem Testprofil menschlich dokumentieren."), SqlFixture.Medical, CaseScenario.Key());
        var valid = await s.Regulatory.Export(s.Case.Id, SqlFixture.Ra);
        Assert.True(valid.Valid, string.Join("\n", valid.Errors));
        Assert.Equal(MirSchemas.For("Initial").Hash, valid.SchemaHash);
        Assert.Contains("SIMULATION ONLY", valid.Xml);
        Assert.Equal("2026-11-12", XDocument.Parse(valid.Xml).Root!.Element("admin_info")!.Element("mfrAwarenessDate")!.Value);
        Assert.Null(XDocument.Parse(valid.Xml).Root!.Element("device_info")!.Element("udiDI"));
        await s.Approve();
        var scope = s.Case.CurrentReport!.ScopeHash;
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version, s.MirFacts() with { NextReportDate = new(2026, 11, 25) },
            "Zugesagten Folgetermin begruendet aendern; vorherige Signaturen gelten nicht fuer das neue XML."), SqlFixture.Medical, CaseScenario.Key());
        Assert.NotEqual(scope, s.Case.CurrentReport!.ScopeHash);
        Assert.Equal(ReportState.Draft, s.Case.CurrentReport.State);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept",
            "Unzulässigen Versand ohne neue Signaturen pruefen."), SqlFixture.Ra, CaseScenario.Key()));
        await fixture.Store.Manifest(s.Case.Id);
    }

    [Theory]
    [InlineData("FollowUp")]
    [InlineData("Final")]
    public async Task T14_FollowupAndFinalUseTheirOwnPinnedSchemasAndSameJurisdictionReceipt(string kind)
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Respond(); await s.Assess(); await s.Prepare(); await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept", "Erste simulierte Meldung mit gebundenem XML."),
            SqlFixture.Ra, CaseScenario.Key());
        var receipt = s.Case.Submissions.Last().ReceiptReference;
        var obligation = Assert.Single(Assert.IsType<FollowUpObligation[]>(s.Case.FollowUps));
        Assert.Equal("Open", obligation.Status);
        await s.Prepare(kind);
        Assert.Equal([obligation.Id], Assert.IsType<string[]>(s.Case.CurrentReport!.FollowUpObligationIds));
        var artifact = s.Case.CurrentReport!.Mir!;
        Assert.True(artifact.Valid, string.Join("\n", artifact.Errors));
        Assert.Equal(MirSchemas.For(kind).Hash, artifact.SchemaHash);
        Assert.Equal(receipt, XDocument.Parse(artifact.Xml).Root!.Element("admin_info")!.Element("ncaReportNo")!.Value);
        await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept", "Geprueften synthetischen Folge- oder Abschlussbericht simulieren."),
            SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal(2, s.Case.Submissions.Length);
        Assert.All(s.Case.Deadlines, d => Assert.Equal("SimulatedReported", d.Status));
        Assert.Equal("SimulatedReported", s.Case.FollowUps!.Single(f => f.Id == obligation.Id).Status);
        Assert.Equal(kind == "Final" ? 0 : 1, s.Case.FollowUps!.Count(f => f.Status == "Open"));
    }

    [Fact]
    public async Task T14_XmlValidationDoesNotResolveExternalEntitiesAndFinalCannotBePreliminary()
    {
        var composer = new MirComposer();
        Assert.NotEmpty(composer.Validate("<!DOCTYPE incident [<!ENTITY x SYSTEM 'file:///not-readable'>]><incident>&x;</incident>", "Initial"));
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess();
        Assert.Equal("final_preliminary", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Prepare(s.Case.Id,
            new(s.Case.Version, "Final", "EU-IE", "de", true, "Untersuchung offen", "Eine vorlaeufige Abschlussmeldung wird nicht zugelassen."),
            SqlFixture.Pms, CaseScenario.Key()))).Code);
    }
}
