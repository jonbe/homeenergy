namespace HomeEnergy.Services;

/// <summary>Fetches Tibber prices at startup and at fixed daily times
/// (00:05 and 13:15 — tomorrow's prices publish around 13:00).</summary>
public class PriceRefreshService : BackgroundService
{
    private static readonly TimeOnly[] DailyTimes =
        { new(0, 5), new(13, 15) };

    private readonly ITibberClient _tibber;
    private readonly SystemState _state;
    private readonly ILogger<PriceRefreshService> _log;

    public PriceRefreshService(ITibberClient tibber, SystemState state, ILogger<PriceRefreshService> log)
    {
        _tibber = tibber;
        _state = state;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = await _tibber.FetchAsync(ct);
            if (s is not null) _state.Prices = s;

            var delay = DailySchedule.UntilNext(DailyTimes);
            _log.LogInformation("Next price refresh in {H:F1} h", delay.TotalHours);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Refreshes the solar forecast at startup and every 2 hours.
/// Frequent runs also let PR calibration capture a near-complete day at dusk.</summary>
public class ForecastRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    private readonly ISolarForecastService _solar;
    private readonly SystemState _state;

    public ForecastRefreshService(ISolarForecastService solar, SystemState state)
    {
        _solar = solar;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var s = await _solar.FetchAsync(ct);
            if (s is not null) _state.Solar = s;
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Refreshes connected-car data (battery %) from the Tibber Data API
/// at startup and every 10 minutes. Tibber itself polls the car cloud on its
/// own cadence, so more frequent polling gains nothing.</summary>
public class VehicleRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly ITibberVehicleClient _vehicles;
    private readonly SystemState _state;

    public VehicleRefreshService(ITibberVehicleClient vehicles, SystemState state)
    {
        _vehicles = vehicles;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var s = await _vehicles.FetchAsync(ct);
            if (s is not null)
            {
                _state.Vehicles = s.Vehicles;
                _state.Chargers = s.Chargers;
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

public static class DailySchedule
{
    /// <summary>Time until the next occurrence of any of the given daily times.</summary>
    public static TimeSpan UntilNext(IEnumerable<TimeOnly> times)
    {
        var now = DateTimeOffset.Now;
        DateTimeOffset best = DateTimeOffset.MaxValue;
        foreach (var t in times)
        {
            var today = new DateTimeOffset(now.Year, now.Month, now.Day,
                t.Hour, t.Minute, 0, now.Offset);
            var next = today > now ? today : today.AddDays(1);
            if (next < best) best = next;
        }
        return best - now;
    }
}
