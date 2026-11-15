namespace Cohors.Domain;

public static class ReportRules
{
    public static string Scope(Assessment assessment, ReportDraft report)
    {
        var content = new
        {
        assessment = new { assessment.Version, assessment.Content, assessment.EvidenceHash, assessment.Quality },
        report.Version, report.AssessmentVersion, report.Kind, report.Jurisdiction, report.Recipient,
        report.Language, report.FormVersion, report.SchemaVersion, report.Narrative, report.Preliminary,
        report.OpenQuestions, report.RuleVersion, report.EvidenceHash, report.AttachmentHashes, report.ObligationIds
        };
        var reportScope = report.Mir is null ? Canonical.Hash(content) : Canonical.Hash(new { content, report.Mir });
        return report.FollowUpObligationIds is null ? reportScope : Canonical.Hash(new { reportScope, report.FollowUpObligationIds });
    }

    public static Approval ApproveMedical(SafetyCase c, Actor actor, string rationale,
        string? findingDisposition, DateTimeOffset now)
    {
        actor.RequireHuman(Roles.Medical);
        actor.RequireProduct(c.ProductId);
        var report = c.CurrentReport ?? throw new DomainException("report_required", "Zuerst einen konkreten Berichtsentwurf erstellen.");
        var assessment = c.CurrentAssessment ?? throw new DomainException("assessment_required", "Assessment fehlt.");
        Guard.Against(report.State != ReportState.Draft, "report_state", "Diese Version ist nicht mehr im Entwurf.");
        Guard.Against(report.ScopeHash != Scope(assessment, report), "scope_changed", "Der Freigabeinhalt hat sich geaendert.", 409);
        AssessmentRules.RequireReviewable(assessment, report.Preliminary, findingDisposition);
        MirSchemas.RequireValid(report);
        FollowUpRules.Covered(c, report);
        return new(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, Roles.Medical, report.ScopeHash,
            now, "Medizinisch-technische Bewertung und konkreter Berichtsinhalt freigegeben",
            Guard.Text(rationale, "Begruendung", 20), findingDisposition);
    }

    public static Approval ApproveRegulatory(SafetyCase c, Actor actor, string rationale, DateTimeOffset now)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(c.ProductId);
        var report = c.CurrentReport ?? throw new DomainException("report_required", "Bericht fehlt.");
        Guard.Against(report.State != ReportState.MedicalApproved, "medical_required", "Gueltige medizinische Freigabe fehlt.");
        Guard.Against(report.ScopeHash != Scope(c.CurrentAssessment!, report), "scope_changed", "Paket wurde geaendert.", 409);
        MirSchemas.RequireValid(report);
        FollowUpRules.Covered(c, report);
        var medical = c.Approvals.LastOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == report.ScopeHash)
            ?? throw new DomainException("medical_required", "Medizinische Signatur fehlt.");
        Guard.Against(medical.ActorId == actor.Id, "four_eyes", "Zwei getrennte menschliche Identitaeten sind erforderlich.", 403);
        return new(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, Roles.Regulatory, report.ScopeHash,
            now, "Regulatorischer Scope und ausschliesslich simulierter Versand freigegeben",
            Guard.Text(rationale, "Begruendung", 20), null);
    }

    public static void RequireSendable(SafetyCase c, Actor actor)
    {
        actor.RequireHuman(Roles.Regulatory);
        actor.RequireProduct(c.ProductId);
        var report = c.CurrentReport ?? throw new DomainException("report_required", "Bericht fehlt.");
        RequireNoUnknown(c, report.Jurisdiction);
        CoveredObligations(c, report);
        var followUps = FollowUpRules.Covered(c, report);
        Guard.Against((c.FollowUps ?? []).Any(f => followUps.Contains(f.Id) && f.Status != "Open"),
            "followup_completed", "Gebundene Folgepflicht wurde bereits erledigt; aktuellen Berichtsscope neu pruefen.", 409);
        MirSchemas.RequireValid(report);
        Guard.Against(report.State != ReportState.Frozen, "not_frozen", "Nur ein eingefrorenes Paket darf an den Simulator gehen.");
        Guard.Against(report.ScopeHash != Scope(c.CurrentAssessment!, report), "scope_changed", "Freigaben passen nicht zum aktuellen Inhalt.", 409);
        var approvals = c.Approvals.Where(a => a.ScopeHash == report.ScopeHash).ToArray();
        var medical = approvals.LastOrDefault(a => a.Role == Roles.Medical);
        var regulatory = approvals.LastOrDefault(a => a.Role == Roles.Regulatory);
        Guard.Against(medical is null || regulatory is null || medical.ActorId == regulatory.ActorId,
            "four_eyes", "Gueltige unabhaengige medizinische und regulatorische Freigaben fehlen.", 403);
        Guard.Against(c.Submissions.Any(s => s.ReportScopeHash == report.ScopeHash), "already_submitted",
            "Diese Paketversion wurde bereits uebermittelt. Bei Unknown zuerst abgleichen.", 409);
        Guard.Against(report.FrozenJson is null, "frozen_payload", "Das eingefrorene Paket fehlt.");
        var frozen = Canonical.Read<ReportDraft>(report.FrozenJson!);
        Guard.Against(frozen.ScopeHash != report.ScopeHash || Scope(c.CurrentAssessment!, frozen) != report.ScopeHash,
            "frozen_tampered", "Das eingefrorene Paket ist nicht konsistent.", 409);
    }

    public static void RequireNoUnknown(SafetyCase c, string jurisdiction)
    {
        Guard.Against(c.Submissions.Any(s => s.Status == "TransmissionUnknown" &&
            c.Reports.Any(r => r.ScopeHash == s.ReportScopeHash && r.Jurisdiction == jurisdiction)),
            "reconciliation_required", "Unklarer Versand dieser regulatorischen Fallverpflichtung muss zuerst abgeglichen werden; ein neuer Paket-Hash ist kein neuer Sendeversuch.", 409);
    }

    public static string[] CoveredObligations(SafetyCase c, ReportDraft report)
    {
        Guard.Against(report.ObligationIds is not { Length: > 0 } ||
            report.ObligationIds.Any(id => !c.Deadlines.Any(d => d.Id == id && d.Jurisdiction == report.Jurisdiction)),
            "obligation_scope", "Paket braucht explizite gueltige Verpflichtungsreferenzen. Altstaende ohne Referenz muessen menschlich abgeglichen werden.", 409);
        return report.ObligationIds!;
    }
}
