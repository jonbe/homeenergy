using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeEnergy.Configuration;
using HomeEnergy.Models;

namespace HomeEnergy.Services;

/// <summary>
/// Reads connected-car data (battery state of charge) from the Tibber Data API
/// (<c>data-api.tibber.com</c>). Auth is OAuth2 authorization-code against the
/// IdentityServer at <c>thewall.tibber.com</c>: the user consents once in the
/// browser (<c>/tibber/connect</c>), we exchange the code for an access +
/// refresh token, then refresh silently thereafter. The refresh token is
/// persisted to a file so consent survives restarts.
/// </summary>
public class TibberVehicleClient : ITibberVehicleClient
{
    private const string Authority = "https://thewall.tibber.com";
    private const string AuthorizeUrl = Authority + "/connect/authorize";
    private const string TokenUrl = Authority + "/connect/token";
    private const string DataApi = "https://data-api.tibber.com";
    private const string RedirectUri = "http://localhost:5034/tibber/callback";
    private const string Scopes = "openid offline_access data-api-homes-read data-api-vehicles-read data-api-chargers-read";

    private readonly IHttpClientFactory _http;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<TibberVehicleClient> _log;
    private readonly string _tokenFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // In-flight authorize requests: state -> PKCE code_verifier.
    private readonly ConcurrentDictionary<string, string> _pending = new();

    private string? _accessToken;
    private DateTimeOffset _accessExpiry;
    private string? _refreshToken;

    public TibberVehicleClient(
        IHttpClientFactory http, ISecretProvider secrets,
        ILogger<TibberVehicleClient> log, IWebHostEnvironment env)
    {
        _http = http;
        _secrets = secrets;
        _log = log;
        _tokenFile = Path.Combine(env.ContentRootPath, "tibber_tokens.json");
        _refreshToken = LoadRefreshToken();
    }

    public bool IsConnected => _refreshToken is not null;

    public string? BuildAuthorizeUrl()
    {
        var clientId = _secrets.Get("TIBBER_DATAAPI_CLIENT_ID");
        if (clientId is null)
        {
            _log.LogWarning("TIBBER_DATAAPI_CLIENT_ID not set; cannot start OAuth");
            return null;
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        _pending[state] = verifier;

        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var query = string.Join('&', q.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthorizeUrl}?{query}";
    }

    public async Task<bool> HandleCallbackAsync(string code, string state, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var verifier))
        {
            _log.LogWarning("Tibber OAuth callback with unknown state");
            return false;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        };
        return await ExchangeAsync(form, ct);
    }

    public async Task<TibberSnapshot?> FetchAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnsureAccessTokenAsync(ct))
                return null;

            var homes = await GetHomeIdsAsync(ct);
            if (homes is null) return null;

            var vehicles = new List<Vehicle>();
            var chargers = new List<Charger>();
            var seen = new HashSet<string>();   // devices repeat under every home; de-dupe
            foreach (var homeId in homes)
            {
                var devicesJson = await GetAsync($"/v1/homes/{homeId}/devices", ct);
                if (devicesJson is null) continue;

                foreach (var (id, name) in ListDevices(devicesJson))
                {
                    if (!seen.Add(id)) continue;   // same device under another home

                    var detail = await GetAsync(
                        $"/v1/homes/{homeId}/devices/{Uri.EscapeDataString(id)}", ct);
                    if (detail is null) continue;

                    ClassifyDevice(detail, name, vehicles, chargers);
                }
            }
            var now = DateTimeOffset.Now;
            return new TibberSnapshot(
                new VehicleSnapshot(vehicles, now),
                new ChargerSnapshot(chargers, now));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tibber vehicle fetch failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.Now < _accessExpiry)
            return true;

        if (_refreshToken is null)
        {
            _log.LogWarning("Tibber Data API not connected; visit /tibber/connect to authorize");
            return false;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
        };
        if (await ExchangeAsync(form, ct))
            return true;

        // Refresh token rejected (expired/revoked/rotated-out) -> require re-consent.
        _refreshToken = null;
        TryDelete(_tokenFile);
        _log.LogWarning("Tibber refresh failed; re-authorize at /tibber/connect");
        return false;
    }

    /// <summary>Posts the given grant to the token endpoint (client_secret_basic
    /// auth) and stores the returned access + refresh tokens on success.</summary>
    private async Task<bool> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var clientId = _secrets.Get("TIBBER_DATAAPI_CLIENT_ID");
        var clientSecret = _secrets.Get("TIBBER_DATAAPI_CLIENT_SECRET");
        if (clientId is null || clientSecret is null)
        {
            _log.LogWarning("TIBBER_DATAAPI_CLIENT_ID/SECRET not set");
            return false;
        }

        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var resp = await client.SendAsync(req, ct);
        var payload = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Tibber token exchange failed: {Status} {Body}", resp.StatusCode, payload);
            return false;
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at) || at.GetString() is not { } access)
        {
            _log.LogWarning("Tibber token response had no access_token");
            return false;
        }
        _accessToken = access;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt32() : 3600;
        _accessExpiry = DateTimeOffset.Now.AddSeconds(expiresIn - 60);   // refresh a minute early

        // Refresh tokens rotate on Tibber; always persist the newest one.
        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { } refresh)
        {
            _refreshToken = refresh;
            SaveRefreshToken(refresh);
        }
        return true;
    }

    private async Task<List<string>?> GetHomeIdsAsync(CancellationToken ct)
    {
        var json = await GetAsync("/v1/homes", ct);
        if (json is null) return null;

        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Tolerate both a bare array and an envelope ({ "homes": [...] } / { "data": [...] }).
        var arr = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("homes", out var h) && h.ValueKind == JsonValueKind.Array ? h
            : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : default;
        if (arr.ValueKind != JsonValueKind.Array) return ids;

        foreach (var home in arr.EnumerateArray())
            if (home.TryGetProperty("id", out var id) && id.GetString() is { } s)
                ids.Add(s);
        return ids;
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, DataApi + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Headers.Add("User-Agent", "HomeEnergy/1.0");

        var resp = await client.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _log.LogWarning("Tibber Data API {Path} -> {Status}", path, resp.StatusCode);
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Tibber Data API {Path} -> {Status}", path, resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Reads the device-list payload and yields (id, displayName) for
    /// each device. The list carries only identity; SoC is on the detail call.</summary>
    private static IEnumerable<(string Id, string? Name)> ListDevices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("devices", out var dv) && dv.ValueKind == JsonValueKind.Array ? dv
            : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : default;
        if (arr.ValueKind != JsonValueKind.Array) yield break;

        foreach (var dev in arr.EnumerateArray())
        {
            var id = Str(dev, "id");
            if (id is null) continue;
            var name = dev.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                ? Str(info, "name") : Str(dev, "name");
            yield return (id, name);
        }
    }

    /// <summary>Parses a device-detail payload and appends it to the vehicle or
    /// charger list. A <c>storage.stateOfCharge</c> capability marks an EV; a
    /// charger connector has a <c>charging.status</c> without one. Anything else
    /// is ignored. SoC/power/status live in the flat <c>capabilities</c> and
    /// <c>attributes</c> arrays keyed by id.</summary>
    private static void ClassifyDevice(string json, string? listName, List<Vehicle> vehicles, List<Charger> chargers)
    {
        using var doc = JsonDocument.Parse(json);
        var dev = doc.RootElement;
        var (name, model) = Info(dev, listName);

        if (Capability(dev, "storage.stateOfCharge") is { ValueKind: JsonValueKind.Number } socEl)
        {
            vehicles.Add(new Vehicle(
                name, model, socEl.GetDouble(),
                ChargingFlag(dev), Online(dev), LastSeen(dev)));
        }
        else if (Capability(dev, "charging.status") is not null)
        {
            chargers.Add(new Charger(
                name, model,
                Capability(dev, "connector.status") is { ValueKind: JsonValueKind.String } cs ? cs.GetString() : null,
                ChargingFlag(dev),
                Num(Capability(dev, "powerFlow.toBattery.power")),
                Num(Capability(dev, "charging.current.effective")),
                Num(Capability(dev, "charging.current.max")),
                Num(Capability(dev, "grid.phaseCount")) is { } p ? (int)p : null,
                Online(dev), LastSeen(dev)));
        }
    }

    private static (string? Name, string? Model) Info(JsonElement dev, string? fallbackName)
    {
        if (dev.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            return (fallbackName ?? Str(info, "name"), Str(info, "model"));
        return (fallbackName, null);
    }

    // charging.status: "charging" -> true, "idle" -> false, anything else -> null.
    private static bool? ChargingFlag(JsonElement dev) =>
        Capability(dev, "charging.status") is { ValueKind: JsonValueKind.String } cs
            ? cs.GetString() switch { "charging" => true, "idle" => false, _ => (bool?)null }
            : null;

    private static bool? Online(JsonElement dev) =>
        Attribute(dev, "isOnline") is { ValueKind: JsonValueKind.True or JsonValueKind.False } io
            ? io.GetBoolean() : null;

    private static DateTimeOffset? LastSeen(JsonElement dev) =>
        dev.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
            && st.TryGetProperty("lastSeen", out var ls) && ls.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ls.GetString(), out var when) ? when : null;

    private static double? Num(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;

    private static JsonElement? Capability(JsonElement dev, string id) => ValueById(dev, "capabilities", id);
    private static JsonElement? Attribute(JsonElement dev, string id) => ValueById(dev, "attributes", id);

    /// <summary>Returns the <c>value</c> of the first item in the named array
    /// whose <c>id</c> matches, or null.</summary>
    private static JsonElement? ValueById(JsonElement dev, string arrayName, string id)
    {
        if (!dev.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in arr.EnumerateArray())
            if (item.TryGetProperty("id", out var i) && i.GetString() == id
                && item.TryGetProperty("value", out var v))
                return v;
        return null;
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string? LoadRefreshToken()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_tokenFile));
            return doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read Tibber token file");
            return null;
        }
    }

    private void SaveRefreshToken(string token)
    {
        try
        {
            File.WriteAllText(_tokenFile,
                JsonSerializer.Serialize(new { refresh_token = token, savedAt = DateTimeOffset.Now }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist Tibber token file");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
