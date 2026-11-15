using System.Text.Json;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public static class DemoBaseline
{
    public static PolicyCatalog LoadPolicies(string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "baseline.json")));
        var root = json.RootElement;
        var devices = root.GetProperty("devices").Deserialize<Device[]>(Canonical.Json)!;
        var calendar = root.GetProperty("calendar");
        var holidays = calendar.GetProperty("holidays").Deserialize<DateOnly[]>(Canonical.Json)!;
        var calendars = calendar.GetProperty("jurisdictions").EnumerateArray().Select(j => new CalendarProfile(
            j.GetProperty("id").GetString()!, calendar.GetProperty("version").GetString()!, j.GetProperty("timeZone").GetString()!,
            DateOnly.Parse(calendar.GetProperty("validFrom").GetString()!), DateOnly.Parse(calendar.GetProperty("validTo").GetString()!),
            holidays, j.GetProperty("applyEuEndRules").GetBoolean())).ToArray();
        var trend = root.GetProperty("trend");
        using var terms = typeof(DemoBaseline).Assembly.GetManifestResourceStream("Cohors.Terminology.Fda2026.json")
            ?? throw new InvalidOperationException("Controlled terminology resource is missing.");
        using var reader = new StreamReader(terms);
        var terminologyJson = reader.ReadToEnd();
        var terminologyHash = Canonical.HashText(terminologyJson);
        Guard.Against(terminologyHash != "bb42629df30caef9c8c64ffcb1852bb3da92445bdea33a0faf079013361d47f8",
            "terminology_integrity", "Der kontrollierte FDA-Mappingstand hat einen anderen Hash.");
        return new() { Devices = devices, Calendars = calendars, Trend = new("synthetic-pms-plan-2026.1",
            trend.GetProperty("methodVersion").GetString()!, trend.GetProperty("referenceRate").GetDecimal(),
            trend.GetProperty("alpha").GetDecimal(), trend.GetProperty("unit").GetString()!),
            Terminology = new(Canonical.Read<TerminologyReference>(terminologyJson), terminologyHash) };
    }

    public static async Task Seed(string directory, IStateStore store, IObjectStore objects, CaseService cases,
        TimeProvider clock, CancellationToken ct = default, bool includeCases = true)
    {
        var actor = CaseService.Workflow;
        using var evidenceFile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "evidence.json"), ct));
        foreach (var e in evidenceFile.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            if (await store.Get<Evidence>("evidence", id, ct) is not null) continue;
            var content = e.GetProperty("content").GetString()!;
            var hash = await objects.Put(content, ct);
            var product = e.GetProperty("productId").GetString()!;
            var evidence = new Evidence(id, 1, product, e.GetProperty("title").GetString()!, e.GetProperty("kind").GetString()!,
                content, hash, "cohors:synthetic-baseline", e.GetProperty("locator").GetString()!, "Synthetic demonstration", "Product",
                DateTimeOffset.Parse("2020-01-01T00:00:00Z"), null, clock.GetUtcNow(), true, true, hash);
            await store.Execute<Evidence>(new(id, "evidence", product, 0, "seed-" + id, "EvidenceSeeded",
                "Versionierte synthetische Baseline; keine Echtbetriebsfreigabe.", actor, evidence, clock.GetUtcNow()), _ => evidence, ct);
        }
        foreach (var product in new[] { "ortho", "cardio" })
        {
            using var templates = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "controlled-documents.json"), ct));
            var productName = product == "ortho" ? "OrthoLink Modular Hip" : "CardioFlow Valve";
            foreach (var template in templates.RootElement.EnumerateArray())
            {
                var id = product + "-" + template.GetProperty("suffix").GetString();
                if (await store.Get<Evidence>("evidence", id, ct) is not null) continue;
                var content = template.GetProperty("content").GetString()!.Replace("{{productName}}", productName, StringComparison.Ordinal);
                var hash = await objects.Put(content, ct);
                var evidence = new Evidence(id, 1, product, template.GetProperty("title").GetString()!,
                    template.GetProperty("kind").GetString()!, content, hash, "cohors:synthetic-controlled-baseline",
                    "Demo-Dokument: kontrollierter Fachabschnitt", "Synthetic demonstration", "Product",
                    DateTimeOffset.Parse("2025-11-15T00:00:00Z"), null, clock.GetUtcNow(), true, true, hash);
                await store.Execute<Evidence>(new(id, "evidence", product, 0, "seed-" + id, "ControlledEvidenceSeeded",
                    "Versionierte synthetische Dokumentvorlage; keine reale Organisations- oder Fachfreigabe.", actor, evidence, clock.GetUtcNow()), _ => evidence, ct);
            }
            foreach (var region in new[] { "Worldwide", "EEA-Turkey-NI" })
            {
                var id = $"exposure-{product}-2026-{region}";
                if (await store.Get<Exposure>("exposure", id, ct) is not null) continue;
                var exposure = new Exposure(id, 1, product, region, new(2025, 11, 15), new(2026, 11, 14),
                    "Implants", region == "Worldwide" ? 3000 : 2200, "Synthetische implantierte Einheiten im Berichtsfenster",
                    "Versionierter fiktiver ERP-Aggregatdatensatz; Worldwide umfasst die Teilregion.", true);
                await store.Execute<Exposure>(new(id, "exposure", product, 0, "seed-" + id, "ExposureSeeded",
                    "Freigegebene synthetische statistische Referenz, keine echten Absatzdaten.", actor, exposure, clock.GetUtcNow()), _ => exposure, ct);
            }
            var profiles = Canonical.Read<ProductGovernance[]>(await File.ReadAllTextAsync(Path.Combine(directory, "governance.json"), ct));
            var policies = LoadPolicies(Path.GetFullPath(Path.Combine(directory, "..", "policies")));
            foreach (var profile in profiles)
            {
                GovernanceRules.RequireValid(profile, policies.Device(profile.ProductId));
                if (await store.Get<ProductGovernance>("productGovernance", profile.Id, ct) is not null) continue;
                await store.Execute<ProductGovernance>(new(profile.Id, "productGovernance", profile.ProductId, 0, "seed-governance-" + profile.Id,
                    "ProductGovernanceSeeded", "Fiktive Verantwortungs- und Zulassungsmetadaten; keine technischen Rechte abgeleitet.",
                    actor, profile, clock.GetUtcNow()), _ => profile, ct);
            }
        }
        if (!includeCases) return;
        using var caseFile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "cases.json"), ct));
        foreach (var e in caseFile.RootElement.EnumerateArray())
        {
            string Text(string field) => e.GetProperty(field).GetString()!;
            var number = Text("number");
            var id = Canonical.Hash(new { manufacturer = "cohors-synthetic", ProductId = Text("product"), IncidentKey = number })[..32];
            if (await store.Get<SafetyCase>("case", id, ct) is not null) continue;
            var awareness = DateTimeOffset.Parse(Text("awareness"));
            await cases.Register(new(number, "seed-" + number, Text("title"), Text("description"), Text("product"),
                e.GetProperty("udi").GetString(), "DEMO-LOT-2026", Text("country"), Text("jurisdiction"),
                Enum.Parse<Seriousness>(Text("seriousness")), awareness.AddDays(-1), awareness, Text("scenario"), true),
                actor, "seed-case-" + number, ct);
        }
    }
}
