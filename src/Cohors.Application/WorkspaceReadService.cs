using Cohors.Domain;

namespace Cohors.Application;

public sealed record CaseProcessInput(SafetyCase Case, int LastAssessmentRevision, int LastAssessmentRequest,
    SafetyCase? DecisionSource);
public sealed record CalendarCaseSource(string Id, int Version, string ProductId, string Reference, string Title,
    Deadline[] Deadlines, FollowUpObligation[]? FollowUps);
public sealed record CalendarSources(CalendarCaseSource[] Cases, Psur[] Psurs, PsurReview[] Reviews);
public interface IWorkspaceReadStore
{
    Task<CaseProcessInput[]> ReadCockpitCases(string[] products, DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);
    Task<CalendarSources> ReadCalendarSources(string[] caseProducts, string[] authorizedProducts,
        DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default);
}

public sealed record WorkspaceCount(string Key, string Label, int Count);
public sealed record IntakePoint(DateOnly From, DateOnly Through, int Count);
public sealed record CockpitKpis(int OpenCases, int PendingMedical, int PendingRegulatory, int OpenObligations,
    int UpcomingObligations, int OverdueObligations);
public sealed record CockpitCase(string Id, int Version, string Reference, string ProductId, string Title,
    string Stage, string Seriousness, CaseOutcome Outcome, DateTimeOffset SystemReceivedAt, string CurrentStep,
    string StepLabel, string ReviewState, string? AssessmentMode, string RiskGroup,
    int OpenObligations, int UpcomingObligations, int OverdueObligations);
public sealed record CockpitSnapshot(string Contract, DateTimeOffset AsOf, DateOnly From, DateOnly Through,
    string TimeZone, string? ProductId, string DateBasis, int TotalCases, CockpitKpis Kpis,
    bool RiskGroupsAvailable, string RiskBoundary, WorkspaceCount[] RiskGroups, WorkspaceCount[] ReviewStates,
    WorkspaceCount[] UrgencyClasses, WorkspaceCount[] ProcessSteps, IntakePoint[] Intake,
    string Focus, int Offset, int PageSize, int MatchingCases, CockpitCase[] Cases);
public sealed record CalendarEvent(string Id, string ObligationId, string Kind, string EntityKind, string EntityId,
    string? PsurId, int? Round, string ProductId, string[] Products, string Reference, string Title,
    int SourceVersion, string SourceField, string? OriginalTimeZone, DateTimeOffset? DueAt, DateOnly? DueDate,
    DateOnly DisplayDate, string Status, string Timing, bool IsOpen, bool IsCurrent, bool Primary, string[] Details);
public sealed record CalendarDay(DateOnly Date, int Events, int OpenEvents, int OverdueMarkers, WorkspaceCount[] Kinds);
public sealed record CalendarSnapshot(string Contract, DateTimeOffset AsOf, DateOnly Month, string TimeZone,
    string? ProductId, string Kind, string Status, DateOnly? Day, CalendarDay[] Days, int Obligations,
    int OpenObligations, int OverduePrimaryObligations, int MissingPsurDates,
    int TotalEvents, int MatchingEvents, int Offset, int PageSize, CalendarEvent[] Events);

public static class WorkspaceDates
{
    public static readonly string[] Zones = ["Europe/Berlin", "Europe/Dublin", "Europe/London", "Europe/Paris", "Europe/Rome", "UTC"];
    public static TimeZoneInfo Zone(string? id)
    {
        var name = id ?? "Europe/Berlin";
        Guard.Against(!Zones.Contains(name, StringComparer.Ordinal), "display_timezone", "Eine angebotene IANA-Anzeigezeitzone waehlen.");
        return TimeZoneInfo.FindSystemTimeZoneById(name);
    }
    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    public static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone));
    public static void RequirePeriod(DateOnly from, DateOnly through)
    {
        Guard.Against(from > through || through.DayNumber - from.DayNumber > 365 || through == DateOnly.MaxValue,
            "workspace_period", "Expliziter Zeitraum von einem bis hoechstens 366 Kalendertagen erforderlich.");
    }
}

public sealed class WorkspaceReadService(IWorkspaceReadStore store, TimeProvider clock)
{
    public const int PageSize = 25;
    public static readonly string[] CalendarKinds = ["LegalDue", "SafetyDue", "FollowUp", "PsurSubmission", "NotifiedBodyReply"];
    private static readonly (string Key, string Label)[] ReviewLabels =
    [
        ("Missing", "Ohne Assessment"), ("AiDraft", "KI-Entwurf, nicht medizinisch freigegeben"),
        ("HumanDraft", "Menschlicher Entwurf, nicht medizinisch freigegeben"),
        ("MedicalReviewed", "Aktuell medizinisch geprueft"), ("Stale", "Analyse veraltet / neuer Lauf offen"),
        ("UnknownMode", "Entwurfsmodus unbekannt")
    ];
    private static readonly (string Key, string Label)[] UrgencyLabels =
    [
        ("PublicHealthThreat", "Gefahr fuer die oeffentliche Gesundheit"),
        ("DeathOrSeriousDeterioration", "Tod oder schwere Verschlechterung (Fristenanlass)"),
        ("OtherSerious", "Sonstiges schwerwiegendes Vorkommnis"), ("Unknown", "Fristenklasse unbekannt")
    ];

    public async Task<CockpitSnapshot> Cockpit(Actor actor, string? productId, DateOnly? from, DateOnly? through,
        string? timeZone, string? focus, int offset, CancellationToken ct = default)
    {
        var products = Products(actor, productId);
        var now = clock.GetUtcNow();
        var zone = WorkspaceDates.Zone(timeZone);
        var end = through ?? WorkspaceDates.Day(now, zone);
        var start = from ?? end.AddDays(-29);
        WorkspaceDates.RequirePeriod(start, end);
        Guard.Against(offset < 0, "workspace_page", "Seitenoffset darf nicht negativ sein.");
        var sources = await store.ReadCockpitCases(products, WorkspaceDates.Start(start, zone),
            WorkspaceDates.Start(end.AddDays(1), zone), ct);
        return ProjectCockpit(sources, actor, productId, start, end, zone, now, focus ?? "all", offset);
    }

    public static CockpitSnapshot ProjectCockpit(CaseProcessInput[] sources, Actor actor, string? productId,
        DateOnly from, DateOnly through, TimeZoneInfo zone, DateTimeOffset now, string focus = "all", int offset = 0)
    {
        var products = Products(actor, productId);
        WorkspaceDates.RequirePeriod(from, through);
        Guard.Against(offset < 0, "workspace_page", "Seitenoffset darf nicht negativ sein.");
        var selected = sources.Where(s => products.Contains(s.Case.ProductId) &&
            WorkspaceDates.Day(s.Case.SystemReceivedAt, zone) >= from &&
            WorkspaceDates.Day(s.Case.SystemReceivedAt, zone) <= through).ToArray();
        var projected = selected.Select(s =>
        {
            var c = s.Case;
            var process = CaseService.ProjectProcess(s);
            var review = c.CurrentAssessment is null ? "Missing" :
                process.Steps.Single(p => p.Id == "analysis").State != "completed" ? "Stale" :
                process.Steps.Single(p => p.Id == "medical").State == "completed" ? "MedicalReviewed" :
                c.CurrentAssessment.Mode == "Human" ? "HumanDraft" :
                c.CurrentAssessment.Mode is "Replay" or "Live" ? "AiDraft" : "UnknownMode";
            var obligations = CaseObligations(c);
            return new CockpitCase(c.Id, c.Version, c.Reference, c.ProductId, c.Title, c.Stage.ToString(),
                c.Seriousness.ToString(), process.Outcome, c.SystemReceivedAt, process.CurrentStep,
                process.Steps.Single(p => p.Id == process.CurrentStep).Label, review, c.CurrentAssessment?.Mode,
                c.CurrentAssessment is null ? "Unassessed" : "Unclassified",
                obligations.Length,
                obligations.Count(o => o.Due >= now && o.Safety <= now.AddHours(72)),
                obligations.Count(o => o.Due < now));
        }).ToArray();
        var allObligations = selected.SelectMany(s => CaseObligations(s.Case)).DistinctBy(o => o.Id).ToArray();
        var kpis = new CockpitKpis(projected.Count(c => c.Stage != "Closed"),
            projected.Count(c => c.CurrentStep == "medical"), projected.Count(c => c.CurrentStep == "regulatory"),
            allObligations.Length, allObligations.Count(o => o.Due >= now && o.Safety <= now.AddHours(72)),
            allObligations.Count(o => o.Due < now));
        var intake = new List<IntakePoint>();
        var daily = through.DayNumber - from.DayNumber < 62;
        for (var day = from; day <= through;)
        {
            var end = daily ? day : new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
            if (end > through) end = through;
            intake.Add(new(day, end, projected.Count(c => WorkspaceDates.Day(c.SystemReceivedAt, zone) >= day &&
                WorkspaceDates.Day(c.SystemReceivedAt, zone) <= end)));
            day = end.AddDays(1);
        }
        var risk = new[]
        {
            new WorkspaceCount("Unassessed", "Unbewertet: kein Assessment", projected.Count(c => c.RiskGroup == "Unassessed")),
            new WorkspaceCount("Unclassified", "Assessment vorhanden, Risikogruppe nicht strukturiert", projected.Count(c => c.RiskGroup == "Unclassified"))
        };
        var reviews = ReviewLabels.Select(r => new WorkspaceCount(r.Key, r.Label, projected.Count(c => c.ReviewState == r.Key))).ToArray();
        var urgency = UrgencyLabels.Select(r => new WorkspaceCount(r.Key, r.Label, projected.Count(c =>
            (UrgencyLabels.Any(u => u.Key == c.Seriousness) ? c.Seriousness : "Unknown") == r.Key))).ToArray();
        var processes = CaseProcess.Definition.Select(step =>
            new WorkspaceCount(step.Id, step.Label, projected.Count(c => c.CurrentStep == step.Id))).ToArray();
        IEnumerable<CockpitCase> matching = focus switch
        {
            "all" => projected,
            "open" => projected.Where(c => c.Stage != "Closed"),
            "medical" => projected.Where(c => c.CurrentStep == "medical"),
            "regulatory" => projected.Where(c => c.CurrentStep == "regulatory"),
            "obligations" => projected.Where(c => c.OpenObligations > 0),
            "upcoming" => projected.Where(c => c.UpcomingObligations > 0),
            "overdue" => projected.Where(c => c.OverdueObligations > 0),
            _ => Focus(projected, focus, risk, reviews, urgency, intake.ToArray(), zone)
        };
        var matches = matching.OrderByDescending(c => c.OverdueObligations).ThenByDescending(c => c.SystemReceivedAt)
            .ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        return new("cohors-cockpit-1", now, from, through, zone.Id, productId,
            "SystemReceivedAt in Anzeigezeitzone: eine Fallakte einmal bei Anlage, nicht alle Eingangssignale oder Ereignisraten.",
            projected.Length, kpis, false,
            "Kein strukturiertes Risikogruppenfeld in den gespeicherten Assessments. Wahrscheinlichkeit, Fristenklasse und Patientenausgang werden nicht in eigene Risikogruppen umgedeutet.",
            risk, reviews, urgency, processes, intake.ToArray(), focus, offset, PageSize, matches.Length,
            matches.Skip(offset).Take(PageSize).ToArray());
    }

    private static IEnumerable<CockpitCase> Focus(CockpitCase[] rows, string focus, WorkspaceCount[] risk,
        WorkspaceCount[] reviews, WorkspaceCount[] urgency, IntakePoint[] intake, TimeZoneInfo zone)
    {
        var parts = focus.Split(':', 2);
        if (parts.Length == 2)
        {
            var key = parts[1];
            if (parts[0] == "risk" && risk.Any(r => r.Key == key)) return rows.Where(c => c.RiskGroup == key);
            if (parts[0] == "review" && reviews.Any(r => r.Key == key)) return rows.Where(c => c.ReviewState == key);
            if (parts[0] == "urgency" && urgency.Any(r => r.Key == key))
                return rows.Where(c => (UrgencyLabels.Any(u => u.Key == c.Seriousness) ? c.Seriousness : "Unknown") == key);
            if (parts[0] == "process" && CaseProcess.Definition.Any(step => step.Id == key)) return rows.Where(c => c.CurrentStep == key);
            if (parts[0] == "intake" && intake.FirstOrDefault(p => p.From.ToString("yyyy-MM-dd") == key) is { } point)
                return rows.Where(c => WorkspaceDates.Day(c.SystemReceivedAt, zone) >= point.From && WorkspaceDates.Day(c.SystemReceivedAt, zone) <= point.Through);
        }
        throw new DomainException("workspace_focus", "Unbekannter Drilldown; eine ausgewiesene Kennzahl oder Grafikgruppe waehlen.");
    }

    public async Task<CalendarSnapshot> Calendar(Actor actor, string? productId, DateOnly? month, string? timeZone,
        string? kind, string? status, DateOnly? day, int offset, CancellationToken ct = default)
    {
        var products = Products(actor, productId);
        var now = clock.GetUtcNow();
        var zone = WorkspaceDates.Zone(timeZone);
        var requested = month ?? WorkspaceDates.Day(now, zone);
        var first = new DateOnly(requested.Year, requested.Month, 1);
        Guard.Against(first.Year == 9999 && first.Month == 12, "workspace_month", "Monatsende liegt ausserhalb des unterstuetzten Datumsbereichs.");
        var sources = await store.ReadCalendarSources(products, actor.Products, WorkspaceDates.Start(first, zone),
            WorkspaceDates.Start(first.AddMonths(1), zone), ct);
        return ProjectCalendar(sources, actor, productId, first, zone, now, kind ?? "all", status ?? "all", day, offset);
    }

    public static CalendarSnapshot ProjectCalendar(CalendarSources sources, Actor actor, string? productId,
        DateOnly month, TimeZoneInfo zone, DateTimeOffset now, string kind = "all", string status = "all",
        DateOnly? day = null, int offset = 0)
    {
        var products = Products(actor, productId);
        var first = new DateOnly(month.Year, month.Month, 1);
        var until = first.AddMonths(1);
        Guard.Against(kind != "all" && !CalendarKinds.Contains(kind) || status is not ("all" or "open") ||
            offset < 0 || day is { } chosen && (chosen < first || chosen >= until),
            "calendar_filter", "Kalenderfilter, Tag oder Seite sind ungueltig.");
        var events = new List<CalendarEvent>();
        foreach (var c in sources.Cases.Where(c => products.Contains(c.ProductId)))
        {
            foreach (var d in c.Deadlines)
            {
                var id = $"case:{c.Id}:deadline:{d.Id}";
                string[] details = [$"Ausloeser {d.TriggerAt:O}; {d.Jurisdiction}; Kalender {d.CalendarVersion}; Regel {d.RuleVersion}.", .. d.Calculation];
                Add(id, "LegalDue", "case", c.Id, null, null, c.ProductId, [c.ProductId], c.Reference, c.Title,
                    c.Version, $"Deadlines/{d.Id}/LegalDue", d.TimeZone, d.LegalDue, null, d.Status, d.Status == "Open", true, true, details);
                Add(id, "SafetyDue", "case", c.Id, null, null, c.ProductId, [c.ProductId], c.Reference, c.Title,
                    c.Version, $"Deadlines/{d.Id}/SafetyDue", d.TimeZone, d.SafetyDue, null, d.Status, d.Status == "Open", true, false, details);
            }
            foreach (var f in c.FollowUps ?? [])
            {
                var id = $"case:{c.Id}:followup:{f.Id}";
                string[] details = [$"Bestaetigter Folgetag {f.Schedule.ReportDate:yyyy-MM-dd}; {f.Jurisdiction}; Kalender {f.Schedule.CalendarVersion}; Regel {f.Schedule.RuleVersion}.",
                    $"Vorbericht v{f.SourceReportVersion}; Paket {f.SourceReportHash}; simulierte Quittung {f.SourceReceiptReference}.",
                    "Kein Agentenvorschlag: gespeicherte Folgepflicht nach simuliertem Eingang des freigegebenen Vorberichts."];
                Add(id, "FollowUp", "case", c.Id, null, null, c.ProductId, [c.ProductId], c.Reference, c.Title,
                    c.Version, $"FollowUps/{f.Id}/Schedule/DueAt", f.Schedule.TimeZone, f.Schedule.DueAt, null, f.Status, f.Status == "Open", true, true, details);
                Add(id, "SafetyDue", "case", c.Id, null, null, c.ProductId, [c.ProductId], c.Reference, c.Title,
                    c.Version, $"FollowUps/{f.Id}/Schedule/SafetyDue", f.Schedule.TimeZone, f.Schedule.SafetyDue, null, f.Status, f.Status == "Open", true, false, details);
            }
        }
        var psurs = sources.Psurs.Where(p => actor.Products.Contains(p.ProductId) && PsurGroupRules.CanRead(p.Snapshot, actor) &&
            (productId is null || PsurGroupRules.Products(p.Snapshot).Contains(productId))).ToArray();
        foreach (var group in psurs.Where(p => p.Snapshot.Series is not null)
            .GroupBy(p => (p.Snapshot.Series!.Reference, p.Snapshot.Start, p.Snapshot.End)))
        {
            var latest = group.Max(p => p.Snapshot.Series!.ReportVersion);
            Guard.Against(group.Count(p => p.Snapshot.Series!.ReportVersion == latest) != 1,
                "calendar_psur_version", "PSUR-Serienfassung ist nicht eindeutig; keine beliebige Terminzuordnung.", 409);
            foreach (var p in group)
            {
                var series = p.Snapshot.Series!;
                var current = series.ReportVersion == latest;
                Add($"psur:{series.Reference}:{p.Snapshot.Start:yyyy-MM-dd}:{p.Snapshot.End:yyyy-MM-dd}",
                    "PsurSubmission", "psur", p.Id, p.Id, null, p.ProductId, PsurGroupRules.Products(p.Snapshot), series.Reference,
                    $"PSUR Nr. {series.ReportVersion}, {p.Snapshot.Start:yyyy-MM-dd} bis {p.Snapshot.End:yyyy-MM-dd}",
                    p.Version, "Snapshot/Series/SubmissionDate", null, null, series.SubmissionDate, p.Status,
                    current && p.NotifiedBodyReceipt is null, current, true,
                    [series.ScheduleAgreement, current ? "Neueste gespeicherte Fassung dieser Serie/Periode." : "Aeltere Fassung; kein zusaetzlicher offener Pflichtzaehler.",
                        "Reiner vereinbarter Kalendertag ohne Uhrzeit/Zeitzone. Tagesvergleich in Anzeigezeitzone, keine neu berechnete gesetzliche Frist.",
                        p.NotifiedBodyReceipt is null ? "Kein simulierter Eingangsbeleg dieser Fassung." : $"Nur simulierte technische Annahme: {p.NotifiedBodyReceipt}. Keine BS-Inhaltsfreigabe."]);
            }
        }
        foreach (var r in sources.Reviews.Where(r => actor.Products.Contains(r.ProductId) && r.Products.All(actor.Products.Contains) &&
            (productId is null || r.Products.Contains(productId))))
        {
            PsurReviewRules.RequireScope(r, actor);
            foreach (var q in r.Questions)
            {
                var submission = r.Submissions.SingleOrDefault(s => s.Round == q.Round);
                var current = q.Round == r.Questions[^1].Round;
                var open = current && submission is null && r.Status is "QuestionOpen" or "ReplyDraft" or "MedicalApproved" or "ReplyApproved";
                Add($"psur-review:{r.Id}:{q.Round}", "NotifiedBodyReply", "psurReview", r.Id,
                    r.OriginalReport.Source.Id, q.Round, r.ProductId, r.Products, r.Reference, $"BS-Rueckfrage {q.Round}",
                    r.Version, $"Questions/{q.Round}/DueAt", "UTC", q.DueAt, null,
                    submission is null || current ? r.Status : "ReplySubmitted", open, current, true,
                    [q.ScheduleBasis, $"Explizit vereinbarter UTC-Termin; erfasst {q.RecordedAt:O}. Keine pauschale gesetzliche Antwortfrist.",
                        submission is null ? "Keine simulierte Antwortquittung dieser Runde." :
                            $"Antwort simuliert quittiert: {submission.SimulatedReceipt}; Frage/Bewertung dadurch nicht automatisch geschlossen."]);
            }
        }
        var filtered = events.Where(e => kind == "all" || e.Kind == kind).Where(e => status == "all" || e.IsOpen).ToArray();
        var days = Enumerable.Range(0, until.DayNumber - first.DayNumber).Select(i =>
        {
            var date = first.AddDays(i);
            var markers = filtered.Where(e => e.DisplayDate == date).ToArray();
            return new CalendarDay(date, markers.Length, markers.Count(e => e.IsOpen),
                markers.Count(e => e.Timing == "Overdue"), markers.GroupBy(e => e.Kind).Select(g => new WorkspaceCount(g.Key, g.Key, g.Count())).ToArray());
        }).ToArray();
        var matching = filtered.Where(e => day is null || e.DisplayDate == day)
            .OrderBy(e => e.DisplayDate).ThenBy(e => e.DueAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        return new("cohors-deadline-calendar-1", now, first, zone.Id, productId, kind, status, day, days,
            filtered.Select(e => e.ObligationId).Distinct().Count(),
            filtered.Where(e => e.IsOpen).Select(e => e.ObligationId).Distinct().Count(),
            filtered.Where(e => e.Primary && e.Timing == "Overdue").Select(e => e.ObligationId).Distinct().Count(),
            psurs.Count(p => p.Snapshot.Series is null), filtered.Length, matching.Length, offset, PageSize,
            matching.Skip(offset).Take(PageSize).ToArray());

        void Add(string obligation, string type, string entityKind, string entityId, string? psurId, int? round,
            string product, string[] memberProducts, string reference, string title, int version, string sourceField,
            string? originalZone, DateTimeOffset? due, DateOnly? dueDate, string sourceStatus, bool open, bool current, bool primary, string[] details)
        {
            var date = dueDate ?? WorkspaceDates.Day(due!.Value, zone);
            if (date < first || date >= until) return;
            var today = WorkspaceDates.Day(now, zone);
            var timing = !current ? "Historical" : !open ? "RecordedClosed" :
                due is { } instant ? instant < now ? "Overdue" : instant <= now.AddHours(72) ? "Upcoming" : "Later" :
                date < today ? "Overdue" : date == today ? "DueToday" : date <= today.AddDays(3) ? "Upcoming" : "Later";
            events.Add(new($"{entityKind}:{entityId}:{sourceField}", obligation, type, entityKind, entityId, psurId, round,
                product, memberProducts, reference, title, version, sourceField, originalZone, due, dueDate,
                date, sourceStatus, timing, open, current, primary, details));
        }
    }

    private static string[] Products(Actor actor, string? product)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);
        if (product is not null) actor.RequireProduct(product);
        return product is null ? actor.Products.Distinct(StringComparer.Ordinal).ToArray() : [product];
    }
    private static (string Id, DateTimeOffset Due, DateTimeOffset Safety)[] CaseObligations(SafetyCase c) =>
        c.Deadlines.Where(d => d.Status == "Open").Select(d => ($"case:{c.Id}:deadline:{d.Id}", d.LegalDue, d.SafetyDue))
            .Concat((c.FollowUps ?? []).Where(f => f.Status == "Open").Select(f => ($"case:{c.Id}:followup:{f.Id}", f.Schedule.DueAt, f.Schedule.SafetyDue)))
            .DistinctBy(o => o.Item1).ToArray();
}
