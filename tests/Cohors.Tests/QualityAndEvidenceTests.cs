using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class QualityAndEvidenceTests(SqlFixture fixture)
{
    [Fact]
    public async Task T17_CapaCannotCloseWithoutAuthorizedIndependentEffectivenessEvidence()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var service = new QualityService(fixture.Store, fixture.Objects, s.Clock);
        var capa = await service.OpenCapa(new(s.Case.Id, "ortho", "Synthetische Montagepruefung",
            "Drei gezielte Pruefungen der Montage- und Dokumentationsbedingungen durchfuehren.",
            "Ursachenabgrenzung und nachpruefbare Beweissicherung zu dem dokumentierten Funktionsverlust.",
            SqlFixture.Pms.Id, "Drei unabhaengige Folgepruefungen ohne Wiederholung der beschriebenen Abweichung.",
            s.Clock.Now.AddDays(14)), SqlFixture.Pms, CaseScenario.Key());
        var review = new VerifyCapaCommand(capa.Version,
            "Synthetischer Pruefbericht: drei Folgepruefungen dokumentiert; Kriterium einzeln mit Soll/Ist-Vergleich bestaetigt.",
            true, "Wirksamkeit anhand des zuvor festgelegten Kriteriums unabhaengig beurteilt.");
        await Assert.ThrowsAsync<DomainException>(() => service.Verify(capa.Id, review, SqlFixture.Pms, CaseScenario.Key()));
        await Assert.ThrowsAsync<DomainException>(() => service.Verify(capa.Id, review with { Evidence = "" }, SqlFixture.Quality, CaseScenario.Key()));
        await Assert.ThrowsAsync<DomainException>(() => service.Verify(capa.Id, review,
            SqlFixture.Pms with { Roles = [Roles.Quality] }, CaseScenario.Key()));
        capa = await service.Verify(capa.Id, review with { Effective = false }, SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("NotEffective", capa.Status);
        capa = await service.Amend(capa.Id, new(capa.Version, capa.Action + " Abweichung nacharbeiten und erneut pruefen.",
            capa.EffectivenessCriterion, s.Clock.Now.AddDays(21), "Nach negativem Ergebnis begruendet nacharbeiten; keine vorgetaeuschte Wirksamkeit."),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Null(capa.VerifiedBy);
        capa = await service.Verify(capa.Id, review with { ExpectedVersion = capa.Version }, SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("Verified", capa.Status);
        Assert.Equal(SqlFixture.Quality.Id, capa.VerifiedBy);
        Assert.NotNull(capa.VerifiedAt);
        await fixture.Store.Manifest(capa.Id, kind: "capa");
    }

    [Fact]
    public async Task T04_T07_ControlledDocumentRevisionPreservesHistoricalRetrievalAndInvalidatesOnlyExplicitRelinking()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        var quality = new QualityService(fixture.Store, fixture.Objects, s.Clock);
        var documents = new EvidenceService(fixture.Store, fixture.Objects, s.Clock);
        var original = (await fixture.Store.Get<Evidence>("evidence", "ortho-spec-v3"))!;
        var change = await quality.OpenChange(new(s.Case.Id, "ortho", original.Id, original.Version,
            "Montagerandbedingungen im synthetischen Spezifikationsnachtrag deutlicher abgrenzen.",
            "Der synthetische Fall zeigt einen Dokumentationspruefbedarf; keine automatische Risikoakzeptanz."),
            SqlFixture.Pms, CaseScenario.Key());
        await Assert.ThrowsAsync<DomainException>(() => quality.ApproveChange(change.Id,
            new(change.Version, "Eigene Beantragung darf nicht als unabhaengiger QM-Review gelten.", null),
            SqlFixture.Pms with { Roles = [Roles.Quality] }, CaseScenario.Key()));
        change = await quality.ApproveChange(change.Id, new(change.Version,
            "Unabhaengiger QM-Review bestaetigt den konkreten synthetischen Dokumentaenderungsauftrag.", null),
            SqlFixture.Quality, CaseScenario.Key());
        await Assert.ThrowsAsync<DomainException>(() => quality.CompleteChange(change.Id,
            new(change.Version, "Nicht vorwegnehmen.", "Behaupteter Abschluss ohne tatsaechlich veroeffentlichte Dokumentfolgeversion."),
            SqlFixture.Quality, CaseScenario.Key()));
        var effective = s.Clock.Now.AddDays(1);
        var revised = await documents.Revise(original.Id, new(original.Version, change.Id, original.Title + " / Folgeversion",
            original.Content + "\nSYNTHETISCHER NACHTRAG: Randbedingungen einzeln gegen den tatsaechlichen Montagebefund pruefen.",
            effective, "Genehmigte synthetische Spezifikationsfolgeversion; kein Eingriff in das Originaldokument."),
            SqlFixture.Quality, CaseScenario.Key());
        var search = new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store);
        var before = await search.Retrieve("ortho", s.Case.OccurredAt!.Value, "specification", SqlFixture.Pms);
        var after = await search.Retrieve("ortho", effective.AddHours(1), "specification", SqlFixture.Pms);
        Assert.Equal(original.Version, before.Single(e => e.Id == original.Id).Version);
        Assert.Equal(revised.Version, after.Single(e => e.Id == original.Id).Version);
        Assert.Equal(original.Content, await fixture.Objects.Read(original.Hash));
        ReportRules.RequireSendable(s.Case, SqlFixture.Ra);
        s.Case = await s.Service.Link(s.Case.Id, new(s.Case.Version, revised.Id,
            "Folgeversion bewusst fuer erneuten menschlichen Vergleich verknuepfen.", revised.Version), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(ReportState.Superseded, s.Case.CurrentReport!.State);
        Assert.Throws<DomainException>(() => ReportRules.RequireSendable(s.Case, SqlFixture.Ra));
        change = await quality.CompleteChange(change.Id, new(change.Version,
            "Umsetzung gegen genehmigten Auftrag und konkrete Folgeversion kontrolliert.",
            "Synthetische Folgeversion mit neuem Inhalt und unveraenderlich aufbewahrter Vorversion nachgewiesen."),
            SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("Implemented", change.Status);
        await fixture.Store.Manifest(revised.Id, kind: "evidence");
    }
}
