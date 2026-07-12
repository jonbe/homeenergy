namespace HomeEnergy.Models;

public record SolarSnapshot(
    double PerformanceRatio,
    int CalibrationDays,
    double TodayKwh,
    double? TodayActualSoFarKwh,
    double? TodayBiasPct,       // + = running ahead of forecast
    double TomorrowKwh,
    IReadOnlyList<HourlyPv> TodayHourly,
    IReadOnlyList<HourlyPv> TomorrowHourly,
    DateTimeOffset FetchedAt);
