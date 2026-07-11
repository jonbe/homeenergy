using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ITibberClient
{
    Task<PriceSnapshot?> FetchAsync(CancellationToken ct);
}
