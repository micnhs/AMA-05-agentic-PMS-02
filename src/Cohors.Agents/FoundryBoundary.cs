using Cohors.Domain;

namespace Cohors.Agents;

public sealed record FoundryBoundaryApproval(string Reference, string ConfigurationHash, string DeploymentSku,
    string DeploymentRegion, string DeploymentGeography, string[] ProcessingCountries);

public static class FoundryBoundary
{
    private static readonly HashSet<string> EuMembers = new(
        ["AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
         "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"], StringComparer.Ordinal);

    public static string ConfigurationHash(FoundryOptions settings, FoundryBoundaryApproval approval) =>
        Canonical.Hash(new { settings.Endpoint, settings.Deployment, settings.ModelVersion, settings.ProcessingBoundary,
            approval.Reference, approval.DeploymentSku, approval.DeploymentRegion, approval.DeploymentGeography,
            countries = approval.ProcessingCountries.Order(StringComparer.Ordinal).ToArray() });

    public static Uri RequireApproved(FoundryOptions settings)
    {
        Guard.Against(string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Deployment)
            || string.IsNullOrWhiteSpace(settings.ModelVersion), "foundry_not_configured",
            "Live Foundry ist nicht konfiguriert; kein stiller Replay-Fallback.");
        var approval = settings.BoundaryApproval;
        Guard.Against(settings.ProcessingBoundary != "EU-Member-States" || approval is null,
            "model_boundary", "Konkreter gepruefter Deploymentnachweis fuer ausschliessliche EU-Mitgliedstaaten fehlt.");
        Guard.Against(approval!.DeploymentSku is not ("Standard" or "ProvisionedManaged"),
            "model_boundary_sku", "DataZone-, Global- und Developer-Deployments sind fuer diese Pilotgrenze nicht freigegeben.");
        Guard.Text(approval.Reference, "Deploymentpruefnachweis", 8, 500);
        Guard.Text(approval.DeploymentRegion, "Bereitstellungsregion", 3, 100);
        Guard.Text(approval.DeploymentGeography, "Verarbeitungsgeografie", 3, 100);
        Guard.Against(approval.ProcessingCountries is null || approval.ProcessingCountries.Length == 0 ||
            approval.ProcessingCountries.Any(country => !EuMembers.Contains(country)),
            "model_boundary_countries", "Alle geprueften Verarbeitungslaender muessen EU-Mitgliedstaaten sein; EFTA ist nicht gleich EU.");
        Guard.Against(approval.ConfigurationHash != ConfigurationHash(settings, approval),
            "model_boundary_stale", "Freigabenachweis bindet eine andere Deployment-/Modell-/Geografiekonfiguration.");
        Guard.Against(!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https" || endpoint.Port != 443 ||
            !endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/",
            "model_endpoint", "Nur der konfigurierte HTTPS-Modellendpunkt ohne Umleitung oder Benutzerinfo ist erlaubt.");
        return endpoint!;
    }
}
