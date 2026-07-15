using HomeEnergy.Models;

namespace HomeEnergy.Services;

/// <summary>Thread-safe holder for the latest snapshot from each subsystem.</summary>
public class SystemState
{
    public volatile InverterStats? Inverter;
    public volatile PriceSnapshot? Prices;
    public volatile ProductionSnapshot? Production;   // Tibber-reported, for comparison
    public volatile ChargingSnapshot? Charging;
    public volatile SolarSnapshot? Solar;
    public volatile VehicleSnapshot? Vehicles;
    public volatile ChargerSnapshot? Chargers;

    // Bounded history of automation/manual actions, newest first (for the UI).
    private const int MaxActions = 20;
    private readonly object _actionLock = new();
    private readonly LinkedList<ActionEntry> _actions = new();

    public void LogAction(string message)
    {
        lock (_actionLock)
        {
            _actions.AddFirst(new ActionEntry(message, DateTimeOffset.Now));
            while (_actions.Count > MaxActions) _actions.RemoveLast();
        }
    }

    public IReadOnlyList<ActionEntry> RecentActions(int count)
    {
        lock (_actionLock) return _actions.Take(count).ToList();
    }
}
