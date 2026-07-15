using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Every 60 s: read inverter stats + charging status. When automation is
/// enabled it drives the inverter mode: a charging car forces Backup/UPS (which
/// reserves the battery so it is not drained into the car); otherwise it follows
/// the suggested plan for the current hour (charge / feed-in / general).
///
/// NOTE: writing the mode register conflicts with the Solinteg app/cloud
/// schema. Automation is OFF by default (AutomationEnabled) — enable only if
/// you drive the mode locally rather than via the app schedule.
/// </summary>
public class PollService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ISolintegClient _inverter;
    private readonly IChargeAmpsClient _chargeAmps;
    private readonly IPlanService _plan;
    private readonly IHistoryStore _history;
    private readonly SystemState _state;
    private readonly AppOptions _opt;
    private readonly ILogger<PollService> _log;

    public PollService(ISolintegClient inverter, IChargeAmpsClient chargeAmps,
        IPlanService plan, IHistoryStore history, SystemState state, IOptions<AppOptions> opt,
        ILogger<PollService> log)
    {
        _inverter = inverter;
        _chargeAmps = chargeAmps;
        _plan = plan;
        _history = history;
        _state = state;
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await PollOnce(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "poll cycle failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task PollOnce(CancellationToken ct)
    {
        var stats = await _inverter.ReadStatsAsync(ct);
        if (stats is not null) _state.Inverter = stats;

        var charging = await _chargeAmps.GetStatusAsync(ct);
        if (charging is not null) _state.Charging = charging;

        if (_opt.AutomationEnabled && charging is not null && stats is not null)
            await ApplyAutomation(charging, stats, ct);

        if (stats is not null)
            await LogHistory(stats, ct);
    }

    private async Task LogHistory(InverterStats stats, CancellationToken ct)
    {
        try
        {
            await _history.LogMinuteAsync(DateTimeOffset.Now, stats.ModeLabel,
                stats.PvPowerW, stats.BatterySoc, stats.GridPowerW, stats.LoadPowerW,
                CurrentPrice(), ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "history log failed"); }
    }

    // Snapshot.Current is only refetched twice a day, so it goes stale between
    // fetches. Derive the price for the active slot from the Today array instead
    // so each history sample records the real current price, not a frozen value.
    private double? CurrentPrice()
    {
        var today = _state.Prices?.Today;
        if (today is null) return null;
        var now = DateTimeOffset.Now;
        return today.Where(p => p.StartsAt <= now).MaxBy(p => p.StartsAt)?.Total;
    }

    private async Task ApplyAutomation(ChargingSnapshot charging, InverterStats stats, CancellationToken ct)
    {
        var (desired, reason) = DesiredMode(charging, stats);
        if (desired is not ushort want) return;

        int currentMode = Convert.ToInt32(stats.ModeRaw, 16);
        if (currentMode == want) return;

        await _inverter.WriteModeAsync(want, ct);
        SetAction($"{reason} -> {SolintegClient.ModeLabel(want)} (was {SolintegClient.ModeLabel(currentMode)})");
    }

    // A charging car forces Backup/UPS (reserves the battery); otherwise follow
    // the plan's current hour. Returns (null, _) when there is no plan yet,
    // meaning "leave the mode alone".
    private (ushort? mode, string reason) DesiredMode(ChargingSnapshot charging, InverterStats stats)
    {
        if (charging.AnyCharging)
            return (SolintegClient.ModeBackup, "Car charging");

        var plan = _plan.Build(_state.Prices, _state.Solar, stats);
        var slot = plan?.Today.FirstOrDefault(h => h.Hour == DateTimeOffset.Now.Hour);
        return slot is null ? (null, "") : (ModeForAction(slot.Action), $"Plan ({slot.Action})");
    }

    private static ushort? ModeForAction(string action) => action switch
    {
        "charge" => SolintegClient.ModePvCharging,
        "feedin" => SolintegClient.ModeFeedIn,
        "idle" => SolintegClient.ModeGeneral,
        _ => null,
    };

    private void SetAction(string msg)
    {
        _log.LogInformation("Automation: {Msg}", msg);
        _state.LogAction(msg);
    }
}
