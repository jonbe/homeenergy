namespace HomeEnergy.Models;

public record ConnectorStatus(
    int ConnectorId,
    string Name,
    string OcppStatus,
    bool IsCharging,
    double PowerKw,
    double Amps);
