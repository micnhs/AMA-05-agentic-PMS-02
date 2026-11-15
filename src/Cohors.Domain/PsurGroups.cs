namespace Cohors.Domain;

public sealed record PsurGroupProduct(string ProductId, VersionReference GovernanceSource, string BasicUdi,
    string RiskClass, string NotifiedBodyNumber);
public sealed record PsurGroupDefinition(string Id, int Version, string ProductId, string Reference,
    PsurGroupProduct[] Members, DateOnly FirstPeriodStart, string Rationale, string ScheduleBasis, DateTimeOffset RecordedAt);
public sealed record PsurGroupMember(PsurGroupProduct Product, ProductGovernance Governance, PsurSnapshot Snapshot);
public sealed record PsurGrouping(string Contract, VersionReference DefinitionSource, PsurGroupDefinition Definition,
    PsurGroupMember[] Members, string SeparationRule);

public static class PsurGroupRules
{
    public const string Contract = "cohors-psur-group-1";
    public static string Id(string reference)
    {
        Guard.Text(reference, "Synthetische Gruppenreferenz", 17, 100);
        Guard.Against(!reference.StartsWith("DEMO-PSUR-GROUP-", StringComparison.Ordinal) ||
            reference.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '-'),
            "psur_group_reference", "Explizite eindeutige DEMO-PSUR-GROUP-Referenz mit Grossbuchstaben/Ziffern erforderlich.");
        return "psur-group-" + Canonical.HashText(reference)[..32];
    }
    public static PsurGroupProduct Product(ProductGovernance p) =>
        new(p.ProductId, new(p.Id, p.Version, Canonical.Hash(p)), p.Device.BasicUdi, p.Device.RiskClass, p.Device.NotifiedBodyNumber);

    public static PsurGroupDefinition Define(string reference, string lead, string[] members, DateOnly firstPeriodStart,
        string rationale, string scheduleBasis, ProductGovernance[] profiles, PsurGroupDefinition? previous, DateTimeOffset now)
    {
        var id = Id(reference);
        Guard.Text(rationale, "Fachlich begruendete synthetische Gruppierung", 80, 4000);
        Guard.Text(scheduleBasis, "Leitprodukt-Zeitplan und fortbestehende Altserienpflichten", 40, 4000);
        Guard.Against(members.Length == 0 || members.Distinct(StringComparer.Ordinal).Count() != members.Length || !members.Contains(lead),
            "psur_group_members", "Eindeutige Mitgliedschaft einschliesslich des festen Leitprodukts erforderlich.");
        var selected = members.Order(StringComparer.Ordinal).Select(id => profiles.SingleOrDefault(p => p.ProductId == id)
            ?? throw new DomainException("psur_group_profile", "Kontrolliertes Profil eines Gruppenmitglieds fehlt.")).ToArray();
        var result = new PsurGroupDefinition(id, (previous?.Version ?? 0) + 1, lead, reference,
            selected.Select(Product).ToArray(), firstPeriodStart, rationale, scheduleBasis, now);
        Validate(result, selected);
        Guard.Against(previous is not null && (previous.ProductId != lead || previous.Reference != reference ||
            previous.FirstPeriodStart != firstPeriodStart ||
            previous.Members.Single(p => p.ProductId == lead).BasicUdi != result.Members.Single(p => p.ProductId == lead).BasicUdi ||
            previous.Members.Single(p => p.ProductId == lead).NotifiedBodyNumber != result.Members.Single(p => p.ProductId == lead).NotifiedBodyNumber),
            "psur_group_identity", "Leitprodukt/Basic-UDI, BS, Serienreferenz und Zeitplananker bleiben fest. Neue Serie aendert oder schliesst keine alte Pflicht.");
        return result;
    }

    public static void Validate(PsurGroupDefinition group, ProductGovernance[] profiles)
    {
        Guard.Against(group.Members.Length == 0 || group.Members.Select(p => p.ProductId).Distinct().Count() != group.Members.Length ||
            group.Members.Select(p => p.BasicUdi).Distinct().Count() != group.Members.Length ||
            group.Members.Count(p => p.ProductId == group.ProductId) != 1,
            "psur_group_members", "Mitglieder/Basic-UDIs muessen eindeutig sein; genau ein festes Leitprodukt.");
        foreach (var member in group.Members)
        {
            var profile = profiles.SingleOrDefault(p => p.ProductId == member.ProductId);
            Guard.Against(profile is null || member != Product(profile), "psur_group_profile_stale",
                "Gruppierung bindet eine andere Produktprofilversion; Mitgliedschaft ausdruecklich neu versionieren.", 409);
            Guard.Against(!profile!.Synthetic || !profile.Device.Implantable, "psur_group_synthetic",
                "Nur kontrollierte synthetische Implantatprofile im Piloten.");
        }
        var lead = group.Members.Single(p => p.ProductId == group.ProductId);
        Guard.Against(group.Members.Any(p => Rank(p.RiskClass) > Rank(lead.RiskClass)), "psur_group_lead",
            "Das feste Leitprodukt muss die hoechste Risikoklasse der Gruppe haben.");
        Guard.Against(group.Members.Any(p => p.NotifiedBodyNumber != lead.NotifiedBodyNumber), "psur_group_notified_body",
            "Eine verantwortliche Benannte Stelle fuer alle Gruppenmitglieder erforderlich.");
        Guard.Against(group.Members.Any(p => p.RiskClass != "III"), "psur_group_pilot_scope",
            "Dieser Pilot verwendet ausschliesslich synthetische Klasse-III-Implantate.");
        var leadProfile = profiles.Single(p => p.ProductId == group.ProductId);
        var years = group.FirstPeriodStart.Year - leadProfile.FirstPeriodStart.Year;
        Guard.Against(years < 0 || group.FirstPeriodStart != leadProfile.FirstPeriodStart.AddYears(years),
            "psur_group_schedule", "Der Gruppenzeitplan muss auf einer Jahresgrenze des kontrollierten Leitprodukt-Zeitplans beginnen.");
    }

    private static int Rank(string riskClass) => riskClass switch
    {
        "III" => 3, "IIb" => 2, "IIa" => 1, "I" => 0,
        _ => throw new DomainException("psur_group_class", "Unbekannte Risikoklasse ist keine begruendete Leitproduktauswahl.")
    };

    public static string[] Products(PsurSnapshot snapshot) =>
        new[] { snapshot.ProductId }.Concat(snapshot.Group?.Definition.Members.Select(m => m.ProductId) ?? [])
            .Concat(snapshot.Group?.Members.Select(m => m.Snapshot.ProductId) ?? []).Distinct(StringComparer.Ordinal).ToArray();
    public static void RequireScope(PsurSnapshot snapshot, Actor actor)
    {
        foreach (var product in Products(snapshot)) actor.RequireProduct(product);
    }
    public static bool CanRead(PsurSnapshot snapshot, Actor actor) => Products(snapshot).All(actor.Products.Contains);
    public static PsurSnapshot[] Snapshots(PsurSnapshot snapshot) => snapshot.Group is { } group ? group.Members.Select(m => m.Snapshot).ToArray() : [snapshot];
    public static Evidence[] Documents(PsurSnapshot snapshot) =>
        Snapshots(snapshot).SelectMany(s => s.Documents).DistinctBy(e => (e.Id, e.Version)).ToArray();

    public static QualityFinding[] Check(PsurSnapshot snapshot)
    {
        if (snapshot.Group is not { } group) return [];
        QualityFinding Invalid(string text) => new("psur_group_integrity", "Critical", text,
            "Gruppierung, Mitgliedsprofile und getrennte Familien-Snapshots nachvollziehbar neu einfrieren; Altberichte nicht umformen.", null);
        if (group.Contract != Contract || group.DefinitionSource != new VersionReference(group.Definition.Id, group.Definition.Version, Canonical.Hash(group.Definition)) ||
            group.Definition.ProductId != snapshot.ProductId || snapshot.LeadProductId != snapshot.ProductId ||
            group.Members.Length != group.Definition.Members.Length ||
            !group.Members.Select(m => m.Product.ProductId).Order(StringComparer.Ordinal).SequenceEqual(group.Definition.Members.Select(m => m.ProductId).Order(StringComparer.Ordinal)))
            return [Invalid("Gruppenquelle, festes Leitprodukt oder Mitgliederzahl ist nicht gebunden.")];
        try { Validate(group.Definition, group.Members.Select(m => m.Governance).ToArray()); }
        catch (DomainException ex) { return [Invalid(ex.Message)]; }
        foreach (var member in group.Members)
            if (member.Product != group.Definition.Members.Single(m => m.ProductId == member.Product.ProductId) ||
                member.Snapshot.Group is not null || member.Snapshot.ProductId != member.Product.ProductId ||
                member.Snapshot.LeadProductId != member.Product.ProductId ||
                member.Snapshot.Start != snapshot.Start || member.Snapshot.End != snapshot.End ||
                member.Snapshot.Region != snapshot.Region || member.Snapshot.FrozenAt != snapshot.FrozenAt)
                return [Invalid("Mitgliedsprofil, getrennte Auswertung oder leitproduktgebundenes Berichtsfenster ist inkonsistent.")];
        var lead = group.Members.Single(m => m.Product.ProductId == snapshot.ProductId);
        if (Canonical.Hash(snapshot with { Group = null }) != Canonical.Hash(lead.Snapshot))
            return [Invalid("Die fuehrende Ansicht ist keine unveraenderte Kopie der ausgewiesenen Leitproduktdaten.")];
        return [];
    }
}
