using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ISolintegClient
{
    Task<InverterStats?> ReadStatsAsync(CancellationToken ct);
    Task<double> ReadGenTodayKwhAsync(CancellationToken ct);
    Task WriteModeAsync(ushort value, CancellationToken ct);
}
