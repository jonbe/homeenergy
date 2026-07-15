using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// SQLite-backed history: raw minute samples in <c>stats_minute</c>, rolled up
/// into <c>stats_hour</c> (dominant mode + averaged values) on every write.
/// Minute rows are pruned after a few days; the hourly table is kept.
/// </summary>
public class SqliteHistoryStore : IHistoryStore
{
    private const int MinuteRetentionDays = 3;

    private readonly string _connString;
    private readonly ILogger<SqliteHistoryStore> _log;

    public SqliteHistoryStore(IOptions<AppOptions> opt, IHostEnvironment env, ILogger<SqliteHistoryStore> log)
    {
        var path = DataPaths.Resolve(opt.Value, env.ContentRootPath, "history.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _log = log;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS stats_minute (
              ts TEXT PRIMARY KEY, mode TEXT,
              solar_w REAL, battery_pct REAL, grid_w REAL, load_w REAL, price REAL);
            CREATE TABLE IF NOT EXISTS stats_hour (
              ts TEXT PRIMARY KEY, mode TEXT,
              solar_w REAL, battery_pct REAL, grid_w REAL, load_w REAL, price REAL,
              samples INTEGER);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task LogMinuteAsync(DateTimeOffset when, string mode, double solarW,
        double batteryPct, double gridW, double loadW, double? price, CancellationToken ct)
    {
        string minute = when.ToString("yyyy-MM-ddTHH:mm:00");
        string hour = when.ToString("yyyy-MM-ddTHH:00:00");
        string hourLike = when.ToString("yyyy-MM-ddTHH:") + "%";
        string cutoff = when.AddDays(-MinuteRetentionDays).ToString("yyyy-MM-ddTHH:mm:00");

        using var conn = Open();

        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO stats_minute (ts, mode, solar_w, battery_pct, grid_w, load_w, price)
                VALUES (:ts, :mode, :solar, :batt, :grid, :load, :price)
                ON CONFLICT(ts) DO UPDATE SET
                  mode=excluded.mode, solar_w=excluded.solar_w, battery_pct=excluded.battery_pct,
                  grid_w=excluded.grid_w, load_w=excluded.load_w, price=excluded.price;
                """;
            ins.Parameters.AddWithValue(":ts", minute);
            ins.Parameters.AddWithValue(":mode", mode);
            ins.Parameters.AddWithValue(":solar", solarW);
            ins.Parameters.AddWithValue(":batt", batteryPct);
            ins.Parameters.AddWithValue(":grid", gridW);
            ins.Parameters.AddWithValue(":load", loadW);
            ins.Parameters.AddWithValue(":price", (object?)price ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }

        using (var agg = conn.CreateCommand())
        {
            agg.CommandText = """
                INSERT INTO stats_hour (ts, mode, solar_w, battery_pct, grid_w, load_w, price, samples)
                SELECT :hour,
                  (SELECT mode FROM stats_minute WHERE ts LIKE :like
                   GROUP BY mode ORDER BY COUNT(*) DESC LIMIT 1),
                  AVG(solar_w), AVG(battery_pct), AVG(grid_w), AVG(load_w), AVG(price), COUNT(*)
                FROM stats_minute WHERE ts LIKE :like
                ON CONFLICT(ts) DO UPDATE SET
                  mode=excluded.mode, solar_w=excluded.solar_w, battery_pct=excluded.battery_pct,
                  grid_w=excluded.grid_w, load_w=excluded.load_w, price=excluded.price,
                  samples=excluded.samples;
                """;
            agg.Parameters.AddWithValue(":hour", hour);
            agg.Parameters.AddWithValue(":like", hourLike);
            await agg.ExecuteNonQueryAsync(ct);
        }

        using (var prune = conn.CreateCommand())
        {
            prune.CommandText = "DELETE FROM stats_minute WHERE ts < :cutoff;";
            prune.Parameters.AddWithValue(":cutoff", cutoff);
            await prune.ExecuteNonQueryAsync(ct);
        }
    }

    public IReadOnlyList<HistoryHour> TodayHourly() =>
        HourlyForDate(DateOnly.FromDateTime(DateTimeOffset.Now.DateTime));

    public IReadOnlyList<HistoryHour> HourlyForDate(DateOnly date)
    {
        var byHour = new Dictionary<int, HistoryHour>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, mode, solar_w, battery_pct, grid_w, load_w, price
                FROM stats_hour WHERE ts LIKE :like;
                """;
            cmd.Parameters.AddWithValue(":like", date.ToString("yyyy-MM-dd") + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int hour = int.Parse(r.GetString(0).Substring(11, 2));
                byHour[hour] = new HistoryHour(hour,
                    r.IsDBNull(1) ? null : r.GetString(1),
                    Num(r, 2), Num(r, 3), Num(r, 4), Num(r, 5), Num(r, 6));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "history read failed"); }

        return Enumerable.Range(0, 24)
            .Select(h => byHour.GetValueOrDefault(h, new HistoryHour(h, null, null, null, null, null, null)))
            .ToList();

        static double? Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public ExportTotals TodayExport()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // grid_w is negative while exporting; over one ~1-minute sample the
            // energy is W/60000 kWh. Value skips minutes with no logged price.
            cmd.CommandText = """
                SELECT COALESCE(SUM(-grid_w) / 60000.0, 0),
                       COALESCE(SUM(-grid_w / 60000.0 * price), 0)
                FROM stats_minute
                WHERE ts LIKE :like AND grid_w < 0;
                """;
            cmd.Parameters.AddWithValue(":like", DateTimeOffset.Now.ToString("yyyy-MM-dd") + "%");
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new ExportTotals(r.GetDouble(0), r.GetDouble(1));
        }
        catch (Exception ex) { _log.LogWarning(ex, "export total read failed"); }
        return new ExportTotals(0, 0);
    }
}
