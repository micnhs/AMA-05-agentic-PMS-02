namespace Cohors.Domain;

public sealed record ManufacturerProfile(string Name, string Srn, string Country, string? AuthorizedRepresentative);
public sealed record PrrcAssignment(string PrimaryRole, string DeputyRole, string ResponsibilityDocument,
    string EscalationReference, string QualificationBoundary);
public sealed record DeviceRegulatoryProfile(string BasicUdi, string UdiDi, string RiskClass, bool Implantable,
    bool Legacy, bool Article18Applicable, string Article18Rationale, string CertificateId,
    DateOnly CertificationDate, string NotifiedBodyName, string NotifiedBodyNumber, string NotifiedBodyContact);
public sealed record ProductGovernance(string Id, int Version, string ProductId, ManufacturerProfile Manufacturer,
    DeviceRegulatoryProfile Device, PrrcAssignment Prrc, string PmsPlanId, string PsurReference,
    DateOnly FirstPeriodStart, string ScheduleAgreement, DateOnly FirstSubmissionDate,
    string[] DocumentIds, string LifecyclePolicy, bool Synthetic);

public static class GovernanceRules
{
    public static void RequireValid(ProductGovernance profile, Device device)
    {
        Guard.Against(!profile.Synthetic || profile.ProductId != device.Id || profile.Id != device.Id ||
            profile.Device.BasicUdi != device.BasicUdi, "governance_product", "Produkt-/Basic-UDI-Zuordnung der synthetischen Akte ist inkonsistent.");
        Guard.Against(profile.Prrc.PrimaryRole == profile.Prrc.DeputyRole ||
            string.IsNullOrWhiteSpace(profile.Prrc.ResponsibilityDocument) || string.IsNullOrWhiteSpace(profile.Prrc.EscalationReference),
            "prrc_assignment", "Produktbezogene PRRC-Verantwortung und andere Vertretung samt Organisationsreferenz fehlen.");
        Guard.Against(profile.Device.RiskClass != "III" || !profile.Device.Implantable ||
            string.IsNullOrWhiteSpace(profile.Device.Article18Rationale) || string.IsNullOrWhiteSpace(profile.Device.CertificateId) ||
            string.IsNullOrWhiteSpace(profile.Device.NotifiedBodyNumber), "device_regulatory_profile", "Begruendete Anwendbarkeit und fiktive Zulassungsmetadaten fehlen.");
        Guard.Against(profile.FirstPeriodStart != profile.Device.CertificationDate && string.IsNullOrWhiteSpace(profile.ScheduleAgreement),
            "psur_schedule", "Abweichender PSUR-Zeitplan braucht eine dokumentierte Vereinbarung.");
        Guard.Against(profile.FirstSubmissionDate < profile.FirstPeriodStart.AddYears(1) || string.IsNullOrWhiteSpace(profile.PsurReference),
            "psur_schedule", "Stabile PSUR-Referenz und ausdruecklich geplanter Einreichungstermin nach Periodenende fehlen.");
    }
}
