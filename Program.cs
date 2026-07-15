using HomeEnergy.Configuration;
using HomeEnergy.Models;
using HomeEnergy.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("HomeEnergy"));
builder.Services.AddHttpClient();

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

app.UseDefaultFiles();
app.UseStaticFiles();

// Aggregate status for the dashboard.
app.MapGet("/api/status", (SystemState s, IPlanService planner, IHistoryStore history, ITibberVehicleClient tibber) =>
{
    var hist = history.TodayHourly();
    return Results.Json(new
    {
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

// One-time OAuth consent to the Tibber Data API (car battery SoC).
// Open /tibber/connect in a browser; Tibber redirects back to /tibber/callback.
app.MapGet("/tibber/connect", (ITibberVehicleClient vehicles) =>
{
    var url = vehicles.BuildAuthorizeUrl();
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
