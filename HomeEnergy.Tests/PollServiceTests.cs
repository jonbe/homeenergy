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
        BatteryPowerW: 0, LoadPowerW: 0, BatterySoc: 50, GenTodayKwh: 0,
        ModeRaw: $"0x{mode:X4}", ModeLabel: SolintegClient.ModeLabel(mode),
        ReadAt: DateTimeOffset.Now);

    private static ChargingSnapshot Charging(bool anyCharging) => new(
        "test-cp", Array.Empty<ConnectorStatus>(), anyCharging, DateTimeOffset.Now);

    // A plan whose current 15-minute slot carries the given action (rest of the day idle).
    private static PlanSnapshot PlanNow(string action)
    {
        var now = DateTimeOffset.Now;
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var today = Enumerable.Range(0, 96)
            .Select(q => midnight.AddMinutes(q * PlanSlot.Minutes))
            .Select(start => new PlanSlot(start,
                start <= now && now < start.AddMinutes(PlanSlot.Minutes) ? action : "idle", 1, 0.5, 0))
            .ToList();
        return new PlanSnapshot(19.2, 0, 0, "", today, 0, today, now);
    }

    private static PollService Build(FakeSolintegClient inv, FakeChargeAmpsClient ch,
        SystemState state, bool automation, FakePlanService? plan = null) =>
        new(inv, ch, plan ?? new FakePlanService(), new FakeHistoryStore(), state,
            Options.Create(new AppOptions { AutomationEnabled = automation }),
            NullLogger<PollService>.Instance);

    [Fact]
    public async Task PollOnce_SwitchesToBackup_WhenCarChargingAndAutomationOn()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: true) };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true).PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModeBackup, inv.LastModeWritten);
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
    public async Task PollOnce_FollowsPlan_WhenNotCharging()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: false) };
        var plan = new FakePlanService { Snapshot = PlanNow("feedin") };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true, plan).PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModeFeedIn, inv.LastModeWritten);
    }

    [Fact]
    public async Task PollOnce_LeavesModeAlone_WhenPlanAlreadyMatches()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeFeedIn) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: false) };
        var plan = new FakePlanService { Snapshot = PlanNow("feedin") };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true, plan).PollOnce(CancellationToken.None);

        Assert.Null(inv.LastModeWritten);
        Assert.Equal(0, inv.WriteCount);
    }

    [Fact]
    public async Task PollOnce_HoldsBatteryInBackup_WhenPlanSaysHold()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: false) };
        var plan = new FakePlanService { Snapshot = PlanNow("hold") };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true, plan).PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModeBackup, inv.LastModeWritten);
    }

    [Fact]
    public async Task PollOnce_CarChargingOverridesPlan()
    {
        var inv = new FakeSolintegClient { Stats = Stats(SolintegClient.ModeGeneral) };
        var ch = new FakeChargeAmpsClient { Snapshot = Charging(anyCharging: true) };
        var plan = new FakePlanService { Snapshot = PlanNow("feedin") };
        var state = new SystemState();

        await Build(inv, ch, state, automation: true, plan).PollOnce(CancellationToken.None);

        Assert.Equal(SolintegClient.ModeBackup, inv.LastModeWritten);
    }
}
