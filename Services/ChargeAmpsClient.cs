using System.Net;
using System.Text;
using System.Text.Json;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Reads charger status from the my.charge.space customer portal backend
/// (the same API the website calls) using just email + password.
/// Cookie-based auth (`ca_session`); every request MUST send the Origin header
/// or the edge returns 403 "Invalid origin".
/// </summary>
public class ChargeAmpsClient
{
    private readonly ChargeAmpsOptions _opt;
    private readonly ILogger<ChargeAmpsClient> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loggedIn;

    public ChargeAmpsClient(IOptions<AppOptions> opt, ILogger<ChargeAmpsClient> log)
    {
        _opt = opt.Value.ChargeAmps;
        _log = log;
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_opt.Origin) };
        _http.DefaultRequestHeaders.Add("Origin", _opt.Origin);
        _http.DefaultRequestHeaders.Add("Referer", _opt.Origin + "/userapp/");
    }

    public async Task<ChargingSnapshot?> GetStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loggedIn && !await LoginAsync(ct))
                return null;

            var resp = await Get("/users/chargepoints/owned", ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _loggedIn = false;                       // session expired -> retry once
                if (!await LoginAsync(ct)) return null;
                resp = await Get("/users/chargepoints/owned", ct);
            }
            resp.EnsureSuccessStatusCode();
            return Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Charge Amps status fetch failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> LoginAsync(CancellationToken ct)
    {
        var email = Secrets.Get("CHARGEAMPS_EMAIL");
        var pw = Secrets.Get("CHARGEAMPS_PASSWORD");
        if (email is null || pw is null)
        {
            _log.LogWarning("CHARGEAMPS_EMAIL/PASSWORD not set; skipping charging poll");
            return false;
        }

        var body = JsonSerializer.Serialize(new { email, password = pw });
        var resp = await _http.PostAsync(_opt.BasePath + "/auth/login",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Charge Amps login failed: {Status}", resp.StatusCode);
            return false;
        }
        _loggedIn = true;
        return true;
    }

    private Task<HttpResponseMessage> Get(string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _opt.BasePath + path);
        req.Headers.Add("Accept", "application/json");
        return _http.SendAsync(req, ct);
    }

    private static ChargingSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return new ChargingSnapshot("(none)", Array.Empty<ConnectorStatus>(), false, DateTimeOffset.Now);

        var cp = arr[0];
        string name = $"{Str(cp, "productName")} {Str(cp, "serialNumber")}".Trim();
        var connectors = new List<ConnectorStatus>();
        bool any = false;

        foreach (var c in cp.GetProperty("connectors").EnumerateArray())
        {
            bool charging = c.TryGetProperty("isCharging", out var ic) && ic.GetBoolean();
            any |= charging;
            double kw = 0, amps = 0;
            for (int ph = 1; ph <= 3; ph++)
            {
                double cur = Num(c, $"current{ph}");
                kw += cur * Num(c, $"voltage{ph}") / 1000.0;
                amps = Math.Max(amps, cur);
            }
            connectors.Add(new ConnectorStatus(
                c.TryGetProperty("connectorId", out var id) ? id.GetInt32() : 0,
                Str(c, "name"), Str(c, "ocppStatus"), charging, kw, amps));
        }
        return new ChargingSnapshot(name, connectors, any, DateTimeOffset.Now);
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
