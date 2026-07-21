namespace HomeEnergy.Models;

public record PlanSnapshot(
    double BatteryCapacityKwh,
    double TodayDeficitKwh,          // kWh of sun still needed to reach 100% today
    bool LowSunDay,                  // today's PV forecast is below the low-sun threshold
    double PlannedGridChargeKwh,     // kWh we plan to pull from grid into the battery today
    string StrategyNote,             // short human summary of the chosen strategy
    IReadOnlyList<PlanHour> Today,
    IReadOnlyList<PlanHour> Tomorrow,
    DateTimeOffset FetchedAt);
