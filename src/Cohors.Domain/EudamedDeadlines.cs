namespace Cohors.Domain;

public sealed record EudamedCorrespondenceSource(EudamedDeliveryLedger Ledger, EudamedDeliveryEntry Entry,
    EudamedCorrespondence Correspondence, EudamedDossier CurrentDossier, EudamedDossier ApprovedDossier,
    EudamedInbox Inbox);
public sealed record EudamedCorrespondenceDeadline(EudamedCorrespondenceSource Source, string[] Products,
    DateTimeOffset? DueAt, bool IsOpen, string[] Issues)
{
    public string ObligationId => $"eudamed:{Source.Ledger.Id}:{Source.Correspondence.Id}";
}

public static class EudamedDeadlineRules
{
    public static bool CanRead(EudamedCorrespondenceSource source, Actor actor) =>
        EudamedRules.CanRead(source.CurrentDossier, actor) && EudamedRules.CanRead(source.ApprovedDossier, actor);

    public static EudamedCorrespondenceDeadline? Project(EudamedCorrespondenceSource source)
    {
        var (ledger, entry, correspondence, current, approved, inbox) = source;
        var attempt = entry.Attempt;
        var original = inbox.Original;
        Guard.Against(ledger.ProductId != current.ProductId || ledger.ProductId != approved.ProductId ||
            ledger.ProductId != inbox.ProductId || current.Id != entry.DossierId ||
            EudamedContracts.Bind(approved) != entry.ApprovedDossier || approved.Id != entry.DossierId ||
            approved.Revision != attempt.DossierRevision || approved.ScopeHash != attempt.PackageHash ||
            approved.ScopeHash != Canonical.Hash(approved.Package) || approved.FrozenPackageHash != approved.ScopeHash ||
            Canonical.Hash(approved.Package.Binding) != Canonical.Hash(attempt.Binding) ||
            ledger.Entries.Count(e => e.Attempt.Id == attempt.Id && Canonical.Hash(e) == Canonical.Hash(entry)) != 1 ||
            entry.Correspondence.Count(c => c.Id == correspondence.Id && Canonical.Hash(c) == Canonical.Hash(correspondence)) != 1,
            "eudamed_deadline_source", "Antworttermin ist nicht eindeutig an Versandledger und freigegebenes Original gebunden.", 409);
        Guard.Against(original is null || original.Kind is not ("Comment" or "InformationRequest" or "Evaluation") ||
            inbox.Id != correspondence.InboxId || inbox.OriginalHash != correspondence.OriginalHash ||
            Canonical.Hash(original) != correspondence.OriginalHash || original.MessageId != inbox.MessageId ||
            original.AttemptId != attempt.Id || original.CorrelationId != attempt.CorrelationId ||
            original.DossierId != entry.DossierId || original.DossierRevision != attempt.DossierRevision ||
            original.PackageHash != attempt.PackageHash || Canonical.Hash(inbox.Binding) != Canonical.Hash(attempt.Binding) ||
            original.Simulated != attempt.Binding.Simulated || correspondence.Simulated != original.Simulated ||
            correspondence.DossierRevision != original.DossierRevision || correspondence.Kind != original.Kind ||
            correspondence.Content != original.Content || correspondence.Issuer != original.Issuer ||
            correspondence.ReceivedAt != inbox.ReceivedAt,
            "eudamed_deadline_original", "Antworttermin und unveraenderter korrelierter Eingang stimmen nicht ueberein.", 409);
        var received = original!;
        // An undated comment/evaluation alone does not establish a response deadline.
        if (received.Kind != "InformationRequest" && received.DueAt is null && correspondence.DueAt is null) return null;
        var issues = new List<string>();
        if (received.DueAt is null)
            issues.Add("Keine ausdrueckliche Antwortfrist im Originaleingang; RA muss den Termin klaeren. Keine Pauschalfrist.");
        if (received.DueAt != correspondence.DueAt)
            issues.Add("Gespeicherte Antwortfrist widerspricht dem Originaleingang; RA muss die Abweichung klaeren. Kein Ersatztermin.");
        if (correspondence.OwnerRole != Roles.Regulatory)
            issues.Add("Die gespeicherte Verantwortung ist nicht Regulatory Affairs; RA muss die Zuordnung klaeren.");
        var replied = correspondence.Status == "Replied" && correspondence.ReplyDossierId is { } reply &&
            ledger.Entries.Any(e => e.DossierId == reply && e.IntendedAction == "Reply" &&
                e.Responses.Any(r => r.AttemptId == e.Attempt.Id && r.Applied && r.Status == "BusinessAccepted"));
        if (correspondence.Status != "Open" && !replied || correspondence.Status == "Open" && correspondence.ReplyDossierId is not null)
            issues.Add("Der Abschluss ist nicht durch eine angewandte fachliche Reply-Annahme belegt; RA muss den Status klaeren.");
        var products = current.AccessProducts.Concat(approved.AccessProducts)
            .Concat(EudamedRules.AccessProducts(current.Package)).Concat(EudamedRules.AccessProducts(approved.Package))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(source, products, issues.Count == 0 ? received.DueAt : null, !replied, issues.ToArray());
    }

    public static EudamedCorrespondenceAlert? Alert(EudamedCorrespondenceDeadline deadline, DateTimeOffset now)
    {
        if (!deadline.IsOpen || deadline.Issues.Length == 0 && deadline.DueAt >= now) return null;
        var source = deadline.Source;
        var correspondence = source.Correspondence;
        var attempt = source.Entry.Attempt;
        var code = deadline.Issues.Length == 0 ? "Overdue" : "ScheduleClarification";
        var message = code == "Overdue"
            ? "Originalgebundener EUDAMED-Antworttermin ueberschritten; Regulatory Affairs und Stellvertretung eskalieren. Keine neue MDR-Initialfrist und keine Verschiebung bestehender LegalDue."
            : string.Join(" ", deadline.Issues);
        var id = Canonical.Hash(new { ledgerId = source.Ledger.Id, attemptId = attempt.Id, correspondenceId = correspondence.Id,
            correspondence.OriginalHash, code, deadline.Issues });
        return new(id, correspondence.Id, correspondence.InboxId, correspondence.OriginalHash,
            source.Entry.DossierId, correspondence.DossierRevision, attempt.Id, attempt.PackageHash,
            Canonical.Hash(attempt.Binding), deadline.Products, Roles.Regulatory, code, now, message);
    }
}
