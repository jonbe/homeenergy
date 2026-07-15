using System.Text;
using System.Text.Json;
using HomeEnergy.Configuration;
using HomeEnergy.Models;

namespace HomeEnergy.Services;

/// <summary>Reads Tibber electricity prices via the public GraphQL API.
/// Requires a personal access token (TIBBER_TOKEN) and a User-Agent header.</summary>
public class TibberClient : ITibberClient
{
    private const string Url = "https://api.tibber.com/v1-beta/gql";
    // QUARTER_HOURLY: 96 points/day (00:00, 00:15, …). Falls back to hourly
    // resolution automatically in markets that don't publish 15-min prices.
    private const string PriceQuery = """
    { viewer { homes {
      currentSubscription { priceInfo(resolution: QUARTER_HOURLY) {
        current  { total currency level startsAt }
        today    { total level startsAt }
        tomorrow { total level startsAt }
      } } } } }
    """;

    // Last 24 hourly production nodes; we sum the ones dated today. Recent hours
    // may be null because Tibber's production data lags behind real time.
    private const string ProductionQuery = """
    { viewer { homes {
      production(resolution: HOURLY, last: 24) {
        nodes { from production profit currency }
      } } } }
    """;

    private readonly IHttpClientFactory _http;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<TibberClient> _log;

    public TibberClient(IHttpClientFactory http, ISecretProvider secrets, ILogger<TibberClient> log)
    {
        _http = http;
        _secrets = secrets;
        _log = log;
    }

    public async Task<PriceSnapshot?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync(PriceQuery, ct);
            if (doc is null) return null;

            var pi = doc.RootElement
                .GetProperty("data").GetProperty("viewer")
                .GetProperty("homes")[0]
                .GetProperty("currentSubscription").GetProperty("priceInfo");

            var current = ParsePoint(pi.GetProperty("current"));
            var currency = pi.GetProperty("current").TryGetProperty("currency", out var c)
                ? c.GetString() ?? "" : "";

            return new PriceSnapshot(
                currency, current,
                ParseArray(pi.GetProperty("today")),
                ParseArray(pi.GetProperty("tomorrow")),
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tibber price fetch failed");
            return null;
        }
    }

    public async Task<ProductionSnapshot?> FetchProductionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync(ProductionQuery, ct);
            if (doc is null) return null;

            var nodes = doc.RootElement
                .GetProperty("data").GetProperty("viewer")
                .GetProperty("homes")[0]
                .GetProperty("production").GetProperty("nodes");

            var today = DateTimeOffset.Now.Date;
            double kwh = 0, profit = 0;
            bool any = false;
            string currency = "";
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.GetProperty("from").GetDateTimeOffset().LocalDateTime.Date != today)
                    continue;
                if (currency.Length == 0 && n.TryGetProperty("currency", out var c))
                    currency = c.GetString() ?? "";
                if (n.GetProperty("production").ValueKind is JsonValueKind.Number)
                { kwh += n.GetProperty("production").GetDouble(); any = true; }
                if (n.GetProperty("profit").ValueKind is JsonValueKind.Number)
                { profit += n.GetProperty("profit").GetDouble(); any = true; }
            }

            return any
                ? new ProductionSnapshot(kwh, profit, currency, DateTimeOffset.Now)
                : new ProductionSnapshot(null, null, currency, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tibber production fetch failed");
            return null;
        }
    }

    private async Task<JsonDocument?> PostAsync(string query, CancellationToken ct)
    {
        var token = _secrets.Get("TIBBER_TOKEN");
        if (token is null)
        {
            _log.LogWarning("TIBBER_TOKEN not set; skipping Tibber fetch");
            return null;
        }

        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Headers.Add("User-Agent", "HomeEnergy/1.0");

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static PricePoint ParsePoint(JsonElement e) => new(
        e.GetProperty("startsAt").GetDateTimeOffset(),
        e.GetProperty("total").GetDouble(),
        e.GetProperty("level").GetString() ?? "");

    private static List<PricePoint> ParseArray(JsonElement arr)
    {
        var list = new List<PricePoint>();
        foreach (var e in arr.EnumerateArray())
            list.Add(ParsePoint(e));
        return list;
    }
}
