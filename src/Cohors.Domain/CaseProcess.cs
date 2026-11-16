using System.Text.Json;

namespace Cohors.Domain;

public sealed record CaseProcessStep(string Id, string Label, string State, string Role, string Detail);
public sealed record CaseProcessDefinition(string Id, string Label, string Role);
public sealed record CaseProcessContext(bool HumanAssessmentReplacesRun, bool NonReportableDecisionCurrent, bool NonReportableApproved);
public sealed record CaseOutcomeSource(string Code, string Status, string Term, string Source);
public sealed record CaseOutcome(string Code, string Status, string Term, string Source, CaseOutcomeSource[] Sources);
public sealed record CaseProcessView(string CaseId, int CaseVersion, string Reference, string Jurisdiction,
    string Recipient, string CurrentStep, string NextAction, string NextRole, string TargetTab,
    CaseProcessStep[] Steps, string[] Blockers, CaseOutcome Outcome);

public static class CaseProcess
{
    public static IReadOnlyList<CaseProcessDefinition> Definition { get; } = Array.AsReadOnly<CaseProcessDefinition>(
    [
        new("intake", "Hinweis & Frist", Roles.Pms),
        new("analysis", "Analyse", Roles.Pms),
        new("assurance", "Assurance", Roles.Medical),
        new("report", "Meldungsentwurf", Roles.Pms),
        new("medical", "Medical Safety", Roles.Medical),
        new("regulatory", "Regulatory Affairs", Roles.Regulatory),
        new("delivery", "Meldesimulator", Roles.Regulatory),
        new("followup", "Nachverfolgung", Roles.Pms)
    ]);

    private static CaseProcessStep Step(string id, string state, string detail)
    {
        var definition = Definition.Single(d => d.Id == id);
        return new(id, definition.Label, state, definition.Role, detail);
    }

    public static CaseProcessView Read(SafetyCase c, CaseProcessContext? context = null)
    {
        var assessment = c.CurrentAssessment;
        var report = c.CurrentReport;
        var blockers = new List<string>();
        var run = c.AgentRuns.LastOrDefault(r => r.Role == AgentRole.CausalityRisk);
        var humanReplacesRun = assessment?.Mode == "Human" && context?.HumanAssessmentReplacesRun == true;
        var busy = !humanReplacesRun && run?.Status is "Queued" or "Running";
        var failed = !humanReplacesRun && run?.Status is "Failed" or "Abandoned" &&
            (assessment is null || run.FinishedAt >= assessment.CreatedAt);
        var evidenceCurrent = assessment is not null && assessment.EvidenceHash == Canonical.Hash(c.Evidence);
        var analysisCurrent = evidenceCurrent && !busy && !failed;
        var qualityCurrent = analysisCurrent && assessment!.Quality?.AssessmentVersion == assessment.Version;
        var critical = qualityCurrent && assessment!.Quality!.Findings.Any(f => f.Severity == "Critical");
        var major = qualityCurrent && assessment!.Quality!.Findings.Any(f => f.Severity == "Major");
        var bound = analysisCurrent && report is not null && report.AssessmentVersion == assessment!.Version &&
            report.EvidenceHash == assessment.EvidenceHash && report.ScopeHash == ReportRules.Scope(assessment, report) &&
            report.State is not (ReportState.Superseded or ReportState.Rejected);
        var medical = bound ? c.Approvals.LastOrDefault(a => a.Role == Roles.Medical && a.ScopeHash == report!.ScopeHash) : null;
        var regulatory = bound ? c.Approvals.LastOrDefault(a => a.Role == Roles.Regulatory && a.ScopeHash == report!.ScopeHash) : null;
        var medicalComplete = medical is not null && report!.State is ReportState.MedicalApproved or ReportState.Frozen or
            ReportState.TransmissionUnknown or ReportState.SimulatedAccepted;
        var regulatoryComplete = medicalComplete && regulatory is not null && medical!.ActorId != regulatory.ActorId &&
            report!.State is ReportState.Frozen or ReportState.TransmissionUnknown or ReportState.SimulatedAccepted;
        var frozenValid = false;
        if (regulatoryComplete && report!.FrozenJson is { } json)
        {
            try
            {
                var frozen = Canonical.Read<ReportDraft>(json);
                frozenValid = frozen.ScopeHash == report.ScopeHash && ReportRules.Scope(assessment!, frozen) == report.ScopeHash;
            }
            catch (JsonException) { blockers.Add("Eingefrorenes Paket ist kein lesbares Original; Integritaet pruefen."); }
        }
        regulatoryComplete &= frozenValid;
        var mirIssue = "";
        if (bound)
        {
            try { MirSchemas.RequireValid(report!); ReportRules.CoveredObligations(c, report!); FollowUpRules.Covered(c, report!); }
            catch (DomainException ex) { mirIssue = ex.Message; }
            if (report!.ReviewReturn is not null)
                mirIssue = "Zur Korrektur an PMS zurueckgegeben: " + report.ReviewReturn?.Reason;
        }
        if (mirIssue.Length > 0) { medicalComplete = false; regulatoryComplete = false; }
        var qaComplete = qualityCurrent && !critical && (!major ||
            report?.Preliminary == true && medicalComplete && !string.IsNullOrWhiteSpace(medical?.FindingDisposition));
        var unknown = c.Submissions.Where(s => s.Status == "TransmissionUnknown").ToArray();
        var accepted = report is not null && c.Submissions.Any(s =>
            s.Status == "SimulatedAccepted" && s.ReportScopeHash == report.ScopeHash);
        var previousAccepted = c.Submissions.Any(s => s.Status == "SimulatedAccepted");
        var followUps = (c.FollowUps ?? []).Count(f => f.Status == "Open");
        var deadlines = c.Deadlines.Count(d => d.Status == "Open");
        var requests = c.InformationRequests.Count(r => r.Status != "SimulatedAnswered");
        var pendingResponses = InformationRules.PendingResponses(c);
        var investigations = (c.Investigations ?? []).Count(i => i.Status is not ("SimulatedExamined" or "Cancelled"));
        var closed = c.Stage == CaseStage.Closed && c.ClosedAt is not null && deadlines == 0 && followUps == 0 &&
            requests == 0 && pendingResponses == 0 && investigations == 0 && unknown.Length == 0;
        CaseProcessStep[] steps =
        [
            Step("intake", c.Sources.Length > 0 ? "completed" : "blocked",
                "Originalkenntnis und persistente Fristen; kein Reset durch Bearbeitung."),
            Step("analysis", analysisCurrent ? "completed" : busy ? "current" : assessment is null && !failed ? "pending" : "blocked",
                busy ? "Erneuter oder erster Analyselauf arbeitet; kein fertiger neuer Entwurf." :
                failed ? "Letzter Analyselauf fehlgeschlagen/abgebrochen; alter Entwurf ist kein neuer Erfolg." :
                assessment is null ? "KI- oder menschlicher Assessment-Entwurf fehlt." :
                !evidenceCurrent ? "Assessment passt nicht zum aktuellen Evidenzstand." : $"Assessment v{assessment.Version} ({assessment.Mode}) liegt vor; noch keine Sicherheitsfreigabe."),
            Step("assurance", qaComplete ? "completed" : analysisCurrent ? "blocked" : "pending",
                !qualityCurrent ? "Versionsgebundene Qualitaetssicht ausstehend." :
                critical ? "Kritische Aussagen/Fundstellen zuerst korrigieren." :
                major && !qaComplete ? "Major-Befunde brauchen Korrektur oder eine begruendete vorlaeufige medizinische Disposition." :
                major ? "Major-Befunde vorlaeufig menschlich disponiert, nicht als erledigt ausgegeben." : "Qualitaetssicht vorhanden; sie ersetzt keine Freigabe."),
            Step("report", bound && mirIssue.Length == 0 ? "completed" : report is null ? "pending" : "blocked",
                report is null ? "Konkretes zielgebundenes Meldungspaket fehlt." :
                !bound ? "Historischer, supersedierter oder nicht mehr passender Bericht ist keine aktuelle Freigabegrundlage." :
                mirIssue.Length > 0 ? "Pflichtangaben/Formularpruefung offen; Human Review & Meldung oeffnen." :
                $"Bericht v{report.Version} / {report.Kind} / {report.Language}; Ziel {report.Jurisdiction}." +
                (report.Jurisdiction == "GB" ? " GB-SIM-1 ist nur ein Pilotnarrativ, keine GB-MIR-Pflichtfeldpruefung." : "")),
            Step("medical", medicalComplete ? "completed" : "pending",
                medicalComplete ? "Medizinische Signatur bindet das aktuelle Paket." : "Aktuelle medizinische Pruefung und Signatur fehlen."),
            Step("regulatory", regulatoryComplete ? "completed" : "pending",
                regulatoryComplete ? "Getrennte RA-Identitaet und eingefrorenes Paket vorhanden." : "Getrennte aktuelle RA-Freigabe und unveraendertes Paket erforderlich."),
            Step("delivery", unknown.Length > 0 ? "blocked" : accepted && regulatoryComplete ? "completed" :
                previousAccepted ? "historical" : "pending",
                unknown.Length > 0 ? "TransmissionUnknown: vorhandene Korrelation abgleichen; kein neuer Sendeversuch." :
                accepted && regulatoryComplete ? "Nur simulierte technische Annahme dieses Pakets, kein Fallabschluss." :
                previousAccepted ? "Frueheres Paket simuliert empfangen; aktuelle Fassung und Ziel separat pruefen." : "Keine bestaetigte simulierte Zustellung."),
            Step("followup", closed ? "completed" : previousAccepted ? "current" : "pending",
                closed ? "Fall menschlich geschlossen; Produktbeobachtung bleibt separat." :
                $"{deadlines} offene Einzelpflicht(en), {followUps} Folgepflicht(en), {requests} Nachforderung(en), {investigations} Untersuchungsauftrag/-auftraege." +
                (pendingResponses > 0 ? $" {pendingResponses} Ruecklauf/Ruecklaeufe noch zuzuordnen oder medizinisch als Quelle zu pruefen." : "") +
                " Kein automatischer Fallabschluss.")
        ];
        var nonReportable = analysisCurrent && qualityCurrent && !critical && !major && context?.NonReportableDecisionCurrent == true;
        if (nonReportable)
        {
            steps[3] = steps[3] with { State = "not-required", Detail = "Begruendeter Nichtmeldeweg statt erfundener Meldungsfortschritt." };
            steps[4] = steps[4] with { State = "completed", Detail = "Aktuelle medizinische Nichtmeldeentscheidung ist an Assessment und Einzelpflichten gebunden." };
            steps[5] = steps[5] with { State = context!.NonReportableApproved ? "completed" : "current",
                Detail = context.NonReportableApproved ? "Nichtmeldepflicht mit getrennter RA-Identitaet bestaetigt." : "Nichtmeldeentscheidung braucht noch die getrennte RA-Freigabe." };
            steps[6] = steps[6] with { State = "not-required", Detail = "Keine simulierte Meldung aus diesem Nichtmeldeweg; neue Erkenntnisse separat pruefen." };
            if (!closed && context.NonReportableApproved) steps[7] = steps[7] with { State = "current" };
        }
        string current, action, role, tab;
        if (unknown.Length > 0) (current, action, role, tab) = ("delivery", "reconcile", Roles.Regulatory, "report");
        else if (closed) (current, action, role, tab) = ("followup", "history", Roles.Pms, "history");
        else if (!analysisCurrent) (current, action, role, tab) = ("analysis", "analysis", failed ? Roles.Medical : Roles.Pms, "analysis");
        else if (!qualityCurrent || critical) (current, action, role, tab) = ("assurance", "analysis", Roles.Medical, "analysis");
        else if (nonReportable) (current, action, role, tab) = context!.NonReportableApproved
            ? ("followup", "history", Roles.Pms, "history") : ("regulatory", "review", Roles.Regulatory, "report");
        else if (!bound) (current, action, role, tab) = ("report", "prepare", Roles.Pms, "report");
        else if (mirIssue.Length > 0) (current, action, role, tab) = ("report", "review", Roles.Pms, "report");
        else if (!medicalComplete) (current, action, role, tab) = ("medical", "review", Roles.Medical, "report");
        else if (!regulatoryComplete) (current, action, role, tab) = ("regulatory", "review", Roles.Regulatory, "report");
        else if (!accepted) (current, action, role, tab) = ("delivery", "review", Roles.Regulatory, "report");
        else (current, action, role, tab) = ("followup", requests > 0 || pendingResponses > 0 ? "information" : "review",
            Roles.Pms, requests > 0 || pendingResponses > 0 ? "information" : "report");
        for (var i = 0; i < steps.Length; i++)
            if (steps[i].Id == current && steps[i].State == "pending") steps[i] = steps[i] with { State = "current" };
        blockers.AddRange(steps.Where(s => s.State == "blocked").Select(s => s.Detail));
        if (mirIssue.Length > 0) blockers.Add(mirIssue);
        if (bound && report!.State is ReportState.Frozen or ReportState.TransmissionUnknown or ReportState.SimulatedAccepted &&
            !regulatoryComplete) blockers.Add("Signaturen oder eingefrorene Bytes passen nicht vollstaendig zur aktuellen Fassung.");
        return new(c.Id, c.Version, c.Reference, report?.Jurisdiction ?? c.Jurisdiction, report?.Recipient ?? "",
            current, action, role, tab, steps, blockers.Distinct().ToArray(), CaseOutcomes.Read(c));
    }
}

public static class CaseOutcomes
{
    public static CaseOutcome Read(SafetyCase c)
    {
        var sources = new List<CaseOutcomeSource>();
        if (c.Coding is { } coding && coding.Codes.Any(code => code.Source.Annex == "F"))
        {
            var outcomes = coding.Codes.Where(code => code.Source.Annex == "F").ToArray();
            var source = $"Menschliche Kodierung v{coding.Version} / {coding.ReleaseId}. Separate Analyse- und Paketfreigaben beachten.";
            var evidenceCurrent = coding.Evidence.Length > 0 && coding.Evidence.All(citation => EvidenceRules.CitationMatches(citation, c.Evidence));
            foreach (var code in outcomes)
            {
                var status = coding.HistoricalAsOf is not null ? "Historical" :
                    !evidenceCurrent || !code.Source.CurrentSelectable || (coding.Findings ?? []).Any(f => f.Severity is "Critical" or "Major") ? "Stale" : "Documented";
                sources.Add(new(code.Source.Code == "F02" ? "Death" : "CodedOther", status, $"{code.Source.Code}: {code.Source.Term}",
                    source + (status == "Historical" ? " Historische Release-Gueltigkeit nicht bestaetigt." :
                        status == "Stale" ? " Beleg-/Terminologiebezug erneut pruefen." : "")));
            }
        }
        foreach (var report in c.Reports.OrderByDescending(r => r.Version))
        {
            var mirCode = report.Mir?.Facts.EventClassification switch
            {
                "Death" => "Death",
                "Unanticipated serious deterioration in state of health" => "SeriousDeterioration",
                _ => null
            };
            if (mirCode is null) continue;
            var latest = report.Version == c.CurrentReport?.Version;
            var current = latest && ReportRules.HasCurrentMedicalApproval(c);
            sources.Add(new(mirCode, current ? "MedicalReviewed" : !latest || report.State == ReportState.Superseded ? "Historical" : "Unreviewed",
                report.Mir!.Facts.EventClassification!, $"Strukturierte MIR-Angabe in Bericht v{report.Version}; " +
                (current ? "aktuell medizinisch freigegeben." : "keine aktuelle medizinische Paketfreigabe bestaetigt.")));
        }
        if (sources.Count == 0)
            return new("Unknown", "Unknown", "", "Keine explizite, aktuell belegte Patientenfolge in Kodierung/MIR. Fristenklasse und KI-Hypothesen sind kein Ausgangsnachweis.", []);
        var ordered = sources.OrderBy(s => s.Status switch { "MedicalReviewed" => 0, "Documented" => 1, _ => 2 })
            .ThenBy(s => s.Code == "Death" ? 0 : 1).ToArray();
        var primary = ordered[0];
        return new(primary.Code, primary.Status, primary.Term, primary.Source, ordered);
    }

}
