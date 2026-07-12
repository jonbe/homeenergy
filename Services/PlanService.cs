using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Derives an hourly charge/feed-in suggestion from the price and solar
/// forecasts: fill the battery from the cheapest remaining sun, export the
/// surplus during the highest-price sunny hours, always reaching 100% before
/// dusk. Suggestion only — nothing here writes to the inverter.
/// </summary>
public class PlanService : IPlanService
{
    // Below this forecast PV output an hour isn't worth acting on: run General.
    private const double GeneralBelowKw = 2.0;

    private readonly BatteryOptions _bat;

    public PlanService(IOptions<AppOptions> opt) => _bat = opt.Value.Battery;

    public PlanSnapshot? Build(PriceSnapshot? prices, SolarSnapshot? solar, InverterStats? inverter)
    {
        if (solar is null) return null;
        var now = DateTimeOffset.Now;

        double cap = _bat.CapacityKwh;
        double soc = inverter?.BatterySoc ?? 0;
        double todayDeficit = Math.Max(0, (100 - soc) / 100.0 * cap);

        var today = PlanDay(solar.TodayHourly, HourlyPrices(prices?.Today), todayDeficit, now.Hour);
        // Tomorrow: assume the battery drains overnight, so it must charge a full
        // capacity from the coming day's sun.
        var tomorrow = PlanDay(solar.TomorrowHourly, HourlyPrices(prices?.Tomorrow), cap, fromHour: 0);

        return new PlanSnapshot(cap, todayDeficit, today, tomorrow, now);
    }

    // Average the quarter-hourly prices into an hour -> price map.
    private static Dictionary<int, double> HourlyPrices(IReadOnlyList<PricePoint>? points)
    {
        var map = new Dictionary<int, double>();
        if (points is null) return map;
        foreach (var g in points.GroupBy(p => p.StartsAt.Hour))
            map[g.Key] = g.Average(p => p.Total);
        return map;
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
