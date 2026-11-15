using Cohors.Agents;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class FoundryBoundaryTests
{
    private static FoundryOptions Settings(string sku = "Standard", string boundary = "EU-Member-States",
        string[]? countries = null)
    {
        var review = new FoundryBoundaryApproval("synthetic-review-2026-001", "", sku, "germanywestcentral", "Germany", countries ?? ["DE"]);
        var settings = new FoundryOptions("https://synthetic-not-provisioned.openai.azure.com/", "unprovisioned", "test-version", boundary, review);
        return settings with { BoundaryApproval = review with { ConfigurationHash = FoundryBoundary.ConfigurationHash(settings, review) } };
    }

    [Theory]
    [InlineData("DataZoneStandard")]
    [InlineData("DataZoneProvisionedManaged")]
    [InlineData("GlobalStandard")]
    [InlineData("GlobalProvisionedManaged")]
    [InlineData("DeveloperTier")]
    public void T08_NonRegionalSkusAreRejectedEvenWithAnEuLabel(string sku)
    {
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(sku)));
    }

    [Fact]
    public void T08_ProviderBoundaryNeedsAConfigurationBoundReviewNotABareLabel()
    {
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(boundary: "EUDataZone")));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(boundary: "EU")));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(countries: ["DE", "NO"])));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(countries: ["CH"])));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings() with { BoundaryApproval = null }));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings() with { ModelVersion = "changed" }));
        Assert.Equal("synthetic-not-provisioned.openai.azure.com", FoundryBoundary.RequireApproved(Settings()).Host);
    }
}
