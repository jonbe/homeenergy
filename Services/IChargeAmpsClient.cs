using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface IChargeAmpsClient
{
    Task<ChargingSnapshot?> GetStatusAsync(CancellationToken ct);
}
