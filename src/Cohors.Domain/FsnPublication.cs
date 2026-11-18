namespace Cohors.Domain;

public sealed record FsnRecipient(string Id, string Country, string Label);
public sealed record FsnFeedbackDecision(string CorrespondenceId, string OriginalHash, string Disposition, string Rationale);
public sealed record FsnFeedbackAssessment(EudamedCorrespondence Original, string Disposition, string Rationale);
public sealed record FsnPublicationPlan(string Contract, VersionReference DossierSource, int DossierRevision,
    string PackageHash, string DocumentHash, string DocumentName, string DocumentVersion, VersionReference FscaSource,
    VersionReference DeliveryLedger, string Audience, FsnRecipient[] Recipients, string Channel, string Route,
    FsnFeedbackAssessment[] Feedback, string ChangeAssessment, string RegulatoryDecision);
public sealed record FsnPublicationReview(string Id, FsnPublicationPlan Plan, string ScopeHash,
    string ReviewStatus, Approval[] Approvals, string CreatedBy, DateTimeOffset CreatedAt);
public sealed record FsnRecipientReceipt(FsnRecipient Recipient, string TechnicalRecipient, string Channel, string Status);
public sealed record FsnDistributionEvidence(string Contract, string Id, string DossierId,
    FsnPublicationReview Publication, EudamedBinding Binding, FsnRecipientReceipt[] Recipients, DateTimeOffset At, string ActorId,
    bool Simulated, string Boundary);

public static class FsnPublicationRules
{
    public const string Contract = "cohors-fsn-publication-simulator-v1";

    public static FsnContent RequireDossier(EudamedDossier value)
    {
        Guard.Against(value.Kind != EudamedDossierKind.FSN, "fsn_publication_kind",
            "Die gesonderte Veroeffentlichung gehoert ausschliesslich zu einer FSN.");
        EudamedRules.RequireFrozen(value);
        Guard.Against(!value.Package.Simulated || value.Package.Binding.Profile.Mode != EudamedMode.Simulator ||
            value.Package.Binding.Profile.Environment != EudamedEnvironment.Simulation,
            "fsn_publication_simulator", "Keine reale Veroeffentlichung oder Kommunikation; ausschliesslich lokaler Simulator.", 409);
        Guard.Against(value.Package.Content.IntendedAction is not ("Submit" or "Revision"),
            "fsn_publication_action", "Ein reiner Entwurfsauftrag oder eine Behoerdenantwort ist keine zu verteilende FSN.", 409);
        return value.Package.Content.Fsn ?? throw new DomainException("eudamed_fsn", "FSN-Inhalt fehlt.", 409);
    }

    public static EudamedCorrespondence[] Feedback(EudamedDeliveryLedger ledger)
    {
        var result = ledger.Entries.SelectMany(e => e.Correspondence).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        Guard.Against(result.Length > 64 || result.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != result.Length,
            "fsn_feedback_scope", "Die vollstaendige Korrespondenz muss eindeutig sein und innerhalb der 64-Eintraege-Pilotgrenze liegen.", 409);
        return result;
    }

    public static void RequirePlan(EudamedDossier dossier, FsnPublicationPlan plan, EudamedDeliveryLedger ledger)
    {
        var fsn = RequireDossier(dossier);
        var fsca = dossier.Package.Sources.Single(s => s.Kind == "eudamedDossier" && s.Source.Id == fsn.FscaId);
        Guard.Against(plan.Contract != Contract || plan.DossierSource.Id != dossier.Id || plan.DossierSource.Version < 1 ||
            plan.DossierSource.Version > dossier.Version || plan.DossierRevision != dossier.Revision ||
            plan.PackageHash != dossier.FrozenPackageHash || plan.DocumentHash != fsn.DocumentHash ||
            plan.DocumentName != fsn.DocumentName || plan.DocumentVersion != fsn.DocumentVersion ||
            plan.FscaSource != fsca.Source || plan.Audience != fsn.Audience ||
            ledger.Id != EudamedRules.DeliveryId(EudamedRules.ObligationKey(dossier.Package)) ||
            ledger.ProductId != dossier.ProductId ||
            plan.DeliveryLedger != new VersionReference(ledger.Id, ledger.Version, Canonical.Hash(ledger)),
            "fsn_publication_stale", "Dokument, FSCA, Paket, Korrespondenz oder Versandjournal wurden geaendert; neuer Verteilungsentwurf und neue Freigaben erforderlich.", 409);
        Guard.Against(plan.Channel is not ("SimulatedEmail" or "SimulatedPortal") ||
            plan.Route is not ("AfterAuthorityFeedback" or "UrgentAlternative"),
            "fsn_publication_route", "Expliziten simulierten Kanal und regulaeren oder dringlichen Alternativweg waehlen.");
        Guard.Text(plan.ChangeAssessment, "Bewertete Dokumentaenderungen und Sicherheitsfolgen", 100, 3999);
        Guard.Text(plan.RegulatoryDecision, "RA-Entscheidungsgrundlage einschliesslich fehlender Rueckmeldungen oder dringlicher Ausnahme", 100, 3999);
        Guard.Against(plan.Recipients is not { Length: > 0 and <= 100 } ||
            plan.Recipients.Any(r => r is null) ||
            plan.Recipients.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != plan.Recipients.Length,
            "fsn_recipients", "Ein bis 100 eindeutige synthetische Empfaenger erforderlich.");
        foreach (var recipient in plan.Recipients)
        {
            Guard.Against(recipient.Id is not { Length: >= 8 and <= 80 } ||
                !recipient.Id.StartsWith("DEMO-", StringComparison.Ordinal) ||
                recipient.Id.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-') ||
                !dossier.Package.Content.Countries.Contains(recipient.Country, StringComparer.Ordinal),
                "fsn_recipient_scope", "Nur DEMO-Kennungen ohne echte Adressen innerhalb des freigegebenen Laenderscopes.");
            Guard.Text(recipient.Label, "Synthetische Empfaengerbezeichnung", 10, 250);
            Guard.Against(!recipient.Label.StartsWith("SYNTHETISCH:", StringComparison.Ordinal),
                "fsn_recipient_synthetic", "Empfaenger ausdruecklich mit SYNTHETISCH: kennzeichnen.");
        }
        var feedback = Feedback(ledger);
        Guard.Against(plan.Feedback is null || plan.Feedback.Length != feedback.Length ||
            plan.Feedback.Any(f => f is null || f.Original is null) ||
            !plan.Feedback.Select(f => f.Original.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(feedback.Select(f => f.Id)),
            "fsn_feedback_incomplete", "Jeder bekannte Kommentar und jede Nachforderung braucht eine eigene originalgebundene Bewertung.", 409);
        foreach (var assessment in plan.Feedback)
        {
            var original = feedback.Single(f => f.Id == assessment.Original.Id);
            Guard.Against(!original.Simulated || Canonical.Hash(assessment.Original) != Canonical.Hash(original),
                "fsn_feedback_stale", "Die bewertete Originalkorrespondenz oder ihr Antwortstatus wurde geaendert.", 409);
            Guard.Against(assessment.Disposition is not ("Incorporated" or "NoChangeJustified" or "DeferredUrgent"),
                "fsn_feedback_disposition", "Uebernahme, begruendeten Verzicht oder dringlich begruendete Nachbearbeitung explizit bestimmen.");
            Guard.Text(assessment.Rationale, "Medizinisch-technische Bewertung genau dieses Kommentars", 80, 3999);
        }
        if (plan.Route == "UrgentAlternative")
            Guard.Against(!fsn.Urgent, "fsn_urgent_source", "Ein dringlicher Alternativweg verlangt bereits im freigegebenen Dokument die bestimmte Dringlichkeit.", 409);
        else
        {
            Guard.Against(fsn.ReportType != "FINAL" || ledger.Entries.Any(EudamedRules.Pending) ||
                !ledger.Entries.Any(e => e.DossierId == dossier.Id && e.Attempt.DossierRevision == dossier.Revision &&
                    e.Attempt.PackageHash == dossier.ScopeHash && e.BusinessStatus == "BusinessAccepted" &&
                    Canonical.Hash(e.Attempt.Binding) == Canonical.Hash(dossier.Package.Binding)),
                "fsn_authority_state", "Der regulaere Simulatorweg braucht die finale FSN-Fassung und ihre eigene fachliche Annahme ohne ungeklaerten Versuch; Annahme ist keine Sicherheitsfreigabe.", 409);
            Guard.Against(plan.Feedback.Any(f => f.Disposition == "DeferredUrgent" ||
                (f.Original.Kind == "InformationRequest" || f.Original.DueAt.HasValue) && f.Original.Status != "Replied"),
                "fsn_feedback_pending", "Offene Nachforderungen oder aufgeschobene Bewertungen verlangen Bearbeitung oder einen gesondert begruendeten dringlichen Alternativweg.", 409);
        }
    }

    public static void RequireFrozen(FsnPublicationReview review)
    {
        Guard.Against(review.Approvals is not { Length: 2 } || review.Approvals.Any(a => a is null),
            "fsn_publication_approval", "Genau zwei getrennte menschliche Freigaben sind erforderlich.", 409);
        var medical = review.Approvals.FirstOrDefault(a => a.Role == Roles.Medical);
        var regulatory = review.Approvals.FirstOrDefault(a => a.Role == Roles.Regulatory);
        Guard.Against(review.ReviewStatus != "Frozen" || review.ScopeHash != Canonical.Hash(review.Plan) ||
            review.Approvals.Length != 2 || medical is null || regulatory is null || medical.ActorId == regulatory.ActorId ||
            review.Approvals.Any(a => a.ScopeHash != review.ScopeHash),
            "fsn_publication_approval", "Zwei getrennte aktuelle Freigaben genau des Dokument-/Kommentar-/Empfaengerscopes sind erforderlich; fehlende Kommentare ersetzen keine Sicherheitspruefung.", 409);
    }
}
