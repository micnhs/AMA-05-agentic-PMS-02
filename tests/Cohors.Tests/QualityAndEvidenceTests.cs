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
        Assert.Equal("completed", (await s.Service.Process(s.Case.Id, SqlFixture.Pms)).Steps.Single(step => step.Id == "regulatory").State);
        var quality = new QualityService(fixture.Store, fixture.Objects, s.Clock);
        var documents = new EvidenceService(fixture.Store, fixture.Objects, s.Clock);
        var original = (await fixture.Store.Get<Evidence>("evidence", "ortho-spec-v3"))!;
        var originalCaseVersion = s.Case.Version;
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
        var originalRead = await documents.Read(original.Id, original.Version, SqlFixture.Pms);
        var revisedRead = await documents.Read(revised.Id, revised.Version, SqlFixture.Pms);
        Assert.True(originalRead.HashVerified);
        Assert.True(revisedRead.HashVerified);
        Assert.Equal(original, originalRead.Evidence);
        Assert.Equal(original.Content, originalRead.Content);
        Assert.Equal(revised.Content, revisedRead.Content);
        await Assert.ThrowsAsync<DomainException>(() => documents.Read(original.Id, revised.Version + 20, SqlFixture.Pms));
        await Assert.ThrowsAsync<DomainException>(() => documents.Read(original.Id, original.Version,
            SqlFixture.Pms with { Products = ["cardio"] }));
        ReportRules.RequireSendable(s.Case, SqlFixture.Ra);
        s.Case = await s.Service.Link(s.Case.Id, new(s.Case.Version, revised.Id,
            "Folgeversion bewusst fuer erneuten menschlichen Vergleich verknuepfen.", revised.Version), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(ReportState.Superseded, s.Case.CurrentReport!.State);
        Assert.Throws<DomainException>(() => ReportRules.RequireSendable(s.Case, SqlFixture.Ra));
        var historical = await s.Service.ReadEvidence(s.Case.Id, original.Id, originalCaseVersion, SqlFixture.Pms);
        Assert.Equal(original.Version, historical.Evidence.Version);
        Assert.Equal(original.Content, historical.Content);
        Assert.True(historical.HashVerified);
        Assert.Equal(s.Case.Version, (await s.Service.Get(s.Case.Id, SqlFixture.Pms)).Version);
        change = await quality.CompleteChange(change.Id, new(change.Version,
            "Umsetzung gegen genehmigten Auftrag und konkrete Folgeversion kontrolliert.",
            "Synthetische Folgeversion mit neuem Inhalt und unveraenderlich aufbewahrter Vorversion nachgewiesen."),
            SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("Implemented", change.Status);
        await fixture.Store.Manifest(revised.Id, kind: "evidence");
    }

    [Fact]
    public async Task DocumentReadRejectsMissingTamperedAndSqlDivergentTextWithoutClaimingVerification()
    {
        var s = new CaseScenario(fixture);
        s.Case = await s.Service.Register(s.Input with { Description = s.Input.Description + " Unique document " + CaseScenario.Key() },
            SqlFixture.Pms, CaseScenario.Key());
        var evidence = s.Case.Evidence.Single(e => e.Kind == "Complaint");
        var path = Path.Combine(fixture.ObjectDirectory, evidence.Hash);
        var bytes = await File.ReadAllBytesAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, "SYNTHETIC TAMPERED TEXT");
            await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadEvidence(s.Case.Id, evidence.Id, s.Case.Version, SqlFixture.Pms));
            File.Delete(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => s.Service.ReadEvidence(s.Case.Id, evidence.Id, s.Case.Version, SqlFixture.Pms));
        }
        finally { await File.WriteAllBytesAsync(path, bytes); }
        await Assert.ThrowsAsync<DomainException>(() => EvidenceService.ReadOriginal(evidence with { Content = "Changed SQL text" },
            fixture.Objects, CancellationToken.None));
        var restored = await s.Service.ReadEvidence(s.Case.Id, evidence.Id, s.Case.Version, SqlFixture.Pms);
        Assert.Equal(evidence.Content, restored.Content);
        Assert.True(restored.HashVerified);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadEvidence(s.Case.Id, evidence.Id, null,
            SqlFixture.Pms with { Products = ["cardio"] }));
    }

    [Fact]
    public async Task ProcessUsesAuditOrderForHumanRevisionAfterFailedRunEvenAtTheSameBusinessTime()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Assess(AgentMode.Failure);
        Assert.Equal("analysis", (await s.Service.Process(s.Case.Id, SqlFixture.Pms)).CurrentStep);
        s.Case = await s.Service.Revise(s.Case.Id, new(s.Case.Version, s.Case.CurrentAssessment!.Content,
            "Menschliche Nachpruefung nach dokumentiertem Modellfehler; kein verdeckter Replay-Erfolg."), SqlFixture.Medical, CaseScenario.Key());
        var result = await s.Service.Process(s.Case.Id, SqlFixture.Pms);
        Assert.Equal("prepare", result.NextAction);
        Assert.Equal("completed", result.Steps.Single(step => step.Id == "analysis").State);
        Assert.Contains(s.Case.AgentRuns, run => run.Status == "Failed");
        Assert.Equal(s.Case.Version, (await s.Service.Get(s.Case.Id, SqlFixture.Pms)).Version);
    }

    [Fact]
    public async Task ProcessDistinguishesPendingAndApprovedNonReportabilityWithoutInventingADelivery()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Respond(); await s.Assess();
        s.Case = await s.Service.Decide(s.Case.Id, new(s.Case.Version, Disposition.NonReportable,
            "Synthetische medizinische Einzelfallentscheidung: konkrete Fallbefunde begruenden derzeit keine meldepflichtige Produktbeteiligung."),
            SqlFixture.Medical, CaseScenario.Key());
        var pending = await s.Service.Process(s.Case.Id, SqlFixture.Pms);
        Assert.Equal("regulatory", pending.CurrentStep);
        Assert.Equal("not-required", pending.Steps.Single(step => step.Id == "report").State);
        s.Case = await s.Service.ApproveDisposition(s.Case.Id, new(s.Case.Version,
            "Unabhaengige regulatorische Pruefung der medizinischen Begruendung, Evidenz und unveraenderten Einzelpflichten."),
            SqlFixture.Ra, CaseScenario.Key());
        var approved = await s.Service.Process(s.Case.Id, SqlFixture.Pms);
        Assert.Equal("followup", approved.CurrentStep);
        Assert.Equal("completed", approved.Steps.Single(step => step.Id == "regulatory").State);
        Assert.Equal("not-required", approved.Steps.Single(step => step.Id == "delivery").State);
        Assert.NotEqual("completed", approved.Steps.Single(step => step.Id == "followup").State);
    }
}
