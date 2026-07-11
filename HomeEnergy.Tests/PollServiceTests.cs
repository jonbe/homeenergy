using HomeEnergy.Configuration;
using HomeEnergy.Models;
using HomeEnergy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Tests;

public class PollServiceTests
{
    private static InverterStats Stats(int mode) => new(
        PvPowerW: 0, Pv1PowerW: 0, Pv2PowerW: 0, InverterAcW: 0, GridPowerW: 0,
        BatteryPowerW: 0, BatterySoc: 50, GenTodayKwh: 0,
        ModeRaw: $"0x{mode:X4}", ModeLabel: SolintegClient.ModeLabel(mode),
        ReadAt: DateTimeOffset.Now);

    private static ChargingSnapshot Charging(bool anyCharging) => new(
        "test-cp", Array.Empty<ConnectorStatus>(), anyCharging, DateTimeOffset.Now);

    private static PollService Build(FakeSolintegClient inv, FakeChargeAmpsClient ch,
        SystemState state, bool automation) =>
        new(inv, ch, state, Options.Create(new AppOptions { AutomationEnabled = automation }),
            NullLogger<PollService>.Instance);

    [Fact]
    public async Task PollOnce_SwitchesToPvCharging_WhenCarChargingAndAutomationOn()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: true) };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true).PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModePvCharging, inv.LastModeWritten);
        Assert.Same(ch.Snapshot, state.Charging);
        Assert.Same(inv.Stats, state.Inverter);
    }

    [Fact]
    public async Task PollOnce_DoesNotWriteMode_WhenAutomationOff()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: true) };
        var state = new SystemState();

        await Build(inv, ch, state, automation: false).PollOnce(CancellationToken.None);

        Assert.Null(inv.LastModeWritten);
        Assert.Same(ch.Snapshot, state.Charging);   // state still updated even with automation off
    }

    [Fact]
    public async Task PollOnce_RestoresPreviousMode_WhenChargingStops()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: true) };
        var state = new SystemState();
        var svc = Build(inv, ch, state, automation: true);

        await svc.PollOnce(CancellationToken.None);              // charging -> PV-Charging
        Assert.Equal(SolintegClient.ModePvCharging, inv.LastModeWritten);

        inv.Stats = Stats(SolintegClient.ModePvCharging);        // inverter now reflects PV-Charging
        ch.Snapshot = Charging(anyCharging: false);              // charging stopped
        await svc.PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModeGeneral, inv.LastModeWritten);   // restored
        Assert.Equal(2, inv.WriteCount);
    }
}
