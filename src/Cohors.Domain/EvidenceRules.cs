namespace Cohors.Domain;

public static class EvidenceRules
{
    public static bool CitationMatches(Citation citation, IReadOnlyList<Evidence> evidence)
    {
        if (citation is null) return false;
        var source = evidence.SingleOrDefault(e => e.Id == citation.EvidenceId);
        return source is not null && source.Approved && source.Hash == citation.Hash && source.Locator == citation.Locator &&
            !string.IsNullOrWhiteSpace(citation.Quote) && source.Content.Contains(citation.Quote, StringComparison.Ordinal);
    }

    public static void RequireExpectedSideEffect(ExpectedSideEffectException exception, Evidence[] evidence, string trendPolicyVersion)
    {
        Guard.Text(exception.Effect, "Konkret erwartete Nebenwirkung", 5, 300);
        Guard.Text(exception.Rationale, "Gemeinsame Ausnahmebegruendung", 40);
        Guard.Against(exception.ReferenceRate is null or < 0 || string.IsNullOrWhiteSpace(exception.Denominator) ||
            exception.TrendPolicyVersion != trendPolicyVersion || !exception.IncludedInTrendReporting,
            "expected_effect_conditions", "Quantifizierte Referenz mit Nenner und ausdruecklicher Einbeziehung in die versionierte Trendmeldung fehlen.");
        foreach (var citation in new[] { exception.ProductInformation, exception.TechnicalQuantification, exception.TrendMonitoring })
            Guard.Against(!CitationMatches(citation, evidence), "expected_effect_evidence",
                "Alle drei Ausnahmevoraussetzungen brauchen verifizierbare aktuelle Fundstellen.");
        var information = evidence.Single(e => e.Id == exception.ProductInformation.EvidenceId);
        var quantification = evidence.Single(e => e.Id == exception.TechnicalQuantification.EvidenceId);
        var trend = evidence.Single(e => e.Id == exception.TrendMonitoring.EvidenceId);
        Guard.Against(information.Kind is not ("IFU" or "SSCP") || quantification.Kind is not ("RiskFile" or "RiskQuantification" or "Specification") ||
            trend.Kind != "PMSPlan", "expected_effect_scope",
            "Dokumentierte Produktinformation, quantifizierte TD und Einbeziehung in Trendmeldung sind gemeinsam erforderlich; Expectedness allein reicht nicht.");
    }

    public static Evidence[] Effective(IEnumerable<Evidence> versions, DateTimeOffset at) =>
        versions.Where(e => e.Approved && e.ValidFrom <= at && (e.ValidTo is null || e.ValidTo > at))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(e => e.Version).First())
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();

    public static void RequireIntegrity(Evidence evidence)
    {
        Guard.Against(Canonical.HashText(evidence.Content) != evidence.Hash || evidence.ObjectKey != evidence.Hash,
            "evidence_content_hash", "SQL-Evidenzinhalt und inhaltsadressiertes Original stimmen nicht ueberein.", 409);
    }
}
