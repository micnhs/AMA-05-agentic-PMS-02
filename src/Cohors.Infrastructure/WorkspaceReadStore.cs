using System.Data;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Data.SqlClient;

namespace Cohors.Infrastructure;

public sealed partial class SqlStateStore : IWorkspaceReadStore
{
    public async Task<CaseProcessInput[]> ReadCockpitCases(string[] products, DateTimeOffset from,
        DateTimeOffset until, CancellationToken ct = default)
    {
        if (products.Length == 0) return [];
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var query = Command(connection, transaction, """
            SELECT a.Json,currentVersion.Hash,
                COALESCE(activity.Revised,0),COALESCE(activity.Requested,0),decisionSource.Json,decisionSource.Hash
            FROM dbo.Aggregates a
            LEFT JOIN dbo.AggregateVersions currentVersion
                ON currentVersion.Kind=a.Kind AND currentVersion.Id=a.Id AND currentVersion.Version=a.Version
            OUTER APPLY (
                SELECT MAX(CASE WHEN e.Action='AssessmentRevised' THEN e.Version END) AS Revised,
                    MAX(CASE WHEN e.Action='AssessmentRequested' THEN e.Version END) AS Requested
                FROM dbo.AuditEvents e WHERE e.AggregateId=a.Id AND e.Version<=a.Version
                    AND e.Action IN ('AssessmentRevised','AssessmentRequested')
            ) activity
            OUTER APPLY (
                SELECT TOP (1) JSON_VALUE(d.value,'$.id') AS Id, JSON_VALUE(d.value,'$.disposition') AS Disposition
                FROM OPENJSON(a.Json,'$.decisions') d ORDER BY CONVERT(int,d.[key]) DESC
            ) currentDecision
            OUTER APPLY (
                SELECT TOP (1) h.Json,h.Hash
                FROM dbo.AggregateVersions h
                CROSS APPLY (
                    SELECT TOP (1) JSON_VALUE(d.value,'$.id') AS Id
                    FROM OPENJSON(h.Json,'$.decisions') d ORDER BY CONVERT(int,d.[key]) DESC
                ) lastDecision
                WHERE h.Kind='case' AND h.Id=a.Id AND h.Version<=a.Version
                    AND currentDecision.Disposition='NonReportable' AND lastDecision.Id=currentDecision.Id
                ORDER BY h.Version
            ) decisionSource
            WHERE a.Kind='case' AND a.ProductId IN (SELECT value FROM OPENJSON(@products))
                AND CONVERT(datetimeoffset,JSON_VALUE(a.Json,'$.systemReceivedAt'))>=@from
                AND CONVERT(datetimeoffset,JSON_VALUE(a.Json,'$.systemReceivedAt'))<@until
            ORDER BY a.Id
            """, ("products", Canonical.Serialize(products)), ("from", from), ("until", until));
        var result = new List<CaseProcessInput>();
        await using (var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
            while (await reader.ReadAsync(ct))
            {
                var c = await WorkspaceOriginal<SafetyCase>(reader, 0, 1, ct);
                var revised = reader.GetInt32(2);
                var requested = reader.GetInt32(3);
                var source = reader.IsDBNull(4) ? null : await WorkspaceOriginal<SafetyCase>(reader, 4, 5, ct);
                result.Add(new(c, revised, requested, source));
            }
        await transaction.CommitAsync(ct);
        return result.ToArray();
    }

    public async Task<CalendarSources> ReadCalendarSources(string[] caseProducts, string[] authorizedProducts,
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default)
    {
        if (authorizedProducts.Length == 0) return new([], [], []);
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var query = Command(connection, transaction, """
            SELECT a.Id,a.Version,a.ProductId,JSON_VALUE(a.Json,'$.reference'),JSON_VALUE(a.Json,'$.title'),
                JSON_QUERY(a.Json,'$.deadlines'),JSON_QUERY(a.Json,'$.followUps')
            FROM dbo.Aggregates a
            WHERE a.Kind='case' AND a.ProductId IN (SELECT value FROM OPENJSON(@caseProducts))
                AND (
                    EXISTS (SELECT 1 FROM OPENJSON(a.Json,'$.deadlines')
                        WITH (LegalDue datetimeoffset '$.legalDue',SafetyDue datetimeoffset '$.safetyDue') d
                        WHERE (d.LegalDue>=@from AND d.LegalDue<@until) OR (d.SafetyDue>=@from AND d.SafetyDue<@until))
                    OR EXISTS (SELECT 1 FROM OPENJSON(a.Json,'$.followUps')
                        WITH (DueAt datetimeoffset '$.schedule.dueAt',SafetyDue datetimeoffset '$.schedule.safetyDue') f
                        WHERE (f.DueAt>=@from AND f.DueAt<@until) OR (f.SafetyDue>=@from AND f.SafetyDue<@until))
                )
            ORDER BY a.Id;
            SELECT a.Kind,a.Json,h.Hash FROM dbo.Aggregates a
            LEFT JOIN dbo.AggregateVersions h ON h.Kind=a.Kind AND h.Id=a.Id AND h.Version=a.Version
            WHERE a.Kind IN ('psur','psurReview') AND a.ProductId IN (SELECT value FROM OPENJSON(@authorizedProducts))
            ORDER BY a.Kind,a.Id;
            """, ("caseProducts", Canonical.Serialize(caseProducts)), ("authorizedProducts", Canonical.Serialize(authorizedProducts)),
                ("from", from), ("until", until));
        var cases = new List<CalendarCaseSource>();
        var psurs = new List<Psur>();
        var reviews = new List<PsurReview>();
        await using (var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            while (await reader.ReadAsync(ct))
                cases.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), Canonical.Read<Deadline[]>(await ReadText(reader, 5, ct)),
                    reader.IsDBNull(6) ? null : Canonical.Read<FollowUpObligation[]>(await ReadText(reader, 6, ct))));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Full PSUR membership includes frozen BS history. Reuse its domain scope check before projecting dates.
                if (reader.GetString(0) == "psur") psurs.Add(await WorkspaceOriginal<Psur>(reader, 1, 2, ct));
                else reviews.Add(await WorkspaceOriginal<PsurReview>(reader, 1, 2, ct));
            }
        }
        var correspondence = await ReadCorrespondenceSources(connection, transaction, authorizedProducts, ct);
        await transaction.CommitAsync(ct);
        return new(cases.ToArray(), psurs.ToArray(), reviews.ToArray(), correspondence);
    }

    public async Task<EudamedCorrespondenceSource[]> ReadCorrespondenceSources(string[] products, CancellationToken ct = default)
    {
        if (products.Length == 0) return [];
        await using var connection = await Open(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var sources = await ReadCorrespondenceSources(connection, transaction, products, ct);
        await transaction.CommitAsync(ct);
        return sources;
    }

    private static async Task<EudamedCorrespondenceSource[]> ReadCorrespondenceSources(SqlConnection connection,
        SqlTransaction transaction, string[] products, CancellationToken ct)
    {
        using var query = Command(connection, transaction, """
            SELECT l.Json,lv.Hash,d.Json,dv.Hash,approved.Json,approved.Hash,i.Json,iv.Hash,
                entry.AttemptId,correspondence.Id
            FROM dbo.Aggregates l
            LEFT JOIN dbo.AggregateVersions lv ON lv.Kind=l.Kind AND lv.Id=l.Id AND lv.Version=l.Version
            CROSS APPLY OPENJSON(l.Json,'$.entries') WITH (
                DossierId nvarchar(200) '$.dossierId',ApprovedVersion int '$.approvedDossier.version',
                AttemptId nvarchar(200) '$.attempt.id',Correspondence nvarchar(max) '$.correspondence' AS JSON) entry
            CROSS APPLY OPENJSON(entry.Correspondence) WITH (
                Id nvarchar(200) '$.id',InboxId nvarchar(200) '$.inboxId') correspondence
            LEFT JOIN dbo.Aggregates d ON d.Kind='eudamedDossier' AND d.Id=entry.DossierId
            LEFT JOIN dbo.AggregateVersions dv ON dv.Kind=d.Kind AND dv.Id=d.Id AND dv.Version=d.Version
            LEFT JOIN dbo.AggregateVersions approved ON approved.Kind='eudamedDossier'
                AND approved.Id=entry.DossierId AND approved.Version=entry.ApprovedVersion
            LEFT JOIN dbo.Aggregates i ON i.Kind='eudamedInbox' AND i.Id=correspondence.InboxId
            LEFT JOIN dbo.AggregateVersions iv ON iv.Kind=i.Kind AND iv.Id=i.Id AND iv.Version=i.Version
            WHERE l.Kind='eudamedDelivery' AND l.ProductId IN (SELECT value FROM OPENJSON(@products))
            ORDER BY l.Id,entry.AttemptId,correspondence.Id;
            """, ("products", Canonical.Serialize(products)));
        var sources = new List<EudamedCorrespondenceSource>();
        await using var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            const string missing = "SQL-Original fuer eine gespeicherte Korrespondenz fehlt; keine unbelegte Fristenanzeige.";
            var ledger = await WorkspaceOriginal<EudamedDeliveryLedger>(reader, 0, 1, ct);
            Guard.Against(reader.IsDBNull(2), "eudamed_deadline_source", missing, 409);
            var current = await WorkspaceOriginal<EudamedDossier>(reader, 2, 3, ct);
            Guard.Against(reader.IsDBNull(4), "eudamed_deadline_source", missing, 409);
            var approved = await WorkspaceOriginal<EudamedDossier>(reader, 4, 5, ct);
            Guard.Against(reader.IsDBNull(6), "eudamed_deadline_source", missing, 409);
            var inbox = await WorkspaceOriginal<EudamedInbox>(reader, 6, 7, ct);
            var attemptId = reader.GetString(8);
            var correspondenceId = reader.GetString(9);
            var entries = ledger.Entries.Where(e => e.Attempt.Id == attemptId).ToArray();
            Guard.Against(entries.Length != 1, "eudamed_deadline_source", "Korrespondenzversuch ist nicht eindeutig.", 409);
            var correspondence = entries[0].Correspondence.Where(c => c.Id == correspondenceId).ToArray();
            Guard.Against(correspondence.Length != 1, "eudamed_deadline_source", "Korrespondenzoriginal ist nicht eindeutig.", 409);
            sources.Add(new(ledger, entries[0], correspondence[0], current, approved, inbox));
        }
        return sources.ToArray();
    }

    private static async Task<T> WorkspaceOriginal<T>(SqlDataReader reader, int content, int hash, CancellationToken ct)
    {
        var json = await ReadText(reader, content, ct);
        Guard.Against(reader.IsDBNull(hash) || Canonical.HashText(json) != reader.GetString(hash),
            "workspace_source_integrity", "SQL-Quellfassung stimmt nicht mit ihrem gespeicherten Originalhash ueberein.", 409);
        return Canonical.Read<T>(json);
    }
}
