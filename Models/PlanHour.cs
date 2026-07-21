namespace HomeEnergy.Models;

// Action is one of: "charge" (PV-Charging), "feedin" (export), "idle",
// "gridcharge" (import into battery at a cheap hour), "discharge" (ride an
// expensive hour on battery instead of importing).
public record PlanHour(int Hour, string Action, double EstKw, double? PriceHourly);
