using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface IHistoryStore
{
    void Init();

    Task LogMinuteAsync(DateTimeOffset when, string mode, double solarW,
        double batteryPct, double gridW, double loadW, double? price, CancellationToken ct);

    IReadOnlyList<HistoryHour> TodayHourly();

    /// <summary>Hourly rollup rows (0-23, gaps filled with empty rows) for the
    /// given local date. <see cref="TodayHourly"/> is this for today.</summary>
    IReadOnlyList<HistoryHour> HourlyForDate(DateOnly date);

    /// <summary>Today's exported energy and its value, summed from the raw
    /// minute samples (each ~1 sample/min, so W/60000 = kWh for that minute).
    /// More accurate than the hourly rollup, which nets import against export.</summary>
    ExportTotals TodayExport();
}
