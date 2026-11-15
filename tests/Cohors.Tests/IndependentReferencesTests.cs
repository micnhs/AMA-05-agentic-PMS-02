using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class IndependentReferencesTests
{
    private static JsonDocument References() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cohors-acceptance-references.json")));

    [Fact]
    public void T10_IndependentEuCalendarReferencesMatchActualDueInstants()
    {
        using var json = References();
        var profile = new CalendarProfile("EU-IE", "independent-synthetic-1", "Europe/Dublin",
            new(2025, 1, 1), new(2029, 12, 31), [new(2027, 1, 1)], true);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone);
        foreach (var item in json.RootElement.GetProperty("deadlineCases").EnumerateArray())
        {
            var awareness = item.GetProperty("awarenessAt").GetDateTimeOffset();
            var seriousness = item.GetProperty("calendarDays").GetInt32() switch
            { 2 => Seriousness.PublicHealthThreat, 10 => Seriousness.DeathOrSeriousDeterioration, _ => Seriousness.OtherSerious };
            var actual = DeadlineCalculator.Calculate("EU-IE", seriousness, awareness, profile);
            var legalLocal = TimeZoneInfo.ConvertTime(actual.LegalDue, zone);
            Assert.Equal(item.GetProperty("expectedLegalDueLocalDate").GetString(), legalLocal.ToString("yyyy-MM-dd"));
            Assert.Equal(new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9_999_999)), TimeOnly.FromDateTime(legalLocal.DateTime));
            var rawLocal = TimeZoneInfo.ConvertTime(actual.SafetyDue.AddHours(12), zone);
            Assert.Equal(item.GetProperty("expectedRawDueLocalDate").GetString(), rawLocal.ToString("yyyy-MM-dd"));
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(awareness, zone).DateTime).AddDays(1);
            var last = DateOnly.FromDateTime(legalLocal.DateTime);
            var included = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays)
                .Count(day => day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday && !profile.Holidays.Contains(day));
            Assert.Equal(item.GetProperty("expectedIncludedWorkingDays").GetInt32(), included);
            Assert.True(actual.SafetyDue < actual.LegalDue);
            if (item.GetProperty("id").GetString() == "eu-dst-start") Assert.Equal(TimeSpan.FromHours(1), legalLocal.Offset);
        }
    }

    [Fact]
    public void T10_GreatBritainDoesNotInheritEuExtensionButNorthernIrelandDoes()
    {
        var awareness = DateTimeOffset.Parse("2026-11-13T10:00:00Z");
        var calendar = new CalendarProfile("GB", "synthetic-1", "Europe/London", new(2026, 1, 1), new(2027, 12, 31), [], false);
        var gb = DeadlineCalculator.Calculate("GB", Seriousness.PublicHealthThreat, awareness, calendar);
        var ni = DeadlineCalculator.Calculate("NI", Seriousness.PublicHealthThreat, awareness, calendar with { Id = "NI", ApplyEuEndRules = true });
        Assert.Equal("2026-11-15", gb.LegalDue.ToString("yyyy-MM-dd"));
        Assert.Equal("2026-11-17", ni.LegalDue.ToString("yyyy-MM-dd"));
        Assert.Throws<DomainException>(() => DeadlineCalculator.Calculate("GB", Seriousness.PublicHealthThreat, awareness, calendar with { ApplyEuEndRules = true }));
    }

    [Fact]
    public void T15_RisingCountsCanHaveFallingDeviceYearNormalizedRate()
    {
        using var json = References();
        var r = json.RootElement.GetProperty("descriptiveRateReference");
        decimal Rate(JsonElement item)
        {
            var year = int.Parse(item.GetProperty("period").GetString()!);
            var exposure = new Exposure("reference", 1, "ortho", "Worldwide", new(year, 1, 1), new(year, 12, 31),
                "DeviceYears", item.GetProperty("matchedExposureDeviceYears").GetInt32(), "Independent", "Coordinator reference", true);
            var value = Statistics.DescriptiveRate(new("ortho", "Worldwide", exposure.Start, exposure.End,
                item.GetProperty("uniqueEvents").GetInt32(), exposure, "DeviceYears", 10000));
            Assert.Equal(item.GetProperty("expectedRatePer10000").GetDecimal(), value);
            return value;
        }
        var baseline = Rate(r.GetProperty("baseline"));
        var comparison = Rate(r.GetProperty("comparison"));
        Assert.Equal(r.GetProperty("expectedComparisonToBaselineRateRatio").GetDecimal(), comparison / baseline);
        Assert.Equal(r.GetProperty("expectedRelativeRateChangePercent").GetDecimal(), (comparison / baseline - 1) * 100);
    }

    [Fact]
    public void T15_MissingMismatchedOrNonpositiveExposureNeverEmitsARate()
    {
        var e = new Exposure("reference", 1, "ortho", "Worldwide", new(2025, 1, 1), new(2025, 12, 31),
            "DeviceYears", 10000, "Independent", "Coordinator reference", true);
        var input = new RateInput(e.ProductId, e.Region, e.Start, e.End, 2, e, e.Unit, 10000);
        foreach (var invalid in new Exposure?[] { null, e with { Count = 0 }, e with { Count = -1 },
            e with { Start = new(2024, 1, 1) }, e with { Region = "EEA-Turkey-NI" },
            e with { Unit = "Implants" }, e with { ProductId = "cardio" }, e with { Approved = false } })
            Assert.Equal("invalid_denominator", Assert.Throws<DomainException>(() => Statistics.DescriptiveRate(input with { Exposure = invalid })).Code);
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 0.950212931632136)]
    [InlineData(6, 3, 0.0839179420313034)]
    public void T15_PoissonTailMatchesIndependentNumericalValues(int count, double expected, double probability) =>
        Assert.Equal(probability, Statistics.PoissonUpperTail(count, expected), 12);
}
