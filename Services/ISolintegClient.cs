using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ISolintegClient
{
    Task<InverterStats?> ReadStatsAsync(CancellationToken ct);
    Task<double> ReadGenTodayKwhAsync(CancellationToken ct);
    Task WriteModeAsync(ushort value, CancellationToken ct);

    /// <summary>Entering Backup pins the max-charge SOC and leaving it through
    /// <see cref="WriteModeAsync"/> restores it. If the inverter left Backup some
    /// other way (a restart, the Solinteg app) the pin would stay forever; call this
    /// with the mode just read to restore the ceiling in that case. Returns true if
    /// it had to.</summary>
    Task<bool> ReleaseStaleChargeCapAsync(int currentMode, CancellationToken ct);

    /// <summary>Read-only diagnostic: dump a raw holding-register range so we can
    /// inspect undocumented tables (e.g. the ToU charge-period schedule).</summary>
    Task<ushort[]> ReadHoldingRawAsync(ushort start, ushort count, CancellationToken ct);

    /// <summary>Program one grid-charge window (PV+GRID) into the Economic-mode
    /// schedule. Requires Economic mode to be active to take effect.</summary>
    Task WriteChargeWindowAsync(TimeOnly start, TimeOnly end, double socPercent, CancellationToken ct);

    /// <summary>Clear the first charge period back to the empty (all-zero) state.</summary>
    Task ClearChargeWindowAsync(CancellationToken ct);
}
