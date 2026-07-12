namespace HomeEnergy.Models;

/// <summary>A Charge Amps wallbox connector as seen through the Tibber Data API.
/// Read-only telemetry; control still goes through the Charge Amps portal client.
/// Power/current fields are only populated while the connector is charging.</summary>
public record Charger(
    string? Name,
    string? Model,
    string? ConnectorStatus,     // connected / disconnected / unknown
    bool? IsCharging,            // charging.status == "charging"
    double? PowerW,              // powerFlow.toBattery.power
    double? CurrentA,            // charging.current.effective
    double? MaxCurrentA,         // charging.current.max
    int? PhaseCount,
    bool? IsOnline,
    DateTimeOffset? LastSeen);

public record ChargerSnapshot(
    IReadOnlyList<Charger> Chargers,
    DateTimeOffset FetchedAt);

/// <summary>Everything one pass over the Tibber Data API devices yields.</summary>
public record TibberSnapshot(
    VehicleSnapshot Vehicles,
    ChargerSnapshot Chargers);
