using System.Buffers.Binary;
using System.Net.Sockets;
using HomeEnergy.Configuration;
using HomeEnergy.Models;
using Microsoft.Extensions.Options;

namespace HomeEnergy.Services;

/// <summary>
/// Minimal Modbus-TCP client for the Solinteg inverter (no external deps).
/// Reads holding registers (FC 0x03); writes a single register (FC 0x06).
/// 32-bit values are big-endian word order (high word first).
/// </summary>
public class SolintegClient : ISolintegClient
{
    private readonly InverterOptions _opt;
    private readonly ILogger<SolintegClient> _log;
    private ushort _tid;

    // Working-mode register 50000. App "PV Charging" == 0x0400 (community "ToU").
    public const ushort ModeRegister = 50000;
    public const ushort ModePvCharging = 0x0400;
    public const ushort ModeGeneral = 0x0101;
    public const ushort ModeEconomic = 0x0102;  // time-of-use battery mode (grid-charge on cheap hours)
    public const ushort ModeFeedIn = 0x0105;
    public const ushort ModeBackup = 0x0103;   // reserves the battery (no discharge to loads)

    // Economic-mode charge/discharge schedule table (53006..53048). Empty = all
    // zeros. One charge period occupies the first 8 registers; the layout below
    // replicates exactly what the Solinteg app writes for a "charge from PV+GRID"
    // period (verified 2026-07-21 by writing one in the app and reading it back).
    public const ushort ChargeWindowRegister = 53006;

    // Max-charge (charge-cutoff) SOC holding register; scale 0.1% (raw 800 = 80.0%).
    // Verified 2026-07-12 by toggling the app setting and re-reading.
    public const ushort MaxChargeSocRegister = 52506;
    private const ushort SocRegister = 33000;   // battery SOC, scale 0.01%

    // Prior max-charge ceiling saved while a Backup-mode cap is in effect, so we
    // can restore the user's setting when leaving Backup. Null = no cap active.
    // Mirrored to a file: the plan holds the battery in Backup for hours at a time,
    // so a restart mid-hold must not forget the ceiling and leave it pinned.
    private ushort? _savedMaxChargeSoc;
    private readonly string _savedCeilingPath;

    // The inverter's Modbus TCP server accepts only one client at a time, so all
    // access is serialized through this gate. Overlapping connections (poll vs.
    // forecast vs. a UI write) otherwise make the device drop one mid-read.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly Dictionary<int, string> ModeLabels = new()
    {
        [0x0101] = "General",
        [0x0102] = "Economic",
        [0x0103] = "Backup/UPS",
        [0x0104] = "PeakShift",
        [0x0105] = "Feed-In",
        [0x0200] = "Off-Grid",
        [0x0400] = "PV-Charging",
    };

    public SolintegClient(IOptions<AppOptions> opt, IHostEnvironment env, ILogger<SolintegClient> log)
    {
        _opt = opt.Value.Inverter;
        _log = log;
        _savedCeilingPath = DataPaths.Resolve(opt.Value, env.ContentRootPath, "maxcharge_saved.txt");
        _savedMaxChargeSoc = LoadSavedCeiling();
    }

    private ushort? LoadSavedCeiling()
    {
        try
        {
            if (File.Exists(_savedCeilingPath) && ushort.TryParse(File.ReadAllText(_savedCeilingPath), out var v))
            {
                _log.LogInformation("Found saved max-charge SOC {Ceiling:F1}% from before restart; will restore on leaving Backup", v / 10.0);
                return v;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "could not read saved max-charge SOC"); }
        return null;
    }

    private void SaveCeiling(ushort? value)
    {
        _savedMaxChargeSoc = value;
        try
        {
            if (value is { } v) File.WriteAllText(_savedCeilingPath, v.ToString());
            else File.Delete(_savedCeilingPath);
        }
        catch (Exception ex) { _log.LogWarning(ex, "could not persist saved max-charge SOC"); }
    }

    public async Task<InverterStats?> ReadStatsAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await WithConnection(async stream =>
                {
                    double pv = await ReadU32(stream, 11028, ct);
                    double pv1 = await ReadU32(stream, 11062, ct);
                    double pv2 = await ReadU32(stream, 11064, ct);
                    double ac = await ReadS32(stream, 11016, ct);
                    // Register 11000 reports + when exporting; negate so the field
                    // follows the app convention (+ import, - export / feed-in).
                    double grid = -await ReadS32(stream, 11000, ct);
                    // Register 30258 reports + when discharging; negate so the field
                    // follows the app convention (+ charging, - discharging).
                    double batt = -await ReadS32(stream, 30258, ct);
                    double soc = await ReadU16(stream, 33000, ct) * 0.01;
                    double gen = await ReadU16(stream, 31005, ct) * 0.1;
                    int mode = await ReadU16(stream, ModeRegister, ct);
                    double load = pv + grid - batt;   // house consumption (energy balance)

                    return new InverterStats(pv, pv1, pv2, ac, grid, batt, load, soc, gen,
                        $"0x{mode:X4}",
                        ModeLabels.GetValueOrDefault(mode, $"Unknown(0x{mode:X4})"),
                        DateTimeOffset.Now);
                }, ct);
            }
            // The device occasionally drops a connection mid-read; one quick
            // retry clears it without surfacing a warning.
            catch (Exception ex) when (attempt < 2 && ex is IOException or SocketException)
            {
                _log.LogDebug(ex, "Solinteg read attempt {Attempt} failed, retrying", attempt);
                await Task.Delay(250, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Solinteg read failed");
                return null;
            }
        }
    }

    public Task<ushort[]> ReadHoldingRawAsync(ushort start, ushort count, CancellationToken ct) =>
        WithConnection(async stream =>
        {
            var bytes = await ReadHolding(stream, start, count, ct);
            var regs = new ushort[count];
            for (int i = 0; i < count; i++)
                regs[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2));
            return regs;
        }, ct);

    public Task<double> ReadGenTodayKwhAsync(CancellationToken ct) =>
        WithConnection(async stream => await ReadU16(stream, 31005, ct) * 0.1, ct);

    public Task WriteModeAsync(ushort value, CancellationToken ct) =>
        WithConnection(async stream =>
        {
            // Entering Backup pins the charge ceiling at the current SOC so the house
            // battery holds instead of topping up; leaving Backup restores the setting.
            if (value == ModeBackup)
                await CapMaxChargeToCurrentSocAsync(stream, ct);
            else
                await RestoreMaxChargeAsync(stream, ct);

            await WriteSingle(stream, ModeRegister, value, ct);
            _log.LogInformation("Wrote mode 0x{Value:X4} to register {Reg}", value, ModeRegister);
        }, ct);

    // Program a single grid-charge window into the Economic-mode schedule. Byte
    // layout mirrors the app's own record: three structural flags = 1 (charge +
    // include grid), two 0xFFFF power placeholders, target SOC at 0.1% scale, and
    // start/stop times packed as 0xHHMM. Economic mode (0x0102) must be active for
    // the inverter to act on it.
    public Task WriteChargeWindowAsync(TimeOnly start, TimeOnly end, double socPercent, CancellationToken ct) =>
        WithConnection(async s =>
        {
            ushort soc = (ushort)Math.Clamp((int)Math.Round(socPercent * 10), 0, 1000);
            ushort[] record = { 1, 1, 1, 0xFFFF, soc, 0xFFFF, Hhmm(start), Hhmm(end) };
            await WriteMultiple(s, ChargeWindowRegister, record, ct);

            _log.LogInformation("Wrote grid-charge window {Start:HH\\:mm}-{End:HH\\:mm} target {Soc:F0}%",
                start, end, socPercent);
        }, ct);

    // Clear the first charge period back to all-zeros (the pristine empty state).
    public Task ClearChargeWindowAsync(CancellationToken ct) =>
        WithConnection(async s =>
        {
            await WriteMultiple(s, ChargeWindowRegister, new ushort[8], ct);
            _log.LogInformation("Cleared grid-charge window (reg {Reg}..{End})",
                ChargeWindowRegister, ChargeWindowRegister + 7);
        }, ct);

    private static ushort Hhmm(TimeOnly t) => (ushort)((t.Hour << 8) | t.Minute);

    private async Task CapMaxChargeToCurrentSocAsync(NetworkStream s, CancellationToken ct)
    {
        if (_savedMaxChargeSoc is not null) return;   // already capped by us

        int socRaw = await ReadU16(s, SocRegister, ct);              // 0.01% units
        var prior = (ushort)await ReadU16(s, MaxChargeSocRegister, ct); // 0.1% units
        // SOC (0.01%) -> ceiling units (0.1%) is /10; never raise above the user's ceiling.
        var cap = (ushort)Math.Clamp((int)Math.Round(socRaw / 10.0), 0, prior);
        if (cap >= prior) return;   // already at/below current SOC; nothing to pin

        SaveCeiling(prior);
        await WriteSingle(s, MaxChargeSocRegister, cap, ct);
        _log.LogInformation("Pinned max-charge SOC to {Cap:F1}% (was {Prior:F1}%) for Backup",
            cap / 10.0, prior / 10.0);
    }

    private async Task RestoreMaxChargeAsync(NetworkStream s, CancellationToken ct)
    {
        if (_savedMaxChargeSoc is not { } prior) return;   // no cap active
        await WriteSingle(s, MaxChargeSocRegister, prior, ct);
        SaveCeiling(null);
        _log.LogInformation("Restored max-charge SOC to {Prior:F1}%", prior / 10.0);
    }

    public static string ModeLabel(int value) =>
        ModeLabels.GetValueOrDefault(value, $"Unknown(0x{value:X4})");

    // ---- Connection handling ----
    // Serialize every conversation through a single connection: the device only
    // tolerates one client at a time. The gate ensures poll/forecast/UI callers
    // never open overlapping sockets.
    private async Task<T> WithConnection<T>(Func<NetworkStream, Task<T>> op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var sock = new TcpClient();
            await sock.ConnectAsync(_opt.Ip, _opt.Port, ct);
            sock.ReceiveTimeout = 6000;
            return await op(sock.GetStream());
        }
        finally { _gate.Release(); }
    }

    private Task WithConnection(Func<NetworkStream, Task> op, CancellationToken ct) =>
        WithConnection<object?>(async s => { await op(s); return null; }, ct);

    // ---- Modbus primitives ----
    private async Task<byte[]> ReadHolding(NetworkStream s, ushort addr, ushort count, CancellationToken ct)
    {
        var tid = ++_tid;
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), tid);   // transaction id
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);     // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);     // length
        frame[6] = _opt.Unit;
        frame[7] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), addr);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), count);
        await s.WriteAsync(frame, ct);

        var hdr = await ReadExact(s, 7, ct);
        int len = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
        var body = await ReadExact(s, len - 1, ct);
        if ((body[0] & 0x80) != 0)
            throw new IOException($"Modbus exception 0x{body[1]:X2} at reg {addr}");
        int byteCount = body[1];
        return body[2..(2 + byteCount)];
    }

    private async Task WriteSingle(NetworkStream s, ushort addr, ushort value, CancellationToken ct)
    {
        var tid = ++_tid;
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        frame[6] = _opt.Unit;
        frame[7] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), addr);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), value);
        await s.WriteAsync(frame, ct);

        var hdr = await ReadExact(s, 7, ct);
        int len = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
        var body = await ReadExact(s, len - 1, ct);
        if ((body[0] & 0x80) != 0)
            throw new IOException($"Modbus write exception 0x{body[1]:X2} at reg {addr}");
    }

    // FC 0x10: write a contiguous block of holding registers in one frame. The
    // Economic-mode schedule rejects single-register writes (FC 0x06) to interior
    // addresses, so the whole record must go as one multi-write.
    private async Task WriteMultiple(NetworkStream s, ushort addr, ushort[] values, CancellationToken ct)
    {
        var tid = ++_tid;
        int qty = values.Length;
        int byteCount = qty * 2;
        var frame = new byte[13 + byteCount];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(7 + byteCount));
        frame[6] = _opt.Unit;
        frame[7] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), addr);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), (ushort)qty);
        frame[12] = (byte)byteCount;
        for (int i = 0; i < qty; i++)
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13 + i * 2), values[i]);
        await s.WriteAsync(frame, ct);

        var hdr = await ReadExact(s, 7, ct);
        int len = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(4));
        var body = await ReadExact(s, len - 1, ct);
        if ((body[0] & 0x80) != 0)
            throw new IOException($"Modbus write-multi exception 0x{body[1]:X2} at reg {addr}");
    }

    private async Task<int> ReadU16(NetworkStream s, ushort addr, CancellationToken ct)
    {
        var d = await ReadHolding(s, addr, 1, ct);
        return BinaryPrimitives.ReadUInt16BigEndian(d);
    }

    private async Task<long> ReadU32(NetworkStream s, ushort addr, CancellationToken ct)
    {
        var d = await ReadHolding(s, addr, 2, ct);
        return ((long)BinaryPrimitives.ReadUInt16BigEndian(d) << 16)
               | BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2));
    }

    private async Task<int> ReadS32(NetworkStream s, ushort addr, CancellationToken ct)
    {
        long v = await ReadU32(s, addr, ct);
        return (int)v;   // wraps to signed 32-bit
    }

    private static async Task<byte[]> ReadExact(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(off, n - off), ct);
            if (r == 0) throw new IOException("socket closed");
            off += r;
        }
        return buf;
    }
}
