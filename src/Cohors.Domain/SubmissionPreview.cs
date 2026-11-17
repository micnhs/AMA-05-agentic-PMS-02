namespace Cohors.Domain;

public sealed record SubmissionPreview(string CaseId, string CaseReference, int CaseVersion, string ProductId,
    string FrozenJsonHash, ReportDraft Report, Evidence[] Evidence, SourceReference[] Originals, Approval[] Approvals,
    string Channel, string Environment, bool Simulated);

public static class SubmissionPreviews
{
    public static SubmissionPreview Read(SafetyCase c, SafetyCase preparedFrom, Actor actor)
    {
        ReportRules.RequireSendable(c, actor);
        var current = c.CurrentReport!;
        var report = Canonical.Read<ReportDraft>(current.FrozenJson!);
        Guard.Against(report.Preparation is null, "dispatch_target_missing",
            "Das Altpaket besitzt kein eingefrorenes logisches Zielprofil. Einen neuen nachvollziehbaren Entwurf getrennt freigeben; kein heutiges Profil hinzuerfinden.", 409);
        ReportTargets.RequireBound(report);
        Guard.Against(preparedFrom.Id != c.Id || preparedFrom.ProductId != c.ProductId ||
            report.Preparation!.CaseSource != new VersionReference(preparedFrom.Id, preparedFrom.Version, Canonical.Hash(preparedFrom)),
            "dispatch_source", "Vorbereitungsfassung und eingefrorener Quellhash stimmen nicht ueberein.", 409);
        var attachments = c.Evidence.Select(e => e.Hash)
            .Concat(report.Mir is { } mir ? [mir.XmlHash] : []).Order(StringComparer.Ordinal).ToArray();
        Guard.Against(Canonical.Hash(c.Evidence) != report.EvidenceHash || Canonical.Hash(preparedFrom.Evidence) != report.EvidenceHash ||
            !attachments.SequenceEqual(report.AttachmentHashes.Order(StringComparer.Ordinal)),
            "dispatch_evidence", "Evidenz-/Anhangsbezug stimmt nicht mit dem eingefrorenen Paket ueberein.", 409);
        return new(c.Id, c.Reference, c.Version, c.ProductId, Canonical.HashText(current.FrozenJson!),
            report, preparedFrom.Evidence, preparedFrom.Sources,
            c.Approvals.Where(a => a.ScopeHash == report.ScopeHash && a.Role is Roles.Medical or Roles.Regulatory).ToArray(),
            "CohorsNativeSimulator", "LocalSimulator", true);
    }
}
