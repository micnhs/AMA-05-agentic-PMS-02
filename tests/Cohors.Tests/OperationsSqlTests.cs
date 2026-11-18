using System.Data;
using System.Text.Json.Nodes;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "OperationsSqlBoundary")]
public sealed class OperationsSqlTests(SqlFixture fixture)
{
    [Fact]
    public async Task OperationsPrincipalCannotForgeApprovalsIdentitiesOrDirectAuditOutboxAndSchemaWrites()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create(); await scenario.Assess(); await scenario.Prepare(); await scenario.Approve();
        var current = scenario.Case;
        var restricted = await fixture.RestrictedOperations();
        await restricted.RequireOperationsReady();
        Assert.Equal("operations_principal_not_restricted", (await Assert.ThrowsAsync<DomainException>(
            () => fixture.Store.RequireOperationsReady())).Code);
        await using var connection = await restricted.Open();
        string principal;
        using (var permissions = new SqlCommand("""
            SELECT USER_NAME(),IS_ROLEMEMBER('db_owner'),
                HAS_PERMS_BY_NAME('dbo.CohorsOperationsWrite','OBJECT','EXECUTE'),
                HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE'),
                HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER ANY ROLE');
            """, connection))
        {
            await using var reader = await permissions.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            principal = reader.GetString(0);
            Assert.StartsWith("cohors_ops_", principal);
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(0, reader.GetInt32(4));
        }
        await using (var administrator = await fixture.Store.Open())
        {
            using var accidentalColumnGrant = new SqlCommand(
                $"GRANT UPDATE ON OBJECT::dbo.Aggregates(Json) TO [{principal}];", administrator);
            await accidentalColumnGrant.ExecuteNonQueryAsync();
        }
        await restricted.RequireOperationsReady();
        var before = await State(current.Id);
        var valid = current with { Version = current.Version + 1, Alerts = [.. current.Alerts, Alert(current)] };
        foreach (var role in new[] { Roles.Medical, Roles.Regulatory })
        {
            var forged = valid with { Approvals = valid.Approvals.Select(a => a.Role == role
                ? a with { ActorId = "forged-operations-human" } : a).ToArray() };
            await Rejected(connection, current, Canonical.Serialize(forged), scenario.Clock.Now);
        }
        await Rejected(connection, current, Canonical.Serialize(valid with
        { Deadlines = current.Deadlines.Select(d => d with { LegalDue = d.LegalDue.AddDays(1) }).ToArray() }), scenario.Clock.Now);
        var extra = JsonNode.Parse(Canonical.Serialize(valid))!.AsObject();
        extra["medicalApproval"] = "forged";
        await Rejected(connection, current, Canonical.Serialize(extra), scenario.Clock.Now);
        await Rejected(connection, current, Canonical.Serialize(valid with
        { Alerts = [.. current.Alerts, "MedicalApproved:forged"] }), scenario.Clock.Now);

        foreach (var table in new[] { "Aggregates", "AggregateVersions", "Commands", "AuditEvents", "Outbox",
            "Inbox", "Deadlines", "SearchProjection", "SchemaVersions" })
        {
            using var permission = SqlStateStore.Command(connection, null,
                "SELECT HAS_PERMS_BY_NAME(@table,'OBJECT','INSERT'),HAS_PERMS_BY_NAME(@table,'OBJECT','UPDATE'),HAS_PERMS_BY_NAME(@table,'OBJECT','DELETE')",
                ("table", "dbo." + table));
            await using var reader = await permission.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2));
        }
        foreach (var sql in new[]
        {
            "UPDATE dbo.Aggregates SET Json=@json WHERE Kind='case' AND Id=@id",
            "UPDATE dbo.AggregateVersions SET Json=@json WHERE Kind='case' AND Id=@id",
            """
            INSERT dbo.AuditEvents(AggregateId,Version,ActorId,ActorName,Roles,Action,Reason,At,CorrelationId,PayloadHash,PreviousHash,Hash)
            VALUES(@id,999,'forged','forged','["MedicalSafetyReviewer"]','MedicalApproved','forged',SYSUTCDATETIME(),'forged',REPLICATE('0',64),REPLICATE('0',64),REPLICATE('0',64))
            """,
            """
            INSERT dbo.Outbox(Id,AggregateId,AggregateKind,ProductId,Version,Type,Payload,CreatedAt)
            VALUES('forged',@id,'case','ortho',999,'RegulatoryApproved',@json,SYSUTCDATETIME())
            """,
            "INSERT dbo.Commands VALUES(REPLICATE('a',64),REPLICATE('b',64),'forged',@json,SYSUTCDATETIME())",
            "DELETE dbo.Deadlines WHERE CaseId=@id",
            "CREATE TABLE dbo.OperationsBypass(Id int)"
        })
        {
            using var denied = SqlStateStore.Command(connection, null, sql, ("id", current.Id), ("json", Canonical.Serialize(valid)));
            var error = await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync());
            Assert.Contains(error.Number, new[] { 229, 262 });
        }
        using (var forgedMetadata = Write(connection, current, Canonical.Serialize(valid), scenario.Clock.Now))
        {
            forgedMetadata.Parameters.AddWithValue("@Action", "MedicalApproved");
            Assert.Equal(8144, (await Assert.ThrowsAsync<SqlException>(() => forgedMetadata.ExecuteScalarAsync())).Number);
        }
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => restricted.Execute<SafetyCase>(
            new(current.Id, "case", current.ProductId, current.Version, CaseScenario.Key(), "MedicalApproved",
                "Forged application identity must not grant database write permissions.", SqlFixture.Medical,
                new { forged = true }, scenario.Clock.Now), _ => valid))).Number);
        Assert.Equal(before, await State(current.Id));
        await restricted.Escalate(current, [Alert(current)], scenario.Clock.Now);
        var recorded = (await fixture.Store.Audit(current.Id))[^1];
        Assert.Equal("cohors-operations", recorded.ActorId);
        Assert.Equal(new[] { Roles.Workflow }, recorded.Roles);
        Assert.Equal("DeadlineEscalated", recorded.Action);
        var updated = (await fixture.Store.Get<SafetyCase>("case", current.Id))!;
        Assert.Equal(Canonical.Serialize(current), Canonical.Serialize(updated with { Version = current.Version, Alerts = current.Alerts }));
        await fixture.Store.Manifest(current.Id);
    }

    [Fact]
    public async Task OperationsFailureIsAtomicAndConcurrentExactRetryHasOnlyOneVersionAuditAndOutboxEffect()
    {
        var scenario = new CaseScenario(fixture);
        await scenario.Create();
        var current = scenario.Case;
        var restricted = await fixture.RestrictedOperations();
        var before = await State(current.Id);
        var trigger = "OperationsRollback_" + CaseScenario.Key();
        await using var administrator = await fixture.Store.Open();
        using (var create = new SqlCommand($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.Outbox AFTER INSERT AS
            BEGIN
                IF EXISTS(SELECT 1 FROM inserted WHERE AggregateId=N'{current.Id}' AND Type=N'DeadlineEscalated')
                    THROW 51999,'Injected owned-fixture outbox failure.',1;
            END;
            """, administrator))
            await create.ExecuteNonQueryAsync();
        try
        {
            Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() =>
                restricted.Escalate(current, [Alert(current)], scenario.Clock.Now))).Number);
            Assert.Equal(before, await State(current.Id));
        }
        finally
        {
            using var drop = new SqlCommand($"DROP TRIGGER dbo.[{trigger}];", administrator);
            await drop.ExecuteNonQueryAsync();
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            restricted.Escalate(current, [Alert(current)], scenario.Clock.Now)));
        Assert.All(results, result => Assert.Equal(current.Version + 1, result.Version));
        Assert.Single(results.Select(Canonical.Hash).Distinct());
        var audit = await fixture.Store.Audit(current.Id);
        Assert.Single(audit, a => a.Action == "DeadlineEscalated");
        Assert.Single((await fixture.Store.PendingEvents(1000)), e => e.AggregateId == current.Id && e.Type == "DeadlineEscalated");
        Assert.Equal(current.Version + 1, (await fixture.Store.History<SafetyCase>("case", ["ortho"])).Count(c => c.Id == current.Id));
        var committed = await State(current.Id);
        await restricted.Escalate(current, [Alert(current)], scenario.Clock.Now.AddSeconds(1));
        Assert.Equal(committed, await State(current.Id));

        var different = $"LEGAL:{current.Deadlines[0].Id}:Gesetzliche Hoechstfrist ueberschritten; keine Rueckdatierung.";
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            restricted.Escalate(current, [different], scenario.Clock.Now))).Code);
        Assert.Equal(committed, await State(current.Id));
        await fixture.Store.Manifest(current.Id);
    }

    private static string Alert(SafetyCase current)
    {
        Assert.True(current.Deadlines[0].ImmediateAction);
        return $"IMMEDIATE:{current.Deadlines[0].Id}:Sofort an Medical Safety, RA und PRRC eskalieren.";
    }

    private static SqlCommand Write(SqlConnection connection, SafetyCase current, string json, DateTimeOffset at)
    {
        var command = SqlStateStore.Command(connection, null, "dbo.CohorsOperationsWrite",
            ("Kind", "case"), ("Id", current.Id), ("ExpectedVersion", current.Version),
            ("Result", json), ("At", at), ("Dependencies", "[]"));
        command.CommandType = CommandType.StoredProcedure;
        return command;
    }

    private static async Task Rejected(SqlConnection connection, SafetyCase current, string json, DateTimeOffset at)
    {
        using var command = Write(connection, current, json, at);
        Assert.Equal(51100, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync())).Number);
    }

    private async Task<string> State(string id)
    {
        await using var connection = await fixture.Store.Open();
        using var command = SqlStateStore.Command(connection, null, """
            SELECT a.Json,
                (SELECT COUNT(*) FROM dbo.AggregateVersions WHERE Kind='case' AND Id=@id),
                (SELECT COUNT(*) FROM dbo.AuditEvents WHERE AggregateId=@id),
                (SELECT COUNT(*) FROM dbo.Outbox WHERE AggregateId=@id),
                (SELECT COUNT(*) FROM dbo.Commands WHERE JSON_VALUE(Result,'$.id')=@id)
            FROM dbo.Aggregates a WHERE Kind='case' AND Id=@id;
            """, ("id", id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return Canonical.Serialize(new { Json = reader.GetString(0), History = reader.GetInt32(1),
            Audit = reader.GetInt32(2), Outbox = reader.GetInt32(3), Commands = reader.GetInt32(4) });
    }
}
