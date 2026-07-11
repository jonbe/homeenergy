using HomeEnergy.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("HomeEnergy"));
builder.Services.AddHttpClient();

builder.Services.AddSingleton<SystemState>();
builder.Services.AddSingleton<SolintegClient>();
builder.Services.AddSingleton<TibberClient>();
builder.Services.AddSingleton<ChargeAmpsClient>();
builder.Services.AddSingleton<SolarForecastService>();

builder.Services.AddHostedService<PollService>();
builder.Services.AddHostedService<PriceRefreshService>();
builder.Services.AddHostedService<ForecastRefreshService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Aggregate status for the dashboard.
app.MapGet("/api/status", (SystemState s) => Results.Json(new
{
    inverter = s.Inverter,
    prices = s.Prices,
    charging = s.Charging,
    solar = s.Solar,
    lastAction = s.LastAction,
    lastActionAt = s.LastActionAt,
    serverTime = DateTimeOffset.Now,
}));

// Manual mode override (e.g. from the UI). value = hex like 0x0400 or decimal.
app.MapPost("/api/mode/{value}", async (string value, SolintegClient inv, SystemState s, CancellationToken ct) =>
{
    ushort mode = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt16(value, 16)
        : ushort.Parse(value);
    await inv.WriteModeAsync(mode, ct);
    s.LastAction = $"Manual mode set to {SolintegClient.ModeLabel(mode)}";
    s.LastActionAt = DateTimeOffset.Now;
    return Results.Ok(new { mode = $"0x{mode:X4}", label = SolintegClient.ModeLabel(mode) });
});

app.Run();
