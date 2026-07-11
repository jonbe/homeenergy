using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ISolarForecastService
{
    Task<SolarSnapshot?> FetchAsync(CancellationToken ct);
}
