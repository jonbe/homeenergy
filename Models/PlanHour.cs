namespace HomeEnergy.Models;

// Action is one of: "charge" (PV-Charging), "feedin" (export), "idle"
// (General mode — self-consume from battery), "gridcharge" (import into the
// battery at a cheap hour).
public record PlanHour(int Hour, string Action, double EstKw, double? PriceHourly);
