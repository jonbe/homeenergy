namespace HomeEnergy.Models;

public record InverterStats(
    double PvPowerW,
    double Pv1PowerW,
    double Pv2PowerW,
    double InverterAcW,
    double GridPowerW,      // + import from grid, - export to grid
    double BatteryPowerW,   // + charging, - discharging
    double BatterySoc,      // %
    double GenTodayKwh,
    string ModeRaw,         // hex of register 50000
    string ModeLabel,
    DateTimeOffset ReadAt);

public record PricePoint(DateTimeOffset StartsAt, double Total, string Level);

public record PriceSnapshot(
    string Currency,
    PricePoint? Current,
    IReadOnlyList<PricePoint> Today,
    IReadOnlyList<PricePoint> Tomorrow,
    DateTimeOffset FetchedAt);

public record ConnectorStatus(
    int ConnectorId,
    string Name,
    string OcppStatus,
    bool IsCharging,
    double PowerKw,
    double Amps);

public record ChargingSnapshot(
    string ChargePoint,
    IReadOnlyList<ConnectorStatus> Connectors,
    bool AnyCharging,
    DateTimeOffset PolledAt);

public record HourlyPv(int Hour, double EstKw, double? CloudPct);

public record SolarSnapshot(
    double PerformanceRatio,
    int CalibrationDays,
    double TodayKwh,
    double? TodayActualSoFarKwh,
    double? TodayBiasPct,       // + = running ahead of forecast
    double TomorrowKwh,
    IReadOnlyList<HourlyPv> TodayHourly,
    DateTimeOffset FetchedAt);
