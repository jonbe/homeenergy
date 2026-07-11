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
    private const string Query = """
    { viewer { homes {
      currentSubscription { priceInfo(resolution: QUARTER_HOURLY) {
        current  { total currency level startsAt }
        today    { total level startsAt }
        tomorrow { total level startsAt }
      } } } } }
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
        var token = _secrets.Get("TIBBER_TOKEN");
        if (token is null)
        {
            _log.LogWarning("TIBBER_TOKEN not set; skipping price fetch");
            return null;
        }

        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = Query }),
                Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Headers.Add("User-Agent", "HomeEnergy/1.0");

        try
        {
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

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
