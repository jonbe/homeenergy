using HomeEnergy.Configuration;
using HomeEnergy.Models;
using HomeEnergy.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("HomeEnergy"));
builder.Services.AddHttpClient();

// Master key for the login session cookies. Generated on first run and kept
// under the data directory so sessions survive restarts and image updates.
var bootOptions = builder.Configuration.GetSection("HomeEnergy").Get<AppOptions>() ?? new AppOptions();
builder.Services.AddDataProtection()
    .SetApplicationName("HomeEnergy")
    .PersistKeysToFileSystem(new DirectoryInfo(
        DataPaths.Resolve(bootOptions, builder.Environment.ContentRootPath, "keys")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SystemState>();
builder.Services.AddSingleton<ISecretProvider, EnvironmentSecretProvider>();
builder.Services.AddSingleton<ISolintegClient, SolintegClient>();
builder.Services.AddSingleton<ITibberClient, TibberClient>();
builder.Services.AddSingleton<ITibberVehicleClient, TibberVehicleClient>();
builder.Services.AddSingleton<IChargeAmpsClient, ChargeAmpsClient>();
builder.Services.AddSingleton<ISolarForecastService, SolarForecastService>();
builder.Services.AddSingleton<IPlanService, PlanService>();
builder.Services.AddSingleton<IHistoryStore, SqliteHistoryStore>();

builder.Services.AddHostedService<PollService>();
builder.Services.AddHostedService<PriceRefreshService>();
builder.Services.AddHostedService<ProductionRefreshService>();
builder.Services.AddHostedService<ForecastRefreshService>();
builder.Services.AddHostedService<VehicleRefreshService>();

var app = builder.Build();

app.Services.GetRequiredService<IHistoryStore>().Init();

// Password gate (see LoginGate). Disabled when no password is configured
// (local dev); otherwise guards the dashboard and every API/control endpoint.
var authToken = app.Services.GetRequiredService<IOptions<AppOptions>>().Value.AuthToken;
if (!string.IsNullOrEmpty(authToken))
{
    var gate = new LoginGate(authToken,
        app.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
    app.Use(gate.InvokeAsync);
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Build identity, baked into the image at build time (see Dockerfile /
// docker-publish.yml). Falls back to "dev" for local runs.
var build = new
{
    sha = Environment.GetEnvironmentVariable("BUILD_SHA") ?? "dev",
    time = Environment.GetEnvironmentVariable("BUILD_TIME") ?? "",
};

// Aggregate status for the dashboard.
app.MapGet("/api/status", (SystemState s, IPlanService planner, IHistoryStore history,
    ITibberVehicleClient tibber, IOptions<AppOptions> opt) =>
{
    var hist = history.TodayHourly();
    return Results.Json(new
    {
        build,
        automation = new { allowed = opt.Value.AutomationEnabled, on = s.AutomationActive },
        inverter = s.Inverter,
        prices = CurrentPrice(s.Prices),
        export = Export(history.TodayExport(), s.Prices?.Currency ?? ""),   // calculated from minute history
        tibberProduction = s.Production,                       // Tibber-reported
        charging = s.Charging,
        solar = s.Solar,
        vehicles = s.Vehicles,
        chargers = s.Chargers,
        tibber = new { connected = tibber.IsConnected },
        plan = planner.Build(s.Prices, s.Solar, s.Inverter),
        history = hist,
        actions = s.RecentActions(5),
        serverTime = DateTimeOffset.Now,
    });
});

// Turn runtime automation on/off. Only permitted while the config master
// (AutomationEnabled) allows it; otherwise the setting is fixed off.
app.MapPost("/api/automation/{on:bool}", (bool on, SystemState s, IOptions<AppOptions> opt) =>
{
    if (!opt.Value.AutomationEnabled)
        return Results.Conflict(new { error = "automation disabled in config" });
    s.AutomationActive = on;
    s.LogAction($"Automation turned {(on ? "on" : "off")}");
    return Results.Ok(new { on });
});

// Hourly history for a specific local date (YYYY-MM-DD), for the day-navigable
// chart. Returns the same 24-row shape as the status endpoint's today history.
app.MapGet("/api/history/{date}", (string date, IHistoryStore history) =>
    DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)
        ? Results.Json(history.HourlyForDate(d))
        : Results.BadRequest("date must be YYYY-MM-DD"));

// Manual mode override (e.g. from the UI). value = hex like 0x0400 or decimal.
app.MapPost("/api/mode/{value}", async (string value, ISolintegClient inv, SystemState s, CancellationToken ct) =>
{
    ushort mode = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt16(value, 16)
        : ushort.Parse(value);
    await inv.WriteModeAsync(mode, ct);
    s.LogAction($"Manual mode set to {SolintegClient.ModeLabel(mode)}");
    return Results.Ok(new { mode = $"0x{mode:X4}", label = SolintegClient.ModeLabel(mode) });
});

// Read-only diagnostic: dump a raw holding-register range as hex + decimal, for
// inspecting undocumented tables like the ToU charge-period schedule.
app.MapGet("/api/regs/{start:int}/{count:int}", async (int start, int count, ISolintegClient inv, CancellationToken ct) =>
{
    if (count is < 1 or > 125) return Results.BadRequest(new { error = "count 1..125" });
    var regs = await inv.ReadHoldingRawAsync((ushort)start, (ushort)count, ct);
    var rows = regs.Select((v, i) => new { reg = start + i, hex = $"0x{v:X4}", dec = v });
    return Results.Json(rows);
});

// Program a grid-charge window into the Economic-mode schedule (PV+GRID). Times
// as HH:mm; soc is the charge-stop percentage. Economic mode must be active for
// the inverter to act on it. e.g. POST /api/charge-window?start=02:00&end=05:00&soc=90
app.MapPost("/api/charge-window", async (string start, string end, double soc, ISolintegClient inv, SystemState s, CancellationToken ct) =>
{
    if (!TimeOnly.TryParse(start, out var t0) || !TimeOnly.TryParse(end, out var t1))
        return Results.BadRequest(new { error = "start/end must be HH:mm" });
    if (soc is < 0 or > 100) return Results.BadRequest(new { error = "soc 0..100" });
    await inv.WriteChargeWindowAsync(t0, t1, soc, ct);
    s.LogAction($"Grid-charge window set {t0:HH\\:mm}-{t1:HH\\:mm} to {soc:F0}%");
    return Results.Ok(new { start = t0.ToString("HH\\:mm"), end = t1.ToString("HH\\:mm"), soc });
});

// Manually grid-charge the battery for the next hour: program a one-hour window
// from now, switch to Economic, and remember to revert to General once it
// passes (PollService handles the revert each cycle). Bound to the "Battery"
// button in the inverter card.
app.MapPost("/api/charge-now", async (ISolintegClient inv, SystemState s, CancellationToken ct) =>
{
    var now = DateTimeOffset.Now;
    var end = now.AddHours(1);
    var start = TimeOnly.FromDateTime(now.DateTime);
    var stop = end.Date == now.Date ? TimeOnly.FromDateTime(end.DateTime) : new TimeOnly(23, 59);
    await inv.WriteChargeWindowAsync(start, stop, 100, ct);
    await inv.WriteModeAsync(SolintegClient.ModeEconomic, ct);
    s.ManualChargeUntil = end;
    s.LogAction($"Manual grid-charge {start:HH\\:mm}-{stop:HH\\:mm} — reverts to General after");
    return Results.Ok(new { start = start.ToString("HH\\:mm"), end = stop.ToString("HH\\:mm") });
});

// Clear the charge window back to empty.
app.MapPost("/api/charge-window/clear", async (ISolintegClient inv, SystemState s, CancellationToken ct) =>
{
    await inv.ClearChargeWindowAsync(ct);
    s.LogAction("Grid-charge window cleared");
    return Results.Ok(new { cleared = true });
});

// One-time OAuth consent to the Tibber Data API (car battery SoC).
// Open /tibber/connect in a browser; Tibber redirects back to /tibber/callback.
app.MapGet("/tibber/connect", (HttpRequest req, ITibberVehicleClient vehicles) =>
{
    var origin = $"{req.Scheme}://{req.Host}";
    var url = vehicles.BuildAuthorizeUrl(origin);
    return url is null
        ? Results.Problem("TIBBER_DATAAPI_CLIENT_ID not configured")
        : Results.Redirect(url);
});

app.MapGet("/tibber/callback", async (string? code, string? state, string? error,
    ITibberVehicleClient vehicles, CancellationToken ct) =>
{
    if (error is not null)
        return Results.Content($"Tibber authorization failed: {error}", "text/plain");
    if (code is null || state is null)
        return Results.BadRequest("Missing code/state");

    var ok = await vehicles.HandleCallbackAsync(code, state, ct);
    return Results.Content(
        ok ? "Tibber Data API connected. You can close this tab."
           : "Token exchange failed; check the server log.",
        "text/plain");
});

// Force an immediate car-data poll (the background poller runs every 10 min).
app.MapGet("/api/vehicles/refresh", async (ITibberVehicleClient vehicles, SystemState s, CancellationToken ct) =>
{
    var snap = await vehicles.FetchAsync(ct);
    if (snap is not null)
    {
        s.Vehicles = snap.Vehicles;
        s.Chargers = snap.Chargers;
    }
    return Results.Json(snap);
});

// The snapshot's Current point is only refetched twice a day, so it goes stale
// as hours pass. Re-derive it from the Today array (all quarter-hour points) at
// request time so the dashboard always shows the price for the current slot.
static PriceSnapshot? CurrentPrice(PriceSnapshot? p)
{
    if (p is null) return null;
    var now = DateTimeOffset.Now;
    var active = p.Today.Where(x => x.StartsAt <= now).MaxBy(x => x.StartsAt);
    return active is null ? p : p with { Current = active };
}

static ProductionSnapshot Export(ExportTotals t, string currency) =>
    new(t.Kwh, t.Value, currency, DateTimeOffset.Now);

app.Run();
