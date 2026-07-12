namespace HomeEnergy.Models;

/// <summary>A car connected to the Tibber account (manufacturer-cloud data).
/// Reports the car's own battery state of charge, not charger power.</summary>
public record Vehicle(
    string? Name,
    string? ShortName,
    double? BatteryPercent,
    bool? IsCharging,            // car-reported (independent of the wallbox)
    bool? IsAlive,               // false = Tibber can't currently reach the car
    DateTimeOffset? LastSeen);

public record VehicleSnapshot(
    IReadOnlyList<Vehicle> Vehicles,
    DateTimeOffset FetchedAt);
