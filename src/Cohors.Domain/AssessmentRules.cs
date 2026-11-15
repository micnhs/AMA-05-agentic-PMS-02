namespace Cohors.Domain;

public static class AssessmentRules
{
    public static readonly string[] RequiredSections =
        ["context", "facts", "technical", "medical", "causality", "risk", "benefitRisk",
         "regulatory", "options", "information", "conclusion", "review"];

    public static QualityFinding[] Check(AssessmentContent content, IReadOnlyList<Evidence> evidence)
    {
        var findings = new List<QualityFinding>();
        if (content is null || content.Sections is null || content.Claims is null || content.Recommendations is null || content.Uncertainties is null ||
            content.Sections.Any(s => s is null || s.Text is null || s.Key is null || s.ClaimIds is null) ||
            content.Claims.Any(c => c is null || c.Text is null) ||
            content.Recommendations.Any(r => r is null || r.Rationale is null || r.Limitation is null || r.ClaimIds is null))
            return [new("assessment_schema", "Critical", "Strukturierte Pflichtfelder fehlen.", "Vollstaendiges Assessment statt unpruefbarem Freitext liefern.", null)];
        if (content.Claims.Length == 0)
            findings.Add(new("claims_missing", "Critical", "Keine tragenden belegten Aussagen vorhanden.",
                "Medizinisch-technische Aussagen mit konkreten Fundstellen verknuepfen.", null));
        if (content.Recommendations.Length == 0)
            findings.Add(new("recommendations_missing", "Critical", "Begruendete Handlungsempfehlung fehlt.",
                "Konkrete Empfehlung mit Evidenzbezug, Grenzen und menschlicher Verantwortlichkeit ergaenzen.", null));
        if (content.Claims.Select(c => c.Id).Distinct().Count() != content.Claims.Length ||
            content.Claims.Any(c => string.IsNullOrWhiteSpace(c.Id)))
            findings.Add(new("claim_identity", "Critical", "Claim-Referenzen sind nicht eindeutig.",
                "Eindeutige, nicht leere Claim-IDs verwenden.", null));
        var known = content.Claims.Select(c => c.Id).ToHashSet();
        string[] groundedSections = ["facts", "technical", "medical", "causality", "risk", "benefitRisk", "regulatory", "options", "information", "conclusion"];
        foreach (var key in RequiredSections)
            if (!content.Sections.Any(s => s.Key == key && s.Text.Length >= 30))
                findings.Add(new("section_missing", "Critical", $"Abschnitt {key} fehlt oder ist nicht begruendet.",
                    "Fachlichen Abschnitt vervollstaendigen.", null));
        foreach (var section in content.Sections)
            if (section.ClaimIds is null || section.ClaimIds.Any(id => !known.Contains(id)) ||
                groundedSections.Contains(section.Key) && section.ClaimIds.Length == 0)
                findings.Add(new("section_claims", "Critical", $"Abschnitt {section.Key} hat keine aufloesbaren tragenden Belege.",
                    "Fachliche Abschnitte an vorhandene verifizierbare Claims binden; Metadaten benoetigen keine kuenstlichen Belege.", null));
        foreach (var claim in content.Claims)
        {
            if (claim.Kind is not ("Observed" or "Hypothesis" or "Inference"))
                findings.Add(new("claim_kind", "Critical", "Aussagetyp ist ungueltig.", "Fakt, Hypothese oder Schlussfolgerung trennen.", claim.Id));
            if (claim.Citations is null || claim.Citations.Length == 0)
            {
                findings.Add(new("unsupported_claim", "Critical", "Tragende Aussage ohne Fundstelle.", "Beleg ergaenzen oder Aussage entfernen.", claim.Id));
                continue;
            }
            foreach (var citation in claim.Citations)
            {
                if (!EvidenceRules.CitationMatches(citation, evidence))
                    findings.Add(new("invalid_citation", "Critical", "Fundstelle, Hash oder Originalzitat ist nicht verifizierbar.",
                        "Originalquelle pruefen; kein Ersatz durch erfundene Belege.", claim.Id));
            }
        }
        foreach (var recommendation in content.Recommendations)
            if (recommendation.Rationale.Length < 30 || recommendation.Limitation.Length < 15 ||
                recommendation.ClaimIds.Length == 0 || recommendation.ClaimIds.Any(id => !known.Contains(id)))
                findings.Add(new("recommendation_basis", "Critical", "Handlungsempfehlung ist nicht ausreichend begruendet.",
                    "Evidenz, Grenzen und Verantwortlichkeit ergaenzen.", null));
        if (!content.Uncertainties.Any(s => !string.IsNullOrWhiteSpace(s)) || content.Probability is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(content.ProbabilityBasis))
            findings.Add(new("false_certainty", "Critical", "Unsicherheit bzw. Wahrscheinlichkeitsbasis fehlt.",
                "Fehlende Wahrscheinlichkeit ist nicht Nullrisiko.", null));
        if (!evidence.Any(e => e.Kind == "ClinicalFollowUp"))
            findings.Add(new("clinical_gap", "Major", "Revisions-/Explantatbefund und mikrobiologische Abklaerung fehlen.",
                "Gezielte, menschlich genehmigte simulierte Nachforderung; Frist nicht pausieren.", null));
        return findings.ToArray();
    }

    public static void RequireReviewable(Assessment assessment, bool preliminary, string? disposition)
    {
        Guard.Against(assessment.Quality is null, "qa_required", "Eine nachvollziehbare Qualitaetssicht fehlt.");
        Guard.Against(assessment.Quality!.AssessmentVersion != assessment.Version, "qa_stale", "QA gilt fuer eine andere Version.");
        Guard.Against(assessment.Quality.Findings.Any(f => f.Severity == "Critical"), "critical_findings",
            "Unbelegte Aussagen muessen korrigiert werden; Dringlichkeit legitimiert keine falschen Fakten.");
        var unresolved = assessment.Quality.Findings.Any(f => f.Severity == "Major");
        Guard.Against(unresolved && (!preliminary || string.IsNullOrWhiteSpace(disposition)),
            "open_findings", "Offene Untersuchungsfragen brauchen eine begruendete vorlaeufige menschliche Disposition.");
    }
}
