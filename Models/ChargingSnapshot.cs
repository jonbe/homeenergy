namespace HomeEnergy.Models;

public record ChargingSnapshot(
    string ChargePoint,
    IReadOnlyList<ConnectorStatus> Connectors,
    bool AnyCharging,
    DateTimeOffset PolledAt);
