using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Derives an hourly plan. On sunny days: fill the battery from the cheapest
/// remaining sun and export the surplus during the priciest sunny hours. On
/// low-sun days (PV forecast below <see cref="StrategyOptions.LowSunKwh"/>) it
/// switches to price arbitrage — grid-charge the battery during the cheapest
/// hours so it can ride the expensive hours until the next cheap window,
/// instead of importing at peak. Suggestion only — nothing here writes to the
/// inverter.
/// </summary>
public class PlanService : IPlanService
{
    // Below this forecast PV output an hour isn't worth acting on: run General.
    private const double GeneralBelowKw = 2.0;

    private readonly BatteryOptions _bat;
    private readonly StrategyOptions _strat;

    public PlanService(IOptions<AppOptions> opt)
    {
        _bat = opt.Value.Battery;
        _strat = opt.Value.Strategy;
    }

    public PlanSnapshot? Build(PriceSnapshot? prices, SolarSnapshot? solar, InverterStats? inverter)
    {
        if (solar is null) return null;
        var now = DateTimeOffset.Now;

        double cap = _bat.CapacityKwh;
        double soc = inverter?.BatterySoc ?? 0;
        double todayDeficit = Math.Max(0, (100 - soc) / 100.0 * cap);

        bool lowSun = solar.TodayKwh <= _strat.LowSunKwh;

        List<PlanHour> today;
        double gridChargeKwh = 0;
        string note;

        var todayPrices = HourlyPrices(prices?.Today);
        var tomorrowPrices = HourlyPrices(prices?.Tomorrow);

        if (lowSun && todayPrices.Count > 0)
        {
            // Energy currently drawable from the pack, above the reserve floor.
            double available = Math.Max(0, soc / 100.0 * cap - (cap - _bat.UsableKwh));
            var arb = PlanArbitrage(solar.TodayHourly, todayPrices, tomorrowPrices,
                available, LoadKwhPerHour(now.Month), now.Hour, solar.TodayKwh);
            today = arb.Plan;
            gridChargeKwh = arb.GridChargeKwh;
            note = arb.Note;
        }
        else
        {
            today = PlanDay(solar.TodayHourly, todayPrices, todayDeficit, now.Hour);
            note = lowSun
                ? $"Low sun ({solar.TodayKwh:F0} kWh) but no prices yet — following solar plan"
                : $"Solar day ({solar.TodayKwh:F0} kWh) — charge from sun, export surplus";
        }

        // Tomorrow: assume the battery drains overnight, so it must charge a full
        // capacity from the coming day's sun. (Arbitrage for tomorrow is future work.)
        var tomorrow = PlanDay(solar.TomorrowHourly, tomorrowPrices, cap, fromHour: 0);

        return new PlanSnapshot(cap, todayDeficit, lowSun, gridChargeKwh, note, today, tomorrow, now);
    }

    // Summer (Apr–Sep) draws little; winter far more. A rough per-hour average
    // is enough to size how much must carry us to the next cheap window.
    private double LoadKwhPerHour(int month) =>
        month is >= 4 and <= 9 ? _strat.SummerLoadKwhPerHour : _strat.WinterLoadKwhPerHour;

    // Average the quarter-hourly prices into an hour -> price map.
    private static Dictionary<int, double> HourlyPrices(IReadOnlyList<PricePoint>? points)
    {
        var map = new Dictionary<int, double>();
        if (points is null) return map;
        foreach (var g in points.GroupBy(p => p.StartsAt.Hour))
            map[g.Key] = g.Average(p => p.Total);
        return map;
    }

    // One forward step in the upcoming price/PV timeline used by the arbitrage
    // planner. Today == false means it belongs to tomorrow (used only to locate
    // the next cheap window and size the energy need; no action assigned there).
    private readonly record struct Slot(bool Today, int Hour, double Price, double NetLoad);

    private (List<PlanHour> Plan, double GridChargeKwh, string Note) PlanArbitrage(
        IReadOnlyList<HourlyPv> pv, Dictionary<int, double> todayPrices,
        Dictionary<int, double> tomorrowPrices, double available,
        double loadPerHour, int currentHour, double forecastKwh)
    {
        double Pv(int hr) => pv.FirstOrDefault(h => h.Hour == hr)?.EstKw ?? 0;

        // Build the forward timeline: rest of today, then tomorrow if published.
        var line = new List<Slot>();
        for (int hr = currentHour; hr < 24; hr++)
            if (todayPrices.TryGetValue(hr, out var p))
                line.Add(new Slot(true, hr, p, Math.Max(0, loadPerHour - Pv(hr))));
        for (int hr = 0; hr < 24; hr++)
            if (tomorrowPrices.TryGetValue(hr, out var p))
                line.Add(new Slot(false, hr, p, loadPerHour));   // PV unknown overnight

        double minP = line.Min(s => s.Price), maxP = line.Max(s => s.Price);
        double gap = _strat.MinArbitrageGapPct;

        // Prices too flat to beat the round-trip loss: just ride on battery.
        if (minP <= 0 || (maxP - minP) / minP * 100 < gap)
            return (FlatPlan(pv, todayPrices, currentHour, "discharge"),
                0, $"Low sun ({forecastKwh:F0} kWh); prices flat (<{gap:F0}% spread) — running on battery");

        // We recharge at the cheapest upcoming hour; the battery must carry the
        // net load until then. Anything short of that we pre-charge on cheap hours.
        int cheapestIdx = line.FindIndex(s => s.Price == minP);
        var horizon = line.Take(Math.Max(cheapestIdx, 1)).ToList();
        double needToCover = horizon.Sum(s => s.NetLoad);
        double deficit = Math.Max(0, needToCover - available);

        double loss = 1 + gap / 100.0;
        double headroom = Math.Max(0, _bat.UsableKwh - available);
        double chargeTarget = Math.Min(deficit * loss, headroom * loss);

        // Grid-charge the cheapest hours (up to MaxGridChargeKw each) until the
        // target is met, but only hours cheap enough to offset a pricier one.
        var chargeToday = new Dictionary<int, double>();
        double charged = 0;
        foreach (var s in horizon.OrderBy(s => s.Price))
        {
            if (charged >= chargeTarget) break;
            if (s.Price * loss >= maxP) continue;              // no pricier hour to beat
            double add = Math.Min(_strat.MaxGridChargeKw, chargeTarget - charged);
            charged += add;
            if (s.Today) chargeToday[s.Hour] = add;
        }

        // Expensive hours we still hold for (ride on battery instead of importing).
        var dischargeToday = horizon
            .Where(s => s.Today && !chargeToday.ContainsKey(s.Hour) && s.Price >= minP * loss)
            .Select(s => s.Hour).ToHashSet();

        var plan = new List<PlanHour>();
        for (int hr = 0; hr < 24; hr++)
        {
            double kw = Pv(hr);
            double? price = todayPrices.TryGetValue(hr, out var p) ? p : null;
            string action =
                hr < currentHour ? "idle" :
                chargeToday.ContainsKey(hr) ? "gridcharge" :
                dischargeToday.Contains(hr) ? "discharge" :
                kw >= GeneralBelowKw ? "charge" : "idle";
            plan.Add(new PlanHour(hr, action, kw, price));
        }

        double gridKwh = chargeToday.Values.Sum();
        var cheapest = line[cheapestIdx];
        string when = $"{cheapest.Hour:00}:00 {(cheapest.Today ? "today" : "tomorrow")}";
        string note = gridKwh > 0
            ? $"Low sun ({forecastKwh:F0} kWh). Grid-charge {gridKwh:F1} kWh over {chargeToday.Count} cheap h "
              + $"to cover ~{needToCover:F0} kWh until the cheap window at {when}"
            : $"Low sun ({forecastKwh:F0} kWh); battery covers ~{needToCover:F0} kWh to {when} — no grid charge needed";
        return (plan, gridKwh, note);
    }

    // Fallback plan for the whole day: past hours idle, future hours all the
    // same action (used when arbitrage isn't worthwhile).
    private static List<PlanHour> FlatPlan(IReadOnlyList<HourlyPv> pv,
        Dictionary<int, double> prices, int currentHour, string futureAction)
    {
        var plan = new List<PlanHour>();
        for (int hr = 0; hr < 24; hr++)
        {
            double kw = pv.FirstOrDefault(h => h.Hour == hr)?.EstKw ?? 0;
            double? price = prices.TryGetValue(hr, out var p) ? p : null;
            plan.Add(new PlanHour(hr, hr < currentHour ? "idle" : futureAction, kw, price));
        }
        return plan;
    }

    private static List<PlanHour> PlanDay(IReadOnlyList<HourlyPv> hours,
        Dictionary<int, double> prices, double deficitKwh, int fromHour)
    {
        // Sunny hours from fromHour on are candidates for charge or feed-in,
        // ranked by price so the priciest are considered for export first.
        var sunny = hours
            .Where(h => h.Hour >= fromHour && h.EstKw >= GeneralBelowKw)
            .OrderByDescending(h => prices.GetValueOrDefault(h.Hour, double.MinValue))
            .ToList();

        double sunnyTotal = sunny.Sum(h => h.EstKw);
        var feedIn = new HashSet<int>();
        double exported = 0;
        foreach (var h in sunny)
        {
            // Export this hour only if the sun left in the charging hours still
            // covers the battery deficit.
            if (sunnyTotal - exported - h.EstKw >= deficitKwh)
            {
                feedIn.Add(h.Hour);
                exported += h.EstKw;
            }
        }

        var plan = new List<PlanHour>();
        for (int hr = 0; hr < 24; hr++)
        {
            double kw = hours.FirstOrDefault(h => h.Hour == hr)?.EstKw ?? 0;
            double? price = prices.TryGetValue(hr, out var p) ? p : null;
            string action =
                hr < fromHour || kw < GeneralBelowKw ? "idle" :
                feedIn.Contains(hr) ? "feedin" : "charge";
            plan.Add(new PlanHour(hr, action, kw, price));
        }
        return plan;
    }
}
