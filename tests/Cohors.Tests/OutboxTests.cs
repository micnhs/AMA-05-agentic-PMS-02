using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class OutboxTests(SqlFixture fixture)
{
    private async Task<string> EventFor(string id)
    {
        await using var connection = await fixture.Store.Open();
        using var query = SqlStateStore.Command(connection, null,
            "SELECT TOP(1) Id FROM dbo.Outbox WHERE AggregateId=@id AND Type='ComplaintRegistered'", ("id", id));
        return (string)(await query.ExecuteScalarAsync())!;
    }

    private async Task MakeDue(string id)
    {
        await using var connection = await fixture.Store.Open();
        using var query = SqlStateStore.Command(connection, null,
            "UPDATE dbo.Outbox SET NextAttemptAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Id=@id", ("id", id));
        await query.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task T12_T18_LeaseBackoffPoisonAndAuthorizedRecoveryPreserveExactlyOneReceipt()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var id = await EventFor(scenario.Case.Id);
        var owners = new[] { CaseScenario.Key(), CaseScenario.Key() };
        var claims = await Task.WhenAll(owners.Select(owner => fixture.Store.ClaimEvent(id, owner)));
        Assert.Single(claims, c => c is not null);
        var winner = claims[0] is not null ? 0 : 1;
        var wrongOwner = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.Store.CompleteEvent(id, "test-consumer", owners[1 - winner], null));
        Assert.Equal(51001, wrongOwner.Number);
        await fixture.Store.CompleteEvent(id, "test-consumer", owners[winner], "synthetic-retry");
        Assert.Null(await fixture.Store.ClaimEvent(id, CaseScenario.Key()));
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            await MakeDue(id);
            var owner = CaseScenario.Key();
            Assert.Equal(attempt, (await fixture.Store.ClaimEvent(id, owner))!.Attempts);
            await fixture.Store.CompleteEvent(id, "test-consumer", owner, "synthetic-retry");
        }
        await MakeDue(id);
        Assert.Null(await fixture.Store.ClaimEvent(id, CaseScenario.Key()));
        Assert.False(await fixture.Store.HasReceipt(id, "test-consumer"));
        var operations = new OperationsService(fixture.Store, fixture.Store, scenario.Clock);
        var input = new VersionCommand(0, "Technische Ursache im synthetischen Test behoben; kontrollierte Wiederaufnahme nach Pruefung.");
        await Assert.ThrowsAsync<DomainException>(() => operations.Recover(id, input, SqlFixture.Pms, CaseScenario.Key()));
        var recovery = await operations.Recover(id, input, SqlFixture.Quality, CaseScenario.Key());
        await fixture.Store.Manifest(recovery.Id, kind: "delivery-recovery");
        var lastOwner = CaseScenario.Key();
        var recovered = (await fixture.Store.ClaimEvent(id, lastOwner))!;
        Assert.Equal(1, recovered.RecoveryVersion);
        Assert.Equal(1, recovered.Attempts);
        await fixture.Store.CompleteEvent(id, "test-consumer", lastOwner, null);
        await fixture.Store.CompleteEvent(id, "test-consumer", lastOwner, null);
        Assert.True(await fixture.Store.HasReceipt(id, "test-consumer"));
        Assert.Null(await fixture.Store.ClaimEvent(id, CaseScenario.Key()));
        await Assert.ThrowsAsync<DomainException>(() => operations.Recover(id, input with { ExpectedVersion = 1 }, SqlFixture.Quality, CaseScenario.Key()));
    }

    [Fact]
    public async Task T12_ExpiredLeaseDoesNotAuthorizeAStaleCompletion()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var id = await EventFor(scenario.Case.Id);
        var first = CaseScenario.Key();
        Assert.NotNull(await fixture.Store.ClaimEvent(id, first));
        await using (var connection = await fixture.Store.Open())
        {
            using var command = SqlStateStore.Command(connection, null,
                "UPDATE dbo.Outbox SET LeaseUntil=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Id=@id", ("id", id));
            await command.ExecuteNonQueryAsync();
        }
        var second = CaseScenario.Key();
        Assert.NotNull(await fixture.Store.ClaimEvent(id, second));
        await Assert.ThrowsAsync<SqlException>(() => fixture.Store.CompleteEvent(id, "recovered-consumer", first, null));
        await fixture.Store.CompleteEvent(id, "recovered-consumer", second, null);
        Assert.True(await fixture.Store.HasReceipt(id, "recovered-consumer"));
    }
}
