using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface ITibberClient
{
    Task<PriceSnapshot?> FetchAsync(CancellationToken ct);

    /// <summary>Today's exported energy and the payment received for it, as
    /// reported by Tibber's GraphQL production connection. Not real-time
    /// (Tibber updates it on a delay), so fetch on a modest cadence.</summary>
    Task<ProductionSnapshot?> FetchProductionAsync(CancellationToken ct);
}
