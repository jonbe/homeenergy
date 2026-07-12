namespace HomeEnergy.Models;

public record PlanSnapshot(
    double BatteryCapacityKwh,
    double TodayDeficitKwh,     // kWh of sun still needed to reach 100% today
    IReadOnlyList<PlanHour> Today,
    IReadOnlyList<PlanHour> Tomorrow,
    DateTimeOffset FetchedAt);
