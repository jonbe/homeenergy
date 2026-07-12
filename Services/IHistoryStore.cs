using HomeEnergy.Models;

namespace HomeEnergy.Services;

public interface IHistoryStore
{
    void Init();

    Task LogMinuteAsync(DateTimeOffset when, string mode, double solarW,
        double batteryPct, double gridW, double loadW, double? price, CancellationToken ct);

    IReadOnlyList<HistoryHour> TodayHourly();
}
