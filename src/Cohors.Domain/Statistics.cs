namespace Cohors.Domain;

public sealed record RateInput(string ProductId, string Region, DateOnly Start, DateOnly End,
    int UniqueEvents, Exposure? Exposure, string Unit, decimal Scale);

public static class Statistics
{
    public static decimal DescriptiveRate(RateInput input)
    {
        var e = input.Exposure;
        Guard.Against(e is null || !e.Approved || e.Count <= 0 || e.Unit != input.Unit ||
            e.ProductId != input.ProductId || e.Region != input.Region || e.Start != input.Start || e.End != input.End,
            "invalid_denominator", "Freigegebener Nenner muss Produkt, Region, Einheit und Zeitraum exakt treffen.");
        Guard.Against(input.UniqueEvents < 0 || input.Scale <= 0 || input.Unit is not ("Implants" or "DeviceYears"),
            "invalid_rate", "Ungueltiger Zaehler, Massstab oder Nennerbezug.");
        return input.UniqueEvents / (decimal)e!.Count * input.Scale;
    }

    public static TrendResult Calculate(TrendInput input)
    {
        var e = input.Exposure;
        var count = input.CaseIds.Distinct(StringComparer.Ordinal).Count();
        var rate = DescriptiveRate(new(input.ProductId, input.Region, input.Start, input.End,
            count, e, "Implants", 1000));
        Guard.Against(input.ReferenceRate <= 0 || input.ReferenceRate > 1, "invalid_baseline", "Referenzrate muss aus einem freigegebenen PMS-Plan stammen.");
        Guard.Against(input.MethodVersion != "poisson-upper-tail-1", "method_version", "Nicht freigegebene statistische Methode.");
        var expected = e.Count * input.ReferenceRate;
        var tail = PoissonUpperTail(count, (double)expected);
        return new(count, e.Count, e.Unit, rate, expected, tail, tail < 0.05,
            ["Explorativer einseitiger Poisson-Test, Alpha 0,05; keine automatische Art.-88-Entscheidung.",
             "Unabhaengige Ereignisse und konstante Referenzrate angenommen; kleine Fallzahlen, Meldeverhalten und Mehrfachtests beachten.",
             "Implantatbezogene Beschwerderate im synthetischen Nenner, keine populationsbasierte klinische Inzidenz.",
             "Oeffentliche MAUDE-Meldungen sind ausgeschlossen; Kausalitaet wird nicht statistisch bewiesen."]);
    }

    public static double PoissonUpperTail(int k, double lambda)
    {
        Guard.Against(k < 0 || !double.IsFinite(lambda) || lambda <= 0 || lambda > 500, "statistical_range",
            "Poisson-Demomethode unterstuetzt k >= 0 und 0 < Erwartungswert <= 500.");
        if (k == 0) return 1;
        var p = Math.Exp(-lambda);
        var lower = p;
        for (var i = 1; i < k; i++)
        {
            p *= lambda / i;
            lower += p;
        }
        return Math.Clamp(1 - lower, 0, 1);
    }
}
