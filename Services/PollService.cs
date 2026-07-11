using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Every 60 s: read inverter stats + charging status. When automation is
/// enabled and a car is charging, switch the inverter to PV-Charging so the
/// house battery is not drained into the car; the previous mode is restored
/// when charging stops.
///
/// NOTE: writing the mode register conflicts with the Solinteg app/cloud
/// schema. Automation is OFF by default (AutomationEnabled) — enable only if
/// you drive the mode locally rather than via the app schedule.
/// </summary>
public class PollService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly SolintegClient _inverter;
    private readonly ChargeAmpsClient _chargeAmps;
    private readonly SystemState _state;
    private readonly AppOptions _opt;
    private readonly ILogger<PollService> _log;
    private ushort? _restoreMode;   // mode to restore when charging stops

    public PollService(SolintegClient inverter, ChargeAmpsClient chargeAmps,
        SystemState state, IOptions<AppOptions> opt, ILogger<PollService> log)
    {
        _inverter = inverter;
        _chargeAmps = chargeAmps;
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

    private async Task PollOnce(CancellationToken ct)
    {
        var stats = await _inverter.ReadStatsAsync(ct);
        if (stats is not null) _state.Inverter = stats;

        var charging = await _chargeAmps.GetStatusAsync(ct);
        if (charging is not null) _state.Charging = charging;

        if (_opt.AutomationEnabled && charging is not null && stats is not null)
            await ApplyAutomation(charging, stats, ct);
    }

    private async Task ApplyAutomation(ChargingSnapshot charging, InverterStats stats, CancellationToken ct)
    {
        int currentMode = Convert.ToInt32(stats.ModeRaw, 16);

        if (charging.AnyCharging && currentMode != SolintegClient.ModePvCharging)
        {
            _restoreMode = (ushort)currentMode;
            await _inverter.WriteModeAsync(SolintegClient.ModePvCharging, ct);
            SetAction($"Car charging -> set PV-Charging (was {SolintegClient.ModeLabel(currentMode)})");
        }
        else if (!charging.AnyCharging && _restoreMode is ushort prev
                 && currentMode == SolintegClient.ModePvCharging)
        {
            await _inverter.WriteModeAsync(prev, ct);
            SetAction($"Charging stopped -> restored {SolintegClient.ModeLabel(prev)}");
            _restoreMode = null;
        }
    }

    private void SetAction(string msg)
    {
        _log.LogInformation("Automation: {Msg}", msg);
        _state.LastAction = msg;
        _state.LastActionAt = DateTimeOffset.Now;
    }
}
