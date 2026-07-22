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

    // The grid-charge window we currently have programmed into the inverter's
    // Economic schedule, or null if none. Tracked so we only write on change.
    private (TimeOnly Start, TimeOnly End, int Soc)? _chargeWindow;

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

        await MaybeRevertManualCharge(ct);

        // A running manual grid-charge takes precedence — don't let automation
        // rewrite the window or mode out from under it.
        if (_state.ManualChargeUntil is null &&
            _opt.AutomationEnabled && _state.AutomationActive && charging is not null && stats is not null)
            await ApplyAutomation(charging, stats, ct);

        if (stats is not null)
            await LogHistory(stats, ct);
    }

    // End a user-triggered grid-charge once its hour is up: clear the window and
    // put the inverter back to General. Runs regardless of the automation flag.
    private async Task MaybeRevertManualCharge(CancellationToken ct)
    {
        var until = _state.ManualChargeUntil;
        if (until is null || DateTimeOffset.Now < until) return;

        await _inverter.ClearChargeWindowAsync(ct);
        await _inverter.WriteModeAsync(SolintegClient.ModeGeneral, ct);
        _state.ManualChargeUntil = null;
        _chargeWindow = null;
        SetAction("Manual grid-charge ended -> General");
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
        var plan = _plan.Build(_state.Prices, _state.Solar, stats);

        // Grid-charge scheduling: while the low-sun plan wants to import into the
        // battery this hour (and no car is charging) program a matching Economic
        // window; otherwise make sure any window we set is cleared.
        if (!charging.AnyCharging && plan is not null)
            await SyncChargeWindow(plan, stats, ct);
        else if (_chargeWindow is not null)
            await ClearChargeWindow(ct);

        var (desired, reason) = DesiredMode(charging, plan);
        if (desired is not ushort want) return;

        int currentMode = Convert.ToInt32(stats.ModeRaw, 16);
        if (currentMode == want) return;

        await _inverter.WriteModeAsync(want, ct);
        SetAction($"{reason} -> {SolintegClient.ModeLabel(want)} (was {SolintegClient.ModeLabel(currentMode)})");
    }

    // A charging car forces Backup/UPS (reserves the battery); otherwise follow
    // the plan's current hour. Returns (null, _) when there is no plan yet,
    // meaning "leave the mode alone".
    private (ushort? mode, string reason) DesiredMode(ChargingSnapshot charging, PlanSnapshot? plan)
    {
        if (charging.AnyCharging)
            return (SolintegClient.ModeBackup, "Car charging");

        var slot = plan?.Today.FirstOrDefault(h => h.Hour == DateTimeOffset.Now.Hour);
        return slot is null ? (null, "") : (ModeForAction(slot.Action), $"Plan ({slot.Action})");
    }

    // Program (or clear) the Economic-mode charge window to match the plan's run
    // of consecutive "gridcharge" hours starting at the current hour. The window
    // charges up to a stop-SOC sized from the plan's remaining grid-charge need.
    private async Task SyncChargeWindow(PlanSnapshot plan, InverterStats stats, CancellationToken ct)
    {
        var run = GridChargeRun(plan.Today, DateTimeOffset.Now.Hour);
        if (run is not (int startHour, int endHour))
        {
            if (_chargeWindow is not null) await ClearChargeWindow(ct);
            return;
        }

        var start = new TimeOnly(startHour, 0);
        var end = endHour >= 24 ? new TimeOnly(23, 59) : new TimeOnly(endHour, 0);

        // Same run already programmed — leave the frozen stop-SOC alone so a
        // rising SOC doesn't cause a rewrite every minute.
        if (_chargeWindow is { } w && w.Start == start && w.End == end) return;

        int soc = plan.BatteryCapacityKwh <= 0 ? 100
            : (int)Math.Round(Math.Clamp(
                stats.BatterySoc + plan.PlannedGridChargeKwh / plan.BatteryCapacityKwh * 100, 0, 100));

        await _inverter.WriteChargeWindowAsync(start, end, soc, ct);
        _chargeWindow = (start, end, soc);
        SetAction($"Grid-charge window {start:HH\\:mm}-{end:HH\\:mm} -> {soc}%");
    }

    private async Task ClearChargeWindow(CancellationToken ct)
    {
        await _inverter.ClearChargeWindowAsync(ct);
        _chargeWindow = null;
        SetAction("Grid-charge window cleared");
    }

    // The block of consecutive "gridcharge" hours starting at the current hour,
    // as (startHour, endHourExclusive), or null if now isn't a gridcharge hour.
    private static (int Start, int End)? GridChargeRun(IReadOnlyList<PlanHour> today, int currentHour)
    {
        if (today.FirstOrDefault(h => h.Hour == currentHour)?.Action != "gridcharge")
            return null;
        int end = currentHour + 1;
        while (today.FirstOrDefault(h => h.Hour == end)?.Action == "gridcharge") end++;
        return (currentHour, end);
    }

    private static ushort? ModeForAction(string action) => action switch
    {
        "charge" => SolintegClient.ModePvCharging,
        "feedin" => SolintegClient.ModeFeedIn,
        "gridcharge" => SolintegClient.ModeEconomic,  // charge the battery from the cheap grid
        "idle" => SolintegClient.ModeGeneral,         // self-consume: battery covers the load
        _ => null,
    };

    private void SetAction(string msg)
    {
        _log.LogInformation("Automation: {Msg}", msg);
        _state.LogAction(msg);
    }
}
