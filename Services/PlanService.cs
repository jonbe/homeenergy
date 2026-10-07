using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Derives a 15-minute plan from the price and PV forecasts. Rather than rules per
/// kind of day, it hands the whole forward timeline (rest of today, plus tomorrow
/// once its prices are out) to <see cref="BatteryDispatch"/>, which minimises cost:
/// the battery is held through cheap slots — the grid runs the house — and spent in
/// the priciest ones; it is grid-charged only where the spread beats the round-trip
/// loss, and PV is stored or exported by whichever is worth more. Suggestion only —
/// nothing here writes to the inverter.
/// </summary>
public class PlanService : IPlanService
{
    // Below this forecast PV output a slot isn't worth switching to a PV mode for:
    // plain General/Hold applies.
    private const double GeneralBelowKw = 2.0;

    private const int SlotsPerDay = 24 * 60 / PlanSlot.Minutes;

    private readonly BatteryOptions _bat;
    private readonly StrategyOptions _strat;
    private readonly TimeProvider _clock;
    private readonly IHistoryStore _history;

    // The house load profile is learned from this many days of history and
    // re-read this often (Build runs every minute; the profile barely moves).
    private const int ProfileDays = 14;
    private static readonly TimeSpan ProfileRefresh = TimeSpan.FromMinutes(15);
    private IReadOnlyList<double?>? _profile;
    private DateTimeOffset _profileAt;

    public PlanService(IOptions<AppOptions> opt, TimeProvider clock, IHistoryStore history)
    {
        _bat = opt.Value.Battery;
        _strat = opt.Value.Strategy;
        _clock = clock;
        _history = history;
    }

    public PlanSnapshot? Build(PriceSnapshot? prices, SolarSnapshot? solar, InverterStats? inverter)
    {
        if (solar is null) return null;
        var now = _clock.GetLocalNow();

        double cap = _bat.CapacityKwh;
        double soc = inverter?.BatterySoc ?? 0;
        double todayDeficit = Math.Max(0, (100 - soc) / 100.0 * cap);

        var today = now.Date;

        // Look prices up by timestamp across both lists: the snapshot's "Today" is
        // still yesterday's until the 00:05 refresh, and its "Tomorrow" is then the
        // real today. Which days have prices at all is read from the points too.
        var points = (prices?.Today ?? Array.Empty<PricePoint>())
            .Concat(prices?.Tomorrow ?? Array.Empty<PricePoint>())
            .OrderBy(p => p.StartsAt).ToList();
        var pricedDays = points
            .Select(p => TimeZoneInfo.ConvertTime(p.StartsAt, _clock.LocalTimeZone).Date)
            .ToHashSet();
        bool haveToday = pricedDays.Contains(today);
        bool haveTomorrow = pricedDays.Contains(today.AddDays(1));

        // Every slot of the planning days, in time order.
        var days = haveTomorrow ? new[] { today, today.AddDays(1) } : new[] { today };
        var profile = LoadProfile(now);
        var timeline = new List<(DateTimeOffset Start, DispatchSlot Slot, bool Priced)>();
        foreach (var day in days)
        {
            var pv = day == today ? solar.TodayHourly : solar.TomorrowHourly;
            for (int q = 0; q < SlotsPerDay; q++)
            {
                var local = day.AddMinutes(q * PlanSlot.Minutes);
                var start = new DateTimeOffset(local, _clock.LocalTimeZone.GetUtcOffset(local));
                timeline.Add((start, new DispatchSlot(
                    PriceAt(points, start) ?? 0,
                    pv.FirstOrDefault(h => h.Hour == local.Hour)?.EstKw ?? 0,
                    LoadKw(profile, local)), pricedDays.Contains(day)));
            }
        }

        // Plan from the quarter we are in; earlier slots are history.
        int first = timeline.FindIndex(t => t.Start.AddMinutes(PlanSlot.Minutes) > now);
        if (first < 0) first = timeline.Count;
        var future = timeline.Skip(first).ToList();

        var settings = new DispatchSettings(
            UsableKwh: _bat.UsableKwh,
            StartKwh: Math.Clamp(soc / 100.0 * cap - (cap - _bat.UsableKwh), 0, _bat.UsableKwh),
            LegEfficiency: Math.Sqrt(1 / (1 + _strat.MinArbitrageGapPct / 100.0)),
            MaxGridChargeKw: _strat.MaxGridChargeKw,
            ExportFactor: _strat.ExportPriceFactor,
            WearPerKwh: _strat.BatteryWearPerKwh,
            SwitchCost: _strat.ModeSwitchCost,
            StartMode: CurrentMode(inverter),
            TerminalPrice: future.Count == 0 ? 0 : Math.Max(0, future.Average(t => t.Slot.Price)));

        // Without today's prices there is nothing to optimise against (and every price
        // reading as 0 would make using the battery look like pure wear): plain General.
        var steps = haveToday
            ? BatteryDispatch.Optimize(future.Select(t => t.Slot).ToList(), settings)
            : future.Select(_ => new DispatchStep(BatteryMode.Discharge, 0)).ToArray();

        // Elapsed slots keep their price/PV for display but carry no action.
        var allSlots = new List<PlanSlot>(timeline.Count);
        for (int i = 0; i < timeline.Count; i++)
        {
            var (start, slot, priced) = timeline[i];
            double? price = priced ? slot.Price : null;
            allSlots.Add(i < first
                ? new PlanSlot(start, "past", slot.PvKw, price, 0)
                : new PlanSlot(start, ActionFor(steps[i - first].Mode, slot), slot.PvKw, price,
                    steps[i - first].BatteryKwh));
        }

        var todaySlots = allSlots.Take(SlotsPerDay).ToList();
        var tomorrowSlots = haveTomorrow ? allSlots.Skip(SlotsPerDay).ToList() : new List<PlanSlot>();

        return new PlanSnapshot(cap, todayDeficit,
            GridChargeKwh(todaySlots), Describe(allSlots.Where(s => s.Action != "past").ToList()),
            todaySlots, GridChargeKwh(tomorrowSlots), tomorrowSlots, now);
    }

    // House load for a slot: what this hour of the day has typically drawn over
    // the last two weeks, which captures the shape of the day (low at night,
    // peaks morning and evening) that decides how much battery to hold back.
    // Until there is enough history, the rough seasonal estimate from config
    // (summer Apr–Sep low, winter far higher) stands in.
    private double LoadKw(IReadOnlyList<double?> profile, DateTime local) =>
        profile[local.Hour]
        ?? (local.Month is >= 4 and <= 9 ? _strat.SummerLoadKwhPerHour : _strat.WinterLoadKwhPerHour);

    private IReadOnlyList<double?> LoadProfile(DateTimeOffset now)
    {
        if (_profile is null || now - _profileAt > ProfileRefresh)
        {
            _profile = _history.LoadProfileKw(ProfileDays);
            _profileAt = now;
        }
        return _profile;
    }

    // The battery behaviour the inverter is currently running, so that leaving it
    // counts as a mode switch in the optimizer.
    private static BatteryMode CurrentMode(InverterStats? inverter)
    {
        if (inverter is null || !int.TryParse(inverter.ModeRaw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int mode))
            return BatteryMode.Discharge;
        return mode switch
        {
            SolintegClient.ModeBackup => BatteryMode.Hold,
            SolintegClient.ModeEconomic => BatteryMode.GridCharge,
            _ => BatteryMode.Discharge,
        };
    }

    // Price of the quarter containing `at`: the latest point that has started.
    // Works for hourly points too (each simply spans four slots).
    private static double? PriceAt(IReadOnlyList<PricePoint> points, DateTimeOffset at)
    {
        PricePoint? hit = null;
        foreach (var p in points)
            if (p.StartsAt <= at && (hit is null || p.StartsAt > hit.StartsAt)) hit = p;
        return hit?.Total ?? points.MinBy(p => p.StartsAt)?.Total;
    }

    // Translate the optimizer's battery behaviour into the inverter action that
    // produces it. PV-Charging / Feed-In are only worth selecting when the sun is
    // really out and covering the house; otherwise General / Backup do the job.
    private static string ActionFor(BatteryMode mode, DispatchSlot slot)
    {
        bool sunnySurplus = slot.PvKw >= GeneralBelowKw && slot.PvKw > slot.LoadKw;
        return mode switch
        {
            BatteryMode.GridCharge => "gridcharge",
            BatteryMode.Hold => sunnySurplus ? "feedin" : "hold",
            _ => sunnySurplus ? "charge" : "idle",
        };
    }

    private static double GridChargeKwh(IEnumerable<PlanSlot> slots) =>
        slots.Where(s => s.Action == "gridcharge").Sum(s => s.BatteryKwh);

    // One-line summary of the next few things the plan does over the coming 24 h:
    // runs of holding the battery, spending it, or grid-charging, with their average
    // price. Only worth saying when the plan is doing something beyond plain General.
    private static string Describe(IReadOnlyList<PlanSlot> upcoming)
    {
        var slots = upcoming.Take(SlotsPerDay).ToList();
        if (!slots.Any(s => s.Action is "hold" or "gridcharge"))
            return "No price arbitrage ahead — running on solar and battery as usual";

        static string? Label(PlanSlot s) => s.Action switch
        {
            "hold" => "hold battery",
            "gridcharge" => "grid-charge",
            "idle" or "charge" when s.BatteryKwh < 0 => "use battery",
            _ => null,
        };

        var runs = new List<string>();
        int i = 0;
        while (i < slots.Count && runs.Count < 4)
        {
            var label = Label(slots[i]);
            int j = i + 1;
            while (label is not null && j < slots.Count && Label(slots[j]) == label) j++;
            if (label is not null)
            {
                var run = slots.Skip(i).Take(j - i).ToList();
                var price = run.Where(s => s.Price is not null).Select(s => s.Price!.Value).DefaultIfEmpty().Average();
                runs.Add($"{label} {run[0].Start:HH\\:mm}–{run[^1].End:HH\\:mm} @ {price:F2}");
            }
            i = j;
        }
        return "Plan: " + string.Join(" · ", runs);
    }
}
