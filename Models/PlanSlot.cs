namespace HomeEnergy.Models;

// One 15-minute step of the plan. Action is one of:
//   "charge"     PV-Charging — sun fills the battery
//   "feedin"     export PV, battery untouched
//   "idle"       General — self-consume, the battery covers the house
//   "hold"       Backup — the grid runs the house, battery kept for pricier slots
//   "gridcharge" Economic window — import into the battery at a cheap slot
//   "past"       already elapsed (display only)
// BatteryKwh is the planned change in stored energy over the slot (+ charge, − discharge).
public record PlanSlot(DateTimeOffset Start, string Action, double EstKw, double? Price, double BatteryKwh)
{
    public const int Minutes = 15;

    public DateTimeOffset End => Start.AddMinutes(Minutes);
}
