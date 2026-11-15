namespace Cohors.Domain;

public sealed record FollowUpSchedule(DateOnly ReportDate, DateTimeOffset DueAt, DateTimeOffset SafetyDue,
    string TimeZone, string CalendarVersion, string RuleVersion);
public sealed record FollowUpObligation(string Id, string Jurisdiction, string SourceReportHash, int SourceReportVersion,
    string SourceReceiptReference, FollowUpSchedule Schedule, string Status, DateTimeOffset CreatedAt,
    string? CompletedByReportHash, DateTimeOffset? CompletedAt);

public static class FollowUpRules
{
    public const string RuleVersion = "human-promised-mir-date-1";

    public static FollowUpSchedule Schedule(DateOnly date, CalendarProfile calendar)
    {
        Guard.Against(date < calendar.ValidFrom || date > calendar.ValidTo, "followup_calendar",
            "Versioniertes Zeitprofil deckt den zugesagten Folgeberichtstermin nicht ab.");
        var due = DeadlineCalculator.LocalDateEnd(date, calendar.TimeZone);
        return new(date, due, due.AddHours(-12), calendar.TimeZone, calendar.Version, RuleVersion);
    }

    public static string[] Covered(SafetyCase c, ReportDraft report)
    {
        var ids = report.FollowUpObligationIds ?? [];
        Guard.Against(ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            ids.Any(id => !(c.FollowUps ?? []).Any(f => f.Id == id && f.Jurisdiction == report.Jurisdiction)) ||
            report.Kind == "Initial" && ids.Length != 0, "followup_scope",
            "Nur explizit gebundene Folgepflichten derselben Jurisdiktion duerfen durch einen Folge-/Abschlussbericht erledigt werden.", 409);
        return ids;
    }

    public static FollowUpObligation[]? Acknowledge(SafetyCase c, ReportDraft report, string receipt, DateTimeOffset now)
    {
        var covered = Covered(c, report);
        var result = (c.FollowUps ?? []).Select(f => covered.Contains(f.Id) && f.Status == "Open"
            ? f with { Status = "SimulatedReported", CompletedByReportHash = report.ScopeHash, CompletedAt = now } : f).ToList();
        if (report.Kind != "Final" && report.Mir is { } mir)
        {
            var schedule = mir.FollowUpSchedule ?? throw new DomainException("legacy_followup_schedule",
                "Alt-MIR besitzt keinen gebundenen Zeit-/Kalenderscope. Kontrollierte Migration vor Abgleich erforderlich; kein stiller Terminersatz.", 409);
            var id = "followup-" + Canonical.Hash(new { report.ScopeHash, report.Jurisdiction, schedule })[..32];
            if (result.All(f => f.Id != id))
                result.Add(new(id, report.Jurisdiction, report.ScopeHash, report.Version, receipt, schedule, "Open", now, null, null));
        }
        return result.Count == 0 && c.FollowUps is null ? null : result.ToArray();
    }
}
