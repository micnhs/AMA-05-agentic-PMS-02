using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class GovernanceTests(SqlFixture fixture)
{
    [Fact]
    public async Task A05_A06_J04_ProductResponsibilitiesAndArticle18BasisArePersistentButDoNotGrantAppRights()
    {
        foreach (var device in fixture.Policies.Devices)
        {
            var profile = (await fixture.Store.Get<ProductGovernance>("productGovernance", device.Id))!;
            GovernanceRules.RequireValid(profile, device);
            Assert.NotEqual(profile.Prrc.PrimaryRole, profile.Prrc.DeputyRole);
            Assert.True(profile.Device.Article18Applicable);
            Assert.NotEmpty(profile.Device.Article18Rationale);
            Assert.Throws<DomainException>(() => new Actor("prrc", "Organizational PRRC", [profile.Prrc.PrimaryRole], [device.Id], true)
                .RequireHuman(Roles.Medical, Roles.Regulatory));
            foreach (var id in profile.DocumentIds)
                Assert.NotNull(await fixture.Store.Get<Evidence>("evidence", id));
            Assert.Throws<DomainException>(() => GovernanceRules.RequireValid(profile with
                { Prrc = profile.Prrc with { DeputyRole = profile.Prrc.PrimaryRole } }, device));
            Assert.Throws<DomainException>(() => GovernanceRules.RequireValid(profile with
                { Device = profile.Device with { BasicUdi = "DEMO-WRONG" } }, device));
        }
    }
}
