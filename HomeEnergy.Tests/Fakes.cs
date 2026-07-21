using HomeEnergy.Configuration;
using HomeEnergy.Models;
using HomeEnergy.Services;

namespace HomeEnergy.Tests;

internal sealed class FakeSecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string> _values = new();

    public FakeSecretProvider Set(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
}

internal sealed class FakeSolintegClient : ISolintegClient
{
    public InverterStats? Stats { get; set; }
    public ushort? LastModeWritten { get; private set; }
    public int WriteCount { get; private set; }

    public Task<InverterStats?> ReadStatsAsync(CancellationToken ct) => Task.FromResult(Stats);

    public Task<double> ReadGenTodayKwhAsync(CancellationToken ct) =>
        Task.FromResult(Stats?.GenTodayKwh ?? 0);

    public Task WriteModeAsync(ushort value, CancellationToken ct)
    {
        LastModeWritten = value;
        WriteCount++;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadHoldingRawAsync(ushort start, ushort count, CancellationToken ct) =>
        Task.FromResult(new ushort[count]);

    public (TimeOnly Start, TimeOnly End, double Soc)? LastChargeWindow { get; private set; }
    public int ChargeWindowClears { get; private set; }

    public Task WriteChargeWindowAsync(TimeOnly start, TimeOnly end, double socPercent, CancellationToken ct)
    {
        LastChargeWindow = (start, end, socPercent);
        return Task.CompletedTask;
    }

    public Task ClearChargeWindowAsync(CancellationToken ct)
    {
        ChargeWindowClears++;
        LastChargeWindow = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeChargeAmpsClient : IChargeAmpsClient
{
    public ChargingSnapshot? Snapshot { get; set; }
    public Task<ChargingSnapshot?> GetStatusAsync(CancellationToken ct) => Task.FromResult(Snapshot);
}

internal sealed class FakePlanService : IPlanService
{
    public PlanSnapshot? Snapshot { get; set; }
    public PlanSnapshot? Build(PriceSnapshot? prices, SolarSnapshot? solar, InverterStats? inverter) => Snapshot;
}

internal sealed class FakeHistoryStore : IHistoryStore
{
    public int Logged { get; private set; }
    public void Init() { }
    public Task LogMinuteAsync(DateTimeOffset when, string mode, double solarW,
        double batteryPct, double gridW, double loadW, double? price, CancellationToken ct)
    {
        Logged++;
        return Task.CompletedTask;
    }
    public IReadOnlyList<HistoryHour> TodayHourly() => Array.Empty<HistoryHour>();
    public IReadOnlyList<HistoryHour> HourlyForDate(DateOnly date) => Array.Empty<HistoryHour>();
    public ExportTotals TodayExport() => new(0, 0);
}

/// <summary>Fails if a client is ever created — proves a code path short-circuits
/// before touching the network.</summary>
internal sealed class ThrowingHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        throw new InvalidOperationException("HTTP client should not be created on this path");
}
