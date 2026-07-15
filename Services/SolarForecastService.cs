using System.Globalization;
using System.Text;
using System.Text.Json;
using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// PV production forecast: expected_kWh = kWp * GTI * PR.
/// GTI from open-meteo (cloud-aware). PR auto-calibrated from the inverter's
/// real generation on COMPLETE days (rolling history CSV). Today blends actual
/// production with a bias-corrected remainder; tomorrow uses the calibrated PR.
/// </summary>
public class SolarForecastService : ISolarForecastService
{
    private const double PrMin = 0.60, PrMax = 1.05;
    private const double DayCompleteFrac = 0.90;
    private const double BiasMin = 0.5, BiasMax = 1.5;
    private const int PrWindowDays = 10;

    private readonly SolarOptions _opt;
    private readonly ISolintegClient _inverter;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SolarForecastService> _log;
    private readonly string _historyPath;

    public SolarForecastService(IOptions<AppOptions> opt, ISolintegClient inverter,
        IHttpClientFactory http, IHostEnvironment env, ILogger<SolarForecastService> log)
    {
        _opt = opt.Value.Solar;
        _inverter = inverter;
        _http = http;
        _log = log;
        _historyPath = DataPaths.Resolve(opt.Value, env.ContentRootPath, "solar_pr_history.csv");
    }

    private string OpenMeteoUrl =>
        "https://api.open-meteo.com/v1/forecast" +
        $"?latitude={_opt.Latitude.ToString(CultureInfo.InvariantCulture)}" +
        $"&longitude={_opt.Longitude.ToString(CultureInfo.InvariantCulture)}" +
        "&hourly=global_tilted_irradiance,cloud_cover&daily=sunshine_duration" +
        $"&tilt={_opt.Tilt}&azimuth={_opt.AzimuthSouth}" +
        "&timezone=auto&past_days=1&forecast_days=2";

    public async Task<SolarSnapshot?> FetchAsync(CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "HomeEnergy/1.0");
            using var doc = JsonDocument.Parse(await client.GetStringAsync(OpenMeteoUrl, ct));
            var root = doc.RootElement;

            var (days, now) = ParseHourly(root);
            string today = now.ToString("yyyy-MM-dd");
            string tomorrow = now.AddDays(1).ToString("yyyy-MM-dd");

            var todayHours = days.GetValueOrDefault(today, new());
            double tFull = DayKwh(todayHours);
            double tElapsed = ElapsedKwh(todayHours, now);

            double? gen = null;
            try { gen = await _inverter.ReadGenTodayKwhAsync(ct); }
            catch (Exception ex) { _log.LogDebug(ex, "gen-today read failed"); }

            var history = LoadHistory();
            RecordToday(history, gen, tElapsed, tFull, today);
            var (pr, ndays) = CalibratedPr(history);

            double todayEst;
            double? actual = gen, biasPct = null;
            if (gen is double g && tElapsed > 0)
            {
                double expectedSoFar = _opt.Kwp * tElapsed * pr;
                double bias = expectedSoFar > 0.3
                    ? Math.Clamp(g / expectedSoFar, BiasMin, BiasMax) : 1.0;
                double remaining = Math.Max(0, tFull - tElapsed);
                todayEst = g + remaining * _opt.Kwp * pr * bias;
                biasPct = (bias - 1) * 100;
            }
            else
            {
                todayEst = _opt.Kwp * tFull * pr;
            }

            double tomorrowEst = _opt.Kwp * DayKwh(days.GetValueOrDefault(tomorrow, new())) * pr;

            // Full-day series (00–23, night hours included as 0) so the bars
            // share the same time axis as the price chart.
            var todayHourly = BuildHourly(todayHours, pr);
            var tomorrowHourly = BuildHourly(days.GetValueOrDefault(tomorrow, new()), pr);

            return new SolarSnapshot(pr, ndays, todayEst, actual, biasPct,
                tomorrowEst, todayHourly, tomorrowHourly, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Solar forecast failed");
            return null;
        }
    }

    private record HourSample(int Hour, double Gti, double? Cloud);

    private (Dictionary<string, List<HourSample>>, DateTimeOffset) ParseHourly(JsonElement root)
    {
        var h = root.GetProperty("hourly");
        var times = h.GetProperty("time");
        var gti = h.GetProperty("global_tilted_irradiance");
        var cloud = h.GetProperty("cloud_cover");
        var days = new Dictionary<string, List<HourSample>>();
        for (int i = 0; i < times.GetArrayLength(); i++)
        {
            string ts = times[i].GetString()!;
            string day = ts[..10];
            int hour = int.Parse(ts.Substring(11, 2));
            double g = gti[i].ValueKind == JsonValueKind.Number ? gti[i].GetDouble() : 0;
            double? c = cloud[i].ValueKind == JsonValueKind.Number ? cloud[i].GetDouble() : null;
            days.TryAdd(day, new());
            days[day].Add(new HourSample(hour, g, c));
        }
        int offset = root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromSeconds(offset));
        return (days, now);
    }

    private List<HourlyPv> BuildHourly(List<HourSample> hours, double pr) =>
        hours.OrderBy(h => h.Hour)
             .Select(h => new HourlyPv(h.Hour, _opt.Kwp * h.Gti / 1000.0 * pr, h.Cloud))
             .ToList();

    private static double DayKwh(List<HourSample> hours) => hours.Sum(h => h.Gti) / 1000.0;

    private static double ElapsedKwh(List<HourSample> hours, DateTimeOffset now)
    {
        double total = 0;
        foreach (var h in hours)
        {
            if (h.Hour < now.Hour) total += h.Gti;
            else if (h.Hour == now.Hour) total += h.Gti * (now.Minute / 60.0);
        }
        return total / 1000.0;
    }

    // ---- history CSV ----
    private record Row(string Date, double Gen, double Completed, double Full, double Pr);

    private Dictionary<string, Row> LoadHistory()
    {
        var rows = new Dictionary<string, Row>();
        if (!File.Exists(_historyPath)) return rows;
        foreach (var line in File.ReadLines(_historyPath).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 5) continue;
            rows[p[0]] = new Row(p[0], D(p[1]), D(p[2]), D(p[3]), D(p[4]));
        }
        return rows;
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }

    private void SaveHistory(Dictionary<string, Row> rows)
    {
        var sb = new StringBuilder("date,gen_kwh,gti_completed,gti_full,pr\n");
        foreach (var d in rows.Keys.OrderBy(k => k))
        {
            var r = rows[d];
            sb.Append(string.Join(',', d,
                r.Gen.ToString("F1", CultureInfo.InvariantCulture),
                r.Completed.ToString("F2", CultureInfo.InvariantCulture),
                r.Full.ToString("F2", CultureInfo.InvariantCulture),
                r.Pr.ToString("F3", CultureInfo.InvariantCulture))).Append('\n');
        }
        File.WriteAllText(_historyPath, sb.ToString());
    }

    private void RecordToday(Dictionary<string, Row> rows, double? gen,
        double elapsed, double full, string today)
    {
        if (gen is not double g || elapsed <= 0) return;
        double pr = Math.Clamp(g / (_opt.Kwp * elapsed), PrMin, PrMax);
        rows[today] = new Row(today, g, elapsed, full, pr);
        SaveHistory(rows);
    }

    private (double pr, int days) CalibratedPr(Dictionary<string, Row> rows)
    {
        var prs = rows.Values
            .Where(r => r.Full > 0 && r.Completed / r.Full >= DayCompleteFrac)
            .OrderBy(r => r.Date)
            .TakeLast(PrWindowDays)
            .Select(r => r.Pr)
            .ToList();
        return prs.Count > 0 ? (prs.Average(), prs.Count) : (_opt.DefaultPr, 0);
    }
}
