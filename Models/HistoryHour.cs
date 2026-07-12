namespace HomeEnergy.Models;

// One hour of logged actuals (aggregated from minute samples). Mode is the
// dominant working mode during the hour; the rest are averages. Null fields
// mean no data was logged for that hour yet.
public record HistoryHour(
    int Hour,
    string? Mode,
    double? SolarW,
    double? BatteryPct,
    double? GridW,
    double? LoadW,
    double? Price);
