using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class RetentionRulesTests
{
    private static JsonDocument Reference() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cohors-retention-recovery-references.json")));
    private static RetentionRecord Item(string dataClass, DateTimeOffset receivedAt) =>
        new("retention-test", 1, "ortho", new(RetentionRules.ObjectContract, dataClass,
                (dataClass == RetentionRules.Raw ? RetentionRules.RawPrefix : "") + new string('a', 64), new string('a', 64)),
            RetentionRules.PolicyVersion, "SYNTHETIC-RETENTION-REFERENCE", receivedAt, receivedAt, true, false, "", "Registered",
            null, 0, null, null, null);

    [Fact]
    public void T18_ImplantMinimumUsesConfirmedLastPlacingDateAndIncludesItsFinalLocalDayNotTheUploadDate()
    {
        using var reference = Reference();
        var input = reference.RootElement.GetProperty("regulatoryDateReference");
        var last = DateOnly.Parse(input.GetProperty("lastPlacingOnMarketLocalDate").GetString()!);
        var lifecycle = new ProductLifecycle("lifecycle-ortho", 1, "ortho", last, true, input.GetProperty("timeZone").GetString()!,
            RetentionRules.PolicyVersion, "SYNTHETIC-DISTRIBUTION-DECLARATION", "Explizit bestaetigte synthetische Referenz, kein realer Vertriebsnachweis.",
            "quality-reference", DateTimeOffset.Parse("2026-11-15T12:00:00Z"), true);
        foreach (var upload in input.GetProperty("uploadDatesWithSamePolicyMinimum").EnumerateArray())
        {
            var item = Item(RetentionRules.ImplantDocumentation, DateTimeOffset.Parse(upload.GetString()! + "T12:00:00Z"));
            foreach (var check in input.GetProperty("checks").EnumerateArray())
            {
                var result = RetentionRules.Evaluate(item, lifecycle,
                    DateTimeOffset.Parse(check.GetProperty("checkedLocalDate").GetString()! + "T12:00:00+01:00"), 0, true);
                Assert.Equal(DateOnly.Parse(input.GetProperty("expectedPolicyMinimumRetainThroughLocalDate").GetString()!), result.MinimumRetainThroughLocalDate);
                Assert.Equal(check.GetProperty("minimumIntervalElapsed").GetBoolean(), result.MinimumIntervalElapsed);
                Assert.False(result.CanDeleteNow);
                Assert.Contains("regulatory_deletion_not_authorized_in_pilot", result.Blockers);
            }
        }
    }

    [Fact]
    public void T18_MissingOrUnconfirmedLifecycleCannotFallBackToUploadAge()
    {
        var item = Item(RetentionRules.ImplantDocumentation, DateTimeOffset.Parse("2000-01-01T00:00:00Z"));
        var unconfirmed = new ProductLifecycle("lifecycle-ortho", 1, "ortho", new(2000, 1, 1), false,
            RetentionRules.LifecycleTimeZone, RetentionRules.PolicyVersion, "UNCONFIRMED-SYNTHETIC-SOURCE",
            "Der Zeitpunkt ist noch nicht bestaetigt.", "", item.RecordedAt, true);
        foreach (var lifecycle in new[] { null, unconfirmed })
        {
            var result = RetentionRules.Evaluate(item, lifecycle, DateTimeOffset.Parse("2035-07-01T12:00:00Z"), 0, true);
            Assert.Null(result.MinimumRetainThroughLocalDate);
            Assert.Null(result.MinimumIntervalElapsed);
            Assert.False(result.EligibleForAuthorization);
            Assert.Contains("lifecycle_missing_or_unconfirmed", result.Blockers);
        }
    }

    [Fact]
    public void T18_ExplicitRawPolicyUsesTheExactSevenElapsedDayBoundary()
    {
        using var reference = Reference();
        var input = reference.RootElement.GetProperty("shortRawRetentionReference");
        var item = Item(input.GetProperty("dataClass").GetString()!, input.GetProperty("receivedAt").GetDateTimeOffset());
        foreach (var check in input.GetProperty("checks").EnumerateArray())
        {
            var result = RetentionRules.Evaluate(item, null, check.GetProperty("checkedAt").GetDateTimeOffset(), 0, true);
            Assert.Equal(input.GetProperty("expectedExpiresAt").GetDateTimeOffset(), result.ExpiresAt);
            Assert.Equal(check.GetProperty("expired").GetBoolean(), result.MinimumIntervalElapsed);
            Assert.False(result.CanDeleteNow);
        }
    }

    [Fact]
    public void T18_ExpiryNeverOverridesHoldAuthorizationOrHistoricalSharedHashBinding()
    {
        using var reference = Reference();
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        foreach (var check in reference.RootElement.GetProperty("deletionGuardCases").EnumerateArray()
            .Where(c => c.TryGetProperty("physicalObjectDeletionPermitted", out _)))
        {
            var item = Item(RetentionRules.Raw, DateTimeOffset.Parse("2026-11-01T12:00:00Z")) with {
                LegalHold = check.GetProperty("legalHold").GetBoolean() };
            var count = check.GetProperty("retainedReferenceCount").GetInt32();
            if (check.GetProperty("authorizedRequest").GetBoolean())
            {
                var scope = RetentionRules.Evaluate(item, null, now, count, true).ScopeHash;
                item = item with { Authorization = new("synthetic-operation", "quality-reference", scope, now,
                    "Expliziter autorisierter synthetischer Loeschauftrag.", true,
                    "Isolierten Testbereich einschliesslich Exporten, Backups, Caches und Agentenspeichern kontrolliert geprueft.") };
            }
            var result = RetentionRules.Evaluate(item, null, now, count, true);
            Assert.Equal(check.GetProperty("physicalObjectDeletionPermitted").GetBoolean(), result.CanDeleteNow);
        }
    }
}
