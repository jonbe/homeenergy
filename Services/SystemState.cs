using HomeEnergy.Models;

namespace HomeEnergy.Services;

/// <summary>Thread-safe holder for the latest snapshot from each subsystem.</summary>
public class SystemState
{
    public volatile InverterStats? Inverter;
    public volatile PriceSnapshot? Prices;
    public volatile ChargingSnapshot? Charging;
    public volatile SolarSnapshot? Solar;

    // Last automation action taken by the charging poller (for the UI).
    public volatile string? LastAction;
    public DateTimeOffset? LastActionAt;
}
