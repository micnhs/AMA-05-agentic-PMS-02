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
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var c = WorkspaceOriginal<SafetyCase>(reader, 0, 1);
                var source = reader.IsDBNull(4) ? null : WorkspaceOriginal<SafetyCase>(reader, 4, 5);
                result.Add(new(c, reader.GetInt32(2), reader.GetInt32(3), source));
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
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                cases.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), Canonical.Read<Deadline[]>(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : Canonical.Read<FollowUpObligation[]>(reader.GetString(6))));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Full PSUR membership includes frozen BS history. Reuse its domain scope check before projecting dates.
                if (reader.GetString(0) == "psur") psurs.Add(WorkspaceOriginal<Psur>(reader, 1, 2));
                else reviews.Add(WorkspaceOriginal<PsurReview>(reader, 1, 2));
            }
        }
        await transaction.CommitAsync(ct);
        return new(cases.ToArray(), psurs.ToArray(), reviews.ToArray());
    }

    private static T WorkspaceOriginal<T>(SqlDataReader reader, int content, int hash)
    {
        var json = reader.GetString(content);
        Guard.Against(reader.IsDBNull(hash) || Canonical.HashText(json) != reader.GetString(hash),
            "workspace_source_integrity", "SQL-Quellfassung stimmt nicht mit ihrem gespeicherten Originalhash ueberein.", 409);
        return Canonical.Read<T>(json);
    }
}
