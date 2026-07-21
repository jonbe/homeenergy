using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ISolintegClient
{
    Task<InverterStats?> ReadStatsAsync(CancellationToken ct);
    Task<double> ReadGenTodayKwhAsync(CancellationToken ct);
    Task WriteModeAsync(ushort value, CancellationToken ct);

    /// <summary>Read-only diagnostic: dump a raw holding-register range so we can
    /// inspect undocumented tables (e.g. the ToU charge-period schedule).</summary>
    Task<ushort[]> ReadHoldingRawAsync(ushort start, ushort count, CancellationToken ct);

    /// <summary>Program one grid-charge window (PV+GRID) into the Economic-mode
    /// schedule. Requires Economic mode to be active to take effect.</summary>
    Task WriteChargeWindowAsync(TimeOnly start, TimeOnly end, double socPercent, CancellationToken ct);

    /// <summary>Clear the first charge period back to the empty (all-zero) state.</summary>
    Task ClearChargeWindowAsync(CancellationToken ct);
}
