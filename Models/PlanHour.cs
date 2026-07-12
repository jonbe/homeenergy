namespace HomeEnergy.Models;

// Action is one of: "charge" (PV-Charging), "feedin" (export), "idle".
public record PlanHour(int Hour, string Action, double EstKw, double? PriceHourly);
