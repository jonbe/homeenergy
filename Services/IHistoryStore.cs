using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface IHistoryStore
{
    void Init();

    Task LogMinuteAsync(DateTimeOffset when, string mode, double solarW,
        double batteryPct, double gridW, double loadW, double? price, CancellationToken ct);

    IReadOnlyList<HistoryHour> TodayHourly();

    /// <summary>Today's exported energy and its value, summed from the raw
    /// minute samples (each ~1 sample/min, so W/60000 = kWh for that minute).
    /// More accurate than the hourly rollup, which nets import against export.</summary>
    ExportTotals TodayExport();
}
