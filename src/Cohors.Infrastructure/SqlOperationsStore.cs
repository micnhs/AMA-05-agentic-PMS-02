using System.Data;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Infrastructure;

public sealed partial class SqlStateStore : IOperationsStore
{
    public async Task RequireOperationsReady(CancellationToken ct = default)
    {
        await RequireReady(ct);
        await using var connection = await Open(ct);
        using var command = new SqlCommand("""
            SELECT CASE WHEN IS_ROLEMEMBER('cohors_operations_runtime')=1
                AND IS_ROLEMEMBER('db_owner')=0 AND IS_ROLEMEMBER('db_ddladmin')=0 AND IS_ROLEMEMBER('db_securityadmin')=0
                AND HAS_PERMS_BY_NAME('dbo.CohorsOperationsWrite','OBJECT','EXECUTE')=1
                AND HAS_PERMS_BY_NAME('dbo','SCHEMA','ALTER')=0
                AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')=0
                AND NOT EXISTS(
                    SELECT 1 FROM (VALUES('dbo.Aggregates'),('dbo.AggregateVersions'),('dbo.Commands'),
                        ('dbo.AuditEvents'),('dbo.Outbox'),('dbo.Inbox'),('dbo.Deadlines'),
                        ('dbo.SearchProjection'),('dbo.SchemaVersions')) objects(Name)
                    CROSS JOIN (VALUES('INSERT'),('UPDATE'),('DELETE')) permissions(Name)
                    WHERE ISNULL(HAS_PERMS_BY_NAME(objects.Name,'OBJECT',permissions.Name),1)<>0)
                THEN 1 ELSE 0 END;
            """, connection);
        Guard.Against((int)(await command.ExecuteScalarAsync(ct))! != 1, "operations_principal_not_restricted",
            "Operations braucht den eingeschraenkten SQL-Principal; generische DML-/DDL-/Ownerrechte sind kein Runtimeersatz.", 503);
    }

    public Task<SafetyCase> Escalate(SafetyCase current, string[] alerts, DateTimeOffset at, CancellationToken ct = default) =>
        WriteOperations("case", current.Id, current.Version, current with
        { Version = current.Version + 1, Alerts = [.. current.Alerts, .. alerts] }, [], at, ct);

    public Task<PsurReview> Escalate(PsurReview current, string alert, DateTimeOffset at, CancellationToken ct = default) =>
        WriteOperations("psurReview", current.Id, current.Version, current with
        { Version = current.Version + 1, Alerts = [.. current.Alerts, alert] }, [], at, ct);

    public Task<EudamedDeliveryLedger> Escalate(EudamedDeliveryLedger current, EudamedCorrespondenceAlert[] alerts,
        StoreDependency[] dependencies, DateTimeOffset at, CancellationToken ct = default) =>
        WriteOperations("eudamedDelivery", current.Id, current.Version, current with
        { Version = current.Version + 1, CorrespondenceAlerts = [.. current.CorrespondenceAlerts ?? [], .. alerts] },
            dependencies, at, ct);

    public Task<OperationsHeartbeat> RecordHeartbeat(OperationsHeartbeat heartbeat, CancellationToken ct = default) =>
        WriteOperations("operations", heartbeat.Id, heartbeat.Version - 1, heartbeat, [], heartbeat.BusinessTime, ct);

    private async Task<T> WriteOperations<T>(string kind, string id, int expectedVersion, T result,
        StoreDependency[] dependencies, DateTimeOffset at, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        using var command = Command(connection, null, "dbo.CohorsOperationsWrite",
            ("Kind", kind), ("Id", id), ("ExpectedVersion", expectedVersion), ("Result", Canonical.Serialize(result)),
            ("At", at), ("Dependencies", Canonical.Serialize(dependencies)));
        command.CommandType = CommandType.StoredProcedure;
        try
        {
            var json = await ReadScalarText(command, ct)
                ?? throw new DomainException("operations_write_missing", "SQL-Watchdog hat keinen gebundenen Schreibbeleg geliefert.", 503);
            return Canonical.Read<T>(json);
        }
        catch (SqlException error) when (error.Number is >= 51100 and <= 51106)
        {
            var code = error.Number switch
            {
                51101 => "version_conflict",
                51102 => "dependency_version",
                51103 => "aggregate_busy",
                51104 => "operations_source_integrity",
                51105 => "retention_rebinding",
                51106 => "idempotency_conflict",
                _ => "operations_write_denied"
            };
            throw new DomainException(code,
                "Der eingeschraenkte SQL-Watchdogvertrag hat die Aenderung abgewiesen; keine Teilfreigabe oder Ersatzschreibweise.",
                409, error);
        }
    }
}
