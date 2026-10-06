namespace HomeEnergy.Models;

public record PlanSnapshot(
    double BatteryCapacityKwh,
    double TodayDeficitKwh,          // kWh of sun still needed to reach 100% today
    double PlannedGridChargeKwh,     // kWh we plan to pull from grid into the battery today
    string StrategyNote,             // short human summary of what the plan does next
    IReadOnlyList<PlanSlot> Today,   // every 15-min slot of today
    double PlannedGridChargeTomorrowKwh, // kWh we plan to grid-charge tomorrow
    IReadOnlyList<PlanSlot> Tomorrow,    // every slot of tomorrow; empty until its prices are published
    DateTimeOffset FetchedAt);
