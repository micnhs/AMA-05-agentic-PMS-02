namespace Cohors.Domain;

public sealed record CalendarProfile(string Id, string Version, string TimeZone,
    DateOnly ValidFrom, DateOnly ValidTo, DateOnly[] Holidays, bool ApplyEuEndRules);
public sealed record Deadline(string Id, string Jurisdiction, Seriousness Seriousness,
    DateTimeOffset TriggerAt, DateTimeOffset LegalDue, DateTimeOffset SafetyDue,
    string RuleVersion, string CalendarVersion, string TimeZone, string[] Calculation,
    string Status, bool ImmediateAction);

public static class DeadlineCalculator
{
    public const string RuleVersion = "cohors-vigilance-2026.1";

    public static DateTimeOffset LocalDateEnd(DateOnly date, string timeZone)
    {
        var midnight = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, TimeZoneInfo.FindSystemTimeZoneById(timeZone)),
            TimeSpan.Zero).AddTicks(-1);
    }

    public static Deadline Calculate(string jurisdiction, Seriousness seriousness,
        DateTimeOffset awareness, CalendarProfile calendar)
    {
        Guard.Against(jurisdiction == "GB" && calendar.ApplyEuEndRules, "jurisdiction_rule",
            "EU-Fristendregeln duerfen nicht auf GB angewendet werden.");
        Guard.Against(jurisdiction != "GB" && !calendar.ApplyEuEndRules, "jurisdiction_rule",
            "Das EU/NI-Demoprofil erfordert die EU-Fristendregeln.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone);
        var dayZero = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(awareness, zone).DateTime);
        var days = seriousness switch
        {
            Seriousness.PublicHealthThreat => 2,
            Seriousness.DeathOrSeriousDeterioration => 10,
            Seriousness.OtherSerious => 15,
            _ => throw new DomainException("seriousness", "Unbekannte Fristenkategorie.")
        };
        var nominal = dayZero.AddDays(days);
        Guard.Against(dayZero < calendar.ValidFrom || nominal > calendar.ValidTo, "calendar_expired",
            "Kein freigegebener Kalender fuer diesen Zeitraum; manuell eskalieren.");
        bool Working(DateOnly day) => day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
            && !calendar.Holidays.Contains(day);
        var dueDay = nominal;
        var steps = new List<string>
        {
            $"Tag 0: {dayZero:yyyy-MM-dd} ({calendar.TimeZone}); erstes Organisationswissen, nicht Systemeingang.",
            $"{days} Kalendertage: {nominal:yyyy-MM-dd}; unverzuegliche Bearbeitung bleibt erforderlich."
        };
        if (calendar.ApplyEuEndRules)
        {
            var workingDays = 0;
            for (var day = dayZero.AddDays(1); day <= dueDay; day = day.AddDays(1))
                if (Working(day)) workingDays++;
            while (!Working(dueDay) || workingDays < 2)
            {
                dueDay = dueDay.AddDays(1);
                Guard.Against(dueDay > calendar.ValidTo, "calendar_expired", "Kalender deckt die Verlaengerung nicht ab.");
                if (Working(dueDay)) workingDays++;
            }
            steps.Add($"VO 1182/71 Art. 3(4): Arbeitstag am Fristende; Art. 3(5): mindestens zwei Arbeitstage ({workingDays}).");
        }
        else steps.Add("GB-Demoprofil: Kalendertage, keine uebernommene EU-Wochenendverlaengerung; nationale Fachabnahme offen.");
        DateTimeOffset EndOfDay(DateOnly date) => LocalDateEnd(date, calendar.TimeZone);
        var legal = EndOfDay(dueDay);
        // A deliberately earlier internal target; never consumes an EU weekend extension.
        var safety = EndOfDay(nominal).AddHours(-12);
        steps.Add($"Interne SOP: nominales Tagesende minus 12 Stunden; keine gesetzliche Frist.");
        return new(Guid.NewGuid().ToString("N"), jurisdiction, seriousness, awareness, legal, safety,
            RuleVersion, calendar.Version, calendar.TimeZone, steps.ToArray(), "Open",
            seriousness == Seriousness.PublicHealthThreat);
    }

    public static Deadline ProtectEarlier(Deadline old, Deadline next) => next with
    {
        Status = old.Status,
        TriggerAt = old.TriggerAt < next.TriggerAt ? old.TriggerAt : next.TriggerAt,
        LegalDue = old.LegalDue < next.LegalDue ? old.LegalDue : next.LegalDue,
        SafetyDue = old.SafetyDue < next.SafetyDue ? old.SafetyDue : next.SafetyDue,
        Calculation = [.. next.Calculation, "Fruehere bestehende Fälligkeit bleibt geschuetzt; kein Reset durch Reklassifizierung."]
    };
}
