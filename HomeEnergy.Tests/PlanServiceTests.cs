using HomeEnergy.Configuration;
using HomeEnergy.Models;
using HomeEnergy.Services;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Tests;

public class PlanServiceTests
{
    // "Tonight": a little after midnight, the free night still ahead of us.
    private static readonly DateTimeOffset Midnight = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset JustAfterMidnight = Midnight.AddMinutes(10);

    // 19.2 kWh pack with 15 kWh usable (the production defaults); 1 kW house load.
    private static PlanService Planner(DateTimeOffset now, double maxGridChargeKw = 5) =>
        new(Options.Create(new AppOptions
        {
            Battery = new BatteryOptions { CapacityKwh = 19.2, UsableKwh = 15 },
            Strategy = new StrategyOptions
            {
                SummerLoadKwhPerHour = 1.0, WinterLoadKwhPerHour = 1.0,
                MinArbitrageGapPct = 15, MaxGridChargeKw = maxGridChargeKw, ExportPriceFactor = 0.6,
            },
        }), new FixedClock(now));

    private static InverterStats Inverter(double soc) => new(
        PvPowerW: 0, Pv1PowerW: 0, Pv2PowerW: 0, InverterAcW: 0, GridPowerW: 0,
        BatteryPowerW: 0, LoadPowerW: 0, BatterySoc: soc, GenTodayKwh: 0,
        ModeRaw: "0x0101", ModeLabel: "General", ReadAt: Midnight);

    // Quarter-hourly prices for a calendar day, from a function of (hour, quarter 0..3).
    private static List<PricePoint> Prices(DateTimeOffset day, Func<int, int, double> price) =>
        Enumerable.Range(0, 96)
            .Select(q => new PricePoint(day.AddMinutes(q * 15), price(q / 4, q % 4), "NORMAL"))
            .ToList();

    private static PriceSnapshot PricesFor(Func<int, int, double> shape) =>
        new("SEK", null, Prices(Midnight, shape), Prices(Midnight.AddDays(1), shape), Midnight);

    private static SolarSnapshot Solar(Func<int, double> kw)
    {
        var day = Enumerable.Range(0, 24).Select(h => new HourlyPv(h, kw(h), null)).ToList();
        return new SolarSnapshot(0.9, 10, 0, null, null, 0, day, day, Midnight);
    }

    private static PlanSlot SlotAt(PlanSnapshot plan, int hour, int quarter = 0) =>
        plan.Today.Single(s => s.Start.Hour == hour && s.Start.Minute == quarter * 15);

    // Free overnight, a single-quarter spike at 08:00, expensive shoulders after it.
    private static double SpikeDay(int hour, int quarter) =>
        hour < 8 ? 0.05
        : hour == 8 && quarter == 0 ? 5.0
        : hour < 22 ? 1.5
        : 0.5;

    [Fact]
    public void FreeNight_BatteryIsNotDrainedBeforeTheSpike()
    {
        // A modest charge (~2.5 kWh usable) against 8 h of 1 kW load: the old plan ran
        // General all night, so the battery was gone long before 08:00.
        var plan = Planner(JustAfterMidnight, maxGridChargeKw: 0)
            .Build(PricesFor(SpikeDay), Solar(_ => 0), Inverter(soc: 35))!;

        var night = plan.Today.Where(s => s.Action != "past" && s.Start.Hour < 8).ToList();
        Assert.NotEmpty(night);
        Assert.All(night, s => Assert.Equal("hold", s.Action));

        // The saved energy goes into the spike quarter itself.
        var spike = SlotAt(plan, 8, 0);
        Assert.Equal("idle", spike.Action);
        Assert.True(spike.BatteryKwh < 0, "battery should discharge into the spike");
    }

    [Fact]
    public void FreeNight_GridChargesInsteadOfUsingTheBattery_WhenItCan()
    {
        var plan = Planner(JustAfterMidnight)
            .Build(PricesFor(SpikeDay), Solar(_ => 0), Inverter(soc: 35))!;

        // Free grid power: the battery is topped up before the spike and never used overnight.
        var night = plan.Today.Where(s => s.Action != "past" && s.Start.Hour < 8).ToList();
        Assert.All(night, s => Assert.Contains(s.Action, new[] { "hold", "gridcharge" }));
        Assert.True(plan.PlannedGridChargeKwh > 5, "should import a meaningful amount of free energy");
        Assert.Equal("idle", SlotAt(plan, 8, 0).Action);
    }

    [Fact]
    public void SpikeThatIsOnlyOneQuarter_IsNotAveragedAway()
    {
        // ~0.3 kWh usable: enough for exactly one quarter of the 1 kW load. It has to go
        // to the 5.0 quarter; an hourly average (~2.4 for 08:00-09:00) would have blurred it.
        var plan = Planner(JustAfterMidnight, maxGridChargeKw: 0)
            .Build(PricesFor(SpikeDay), Solar(_ => 0), Inverter(soc: 23.5))!;

        Assert.True(SlotAt(plan, 8, 0).BatteryKwh <= -0.25, "the whole quarter should come from the battery");
        Assert.All(plan.Today.Where(s => s.Action != "past" && s.Start.Hour < 8),
            s => Assert.Equal("hold", s.Action));
    }

    [Fact]
    public void FlatPrices_JustRunGeneral()
    {
        var plan = Planner(JustAfterMidnight).Build(PricesFor((_, _) => 1.0), Solar(_ => 0), Inverter(soc: 90))!;

        Assert.All(plan.Today.Where(s => s.Action != "past"), s => Assert.Equal("idle", s.Action));
        Assert.Equal(0, plan.PlannedGridChargeKwh);
    }

    [Fact]
    public void CheapNightBeforeExpensiveDay_GridChargesAnEmptyBattery()
    {
        double Shape(int h, int q) => h < 5 ? 0.1 : 2.5;

        var plan = Planner(JustAfterMidnight).Build(PricesFor(Shape), Solar(_ => 0), Inverter(soc: 22))!;

        Assert.True(plan.PlannedGridChargeKwh > 5);
        Assert.All(plan.Today.Where(s => s.Action == "gridcharge"), s => Assert.True(s.Start.Hour < 5));
    }

    [Fact]
    public void SunnyDay_ChargesFromTheSun()
    {
        // 6 kW of sun 10–14 against a 1 kW house: the surplus fills the battery.
        var plan = Planner(JustAfterMidnight)
            .Build(PricesFor((_, _) => 1.0), Solar(h => h is >= 10 and < 14 ? 6 : 0), Inverter(soc: 30))!;

        Assert.Equal("charge", SlotAt(plan, 12).Action);
        // The sun is free: never buy from the grid to fill a battery the sun will fill.
        Assert.DoesNotContain(plan.Today, s => s.Action == "gridcharge");
    }

    [Fact]
    public void Tomorrow_IsEmptyUntilItsPricesArePublished()
    {
        var todayOnly = new PriceSnapshot("SEK", null, Prices(Midnight, SpikeDay), Array.Empty<PricePoint>(), Midnight);

        var plan = Planner(JustAfterMidnight).Build(todayOnly, Solar(_ => 0), Inverter(soc: 50))!;

        Assert.Empty(plan.Tomorrow);
        Assert.Equal(96, plan.Today.Count);
    }

    [Fact]
    public void StaleSnapshotBefore0005Refresh_StillPlansAgainstTodaysRealPrices()
    {
        // Just after midnight the snapshot's "Today" is still yesterday's flat prices and
        // its "Tomorrow" is the real today (with the 08:00 spike).
        var stale = new PriceSnapshot("SEK", null,
            Prices(Midnight.AddDays(-1), (_, _) => 1.0), Prices(Midnight, SpikeDay), Midnight);

        var plan = Planner(Midnight.AddMinutes(2), maxGridChargeKw: 0)
            .Build(stale, Solar(_ => 0), Inverter(soc: 35))!;

        Assert.Equal("idle", SlotAt(plan, 8, 0).Action);
        Assert.Equal("hold", SlotAt(plan, 3, 0).Action);
        Assert.Empty(plan.Tomorrow);   // the real tomorrow isn't published yet
    }

    [Fact]
    public void NoPrices_FallsBackToPlainGeneral()
    {
        var plan = Planner(JustAfterMidnight).Build(null, Solar(_ => 0), Inverter(soc: 50))!;

        Assert.All(plan.Today.Where(s => s.Action != "past"), s => Assert.Equal("idle", s.Action));
        Assert.Empty(plan.Tomorrow);
    }

    [Fact]
    public void ElapsedSlotsAreMarkedPast()
    {
        var plan = Planner(Midnight.AddHours(12)).Build(PricesFor(SpikeDay), Solar(_ => 0), Inverter(soc: 50))!;

        Assert.Equal("past", SlotAt(plan, 8, 0).Action);
        Assert.NotEqual("past", SlotAt(plan, 12, 0).Action);
    }

    [Fact]
    public void Build_ReturnsNull_WithoutSolarForecast() =>
        Assert.Null(Planner(Midnight).Build(null, null, Inverter(50)));
}

public class BatteryDispatchTests
{
    private static DispatchSettings Settings(double startKwh, double maxGridKw = 5) =>
        new(UsableKwh: 15, StartKwh: startKwh, LegEfficiency: 0.93, MaxGridChargeKw: maxGridKw,
            ExportFactor: 0.6, TerminalPrice: 1.0);

    private static DispatchSlot Slot(double price, double pvKw = 0, double loadKw = 1) => new(price, pvKw, loadKw);

    [Fact]
    public void SpendsTheBatteryInThePriciestSlots()
    {
        // Only ~0.5 kWh stored — enough for two quarters of load. Must pick the two dearest.
        var slots = new[] { Slot(1.0), Slot(1.0), Slot(4.0), Slot(1.0), Slot(3.0), Slot(1.0) };

        var plan = BatteryDispatch.Optimize(slots, Settings(startKwh: 0.55, maxGridKw: 0));

        Assert.Equal(BatteryMode.Hold, plan[0].Mode);
        Assert.Equal(BatteryMode.Hold, plan[1].Mode);
        Assert.Equal(BatteryMode.Hold, plan[3].Mode);
        Assert.True(plan[2].BatteryKwh <= -0.25, "the 4.0 slot is served from the battery");
        Assert.True(plan[4].BatteryKwh <= -0.25, "and so is the 3.0 slot");
    }

    [Fact]
    public void ChargesFromTheGridOnlyWhenTheSpreadBeatsTheRoundTripLoss()
    {
        var thin = new[] { Slot(1.00), Slot(1.05) };    // 5% spread < ~15% loss
        var wide = new[] { Slot(1.00), Slot(2.00) };

        Assert.DoesNotContain(BatteryDispatch.Optimize(thin, Settings(0) with { TerminalPrice = 0 }),
            p => p.Mode == BatteryMode.GridCharge);
        Assert.Contains(BatteryDispatch.Optimize(wide, Settings(0) with { TerminalPrice = 0 }),
            p => p.Mode == BatteryMode.GridCharge);
    }

    [Fact]
    public void DischargeCreditsOnlyWhatTheBatteryActuallyGave()
    {
        // 15 kWh usable at 93% leg efficiency delivers 13.95 kWh to a 1 kW house, not 15:
        // the house gets eta × what the level dropped, never more.
        var slots = Enumerable.Range(0, 96).Select(_ => Slot(3.0)).ToArray();

        var plan = BatteryDispatch.Optimize(slots, Settings(startKwh: 15, maxGridKw: 0) with { TerminalPrice = 0 });

        double delivered = plan.Sum(p => Math.Min(0.25, -p.BatteryKwh * 0.93));
        Assert.Equal(-15, plan.Sum(p => p.BatteryKwh), 2);
        Assert.InRange(delivered, 13.9, 14.0);
    }

    // Today's real hourly shape (SEK/kWh, 2026-10-06): free night, 1.35 spike at 08,
    // cheap midday, 1.5–1.8 evening — with a few öre of quarter-to-quarter noise.
    private static readonly double[] RealDay =
        { 0.12, 0.12, 0.13, 0.12, 0.12, 0.13, 0.08, 0.12, 1.35, 0.22, 0.20, 0.17,
          0.16, 0.16, 0.16, 0.21, 0.46, 1.44, 1.73, 1.67, 1.68, 1.78, 1.55, 1.53 };

    private static DispatchSlot[] RealDaySlots(double loadKw) =>
        Enumerable.Range(0, 96)
            .Select(q => Slot(RealDay[q / 4] + (q % 3 - 1) * 0.01, loadKw: loadKw))
            .ToArray();

    [Theory]
    [InlineData(1.0, 5.4)]
    [InlineData(3.0, 5.4)]
    [InlineData(3.0, 13.0)]
    public void SwitchCost_RemovesOneSlotIslands(double loadKw, double startKwh)
    {
        var s = Settings(startKwh) with { SwitchCost = 0.05, TerminalPrice = RealDay.Average() };

        var plan = BatteryDispatch.Optimize(RealDaySlots(loadKw), s);

        var modes = plan.Select(p => p.Mode).ToArray();
        for (int i = 1; i < modes.Length - 1; i++)
            Assert.False(modes[i] != modes[i - 1] && modes[i] != modes[i + 1],
                $"one-slot island of {modes[i]} at slot {i}");
        int changes = modes.Zip(modes.Skip(1)).Count(x => x.First != x.Second);
        Assert.InRange(changes, 1, 6);
    }

    [Fact]
    public void SwitchCost_StillCatchesARealSpike()
    {
        // A one-quarter 5.0 spike against ~1 kWh of battery: worth two switches.
        var slots = Enumerable.Range(0, 8).Select(q => Slot(q == 4 ? 5.0 : 0.1)).ToArray();

        var plan = BatteryDispatch.Optimize(slots, Settings(startKwh: 0.3, maxGridKw: 0) with { SwitchCost = 0.05, TerminalPrice = 0 });

        Assert.Equal(BatteryMode.Discharge, plan[4].Mode);
        Assert.True(plan[4].BatteryKwh < 0);
        Assert.All(plan.Take(4), p => Assert.Equal(BatteryMode.Hold, p.Mode));
    }

    [Fact]
    public void StartMode_LeavingTheCurrentModeCountsAsASwitch()
    {
        // Flat prices: Hold and Discharge cost the same (nothing to gain either way), so
        // an inverter already holding should keep holding rather than pay a switch.
        var slots = Enumerable.Range(0, 4).Select(_ => Slot(1.0, loadKw: 0)).ToArray();

        var plan = BatteryDispatch.Optimize(slots, Settings(startKwh: 5) with { SwitchCost = 0.05, StartMode = BatteryMode.Hold });

        Assert.All(plan, p => Assert.Equal(BatteryMode.Hold, p.Mode));
    }

    [Fact]
    public void NeverPlansPastEmptyOrFull()
    {
        var slots = Enumerable.Range(0, 60).Select(i => Slot(i % 2 == 0 ? 0.1 : 3.0)).ToArray();

        var plan = BatteryDispatch.Optimize(slots, Settings(startKwh: 7));

        double level = 7;
        foreach (var step in plan)
        {
            level += step.BatteryKwh;
            Assert.InRange(level, -1e-9, 15 + 1e-9);
        }
    }
}
