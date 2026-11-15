using System.Net;
using System.Text.Json;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class PublicSources(HttpClient http) : IPublicSourceReader
{
    private static readonly string[] Hosts = ["api.fda.gov", "eutils.ncbi.nlm.nih.gov"];

    public async Task<PublicSourceItem[]> Read(string source, string productId, CancellationToken ct = default)
    {
        Guard.Against(productId is not ("ortho" or "cardio"), "source_product", "Kein kuratierter Suchvertrag fuer dieses Produkt.");
        if (source == "fda")
        {
            var term = productId == "ortho" ? "hip" : "heart";
            var url = $"https://api.fda.gov/device/event.json?search=device.generic_name:{term}&count=device.generic_name.exact&limit=5";
            using var response = await Get(new Uri(url), ct);
            return Member(response.RootElement, "results", JsonValueKind.Array).EnumerateArray().Take(5).Select(item =>
            {
                var title = RequiredText(item, "term");
                var countValue = Member(item, "count", JsonValueKind.Number);
                if (!countValue.TryGetInt32(out var count) || count < 0) throw Contract("count");
                return new PublicSourceItem("FDA-category-" + Canonical.HashText(title)[..16], title, url, null,
                    $"MAUDE-Meldungszaehlung zur generischen Kategorie beim Abruf: {count}. " +
                    "Dies ist weder eine deduplizierte Herstellerfallzahl noch eine Inzidenz, Exposition oder Kausalitaetswahrscheinlichkeit. " +
                    "Keine Patientennarrative abgerufen. Keine Zuordnung zu einem fiktiven Cohors-Produkt behauptet.", Canonical.Hash(item));
            }).ToArray();
        }
        Guard.Against(source != "pubmed", "source_not_allowed", "Nicht freigegebene Quelle.");
        var query = productId == "ortho" ? "hip arthroplasty complications" : "prosthetic heart valve complications";
        using var search = await Get(new Uri("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esearch.fcgi?db=pubmed&retmode=json&retmax=5&tool=cohors-pilot&term=" +
            Uri.EscapeDataString(query)), ct);
        var ids = Member(Member(search.RootElement, "esearchresult", JsonValueKind.Object), "idlist", JsonValueKind.Array)
            .EnumerateArray().Take(5).Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String) throw Contract("idlist");
                var id = item.GetString()!;
                if (id.Length is < 1 or > 12 || !id.All(char.IsAsciiDigit)) throw Contract("idlist");
                return id;
            }).ToArray();
        if (ids.Length == 0) return [];
        using var summary = await Get(new Uri("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/esummary.fcgi?db=pubmed&retmode=json&tool=cohors-pilot&id=" +
            string.Join(",", ids)), ct);
        var result = Member(summary.RootElement, "result", JsonValueKind.Object);
        return ids.Select(id =>
        {
            var item = Member(result, id, JsonValueKind.Object);
            var title = RequiredText(item, "title");
            var publication = RequiredText(item, "pubdate");
            return new PublicSourceItem(id, title, $"https://pubmed.ncbi.nlm.nih.gov/{id}/", null,
                $"PubMed-Metadaten; Publikationsangabe: {publication}. " +
                "Nur Titel-/Publikationsmetadaten, kein Volltext und keine abgeleitete klinische Schlussfolgerung. " +
                "Relevanz, Population, Produktaehnlichkeit und Volltextrechte muessen menschlich geprueft werden.", Canonical.Hash(item));
        }).ToArray();
    }

    private static DomainException Contract(string field) =>
        new("source_contract", $"Oeffentliche JSON-Antwort verletzt den geprueften Feldvertrag ({field}); keine Ersatzmetadaten.");

    private static JsonElement Member(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw Contract(name);
        return value;
    }

    private static string RequiredText(JsonElement parent, string name)
    {
        var text = Member(parent, name, JsonValueKind.String).GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12000) throw Contract(name);
        return text;
    }

    private async Task<JsonDocument> Get(Uri uri, CancellationToken ct)
    {
        Guard.Against(uri.Scheme != "https" || !Hosts.Contains(uri.Host) || !uri.IsDefaultPort || uri.UserInfo.Length != 0,
            "source_not_allowed", "URL liegt ausserhalb des festen Lesevertrags.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CohorsSyntheticPilot/0.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 2)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, budget.Token);
                continue;
            }
            Guard.Against((int)response.StatusCode is >= 300 and < 400, "source_redirect",
                "Weiterleitung wird nicht verfolgt; Quellenvertrag muss neu geprueft werden.");
            response.EnsureSuccessStatusCode();
            Guard.Against(response.Content.Headers.ContentLength > 1_000_000, "source_budget", "Quellenantwort ueberschreitet das Lesevolumen.");
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, budget.Token)) > 0)
            {
                Guard.Against(bytes.Length + read > 1_000_000, "source_budget", "Quellenantwort ueberschreitet das Lesevolumen.");
                bytes.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(bytes.ToArray());
        }
        throw new DomainException("source_retry_budget", "Leseversuche erschoepft; manuell spaeter erneut beauftragen.");
    }
}
