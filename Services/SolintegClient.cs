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
    public const ushort ModeFeedIn = 0x0105;

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

    public SolintegClient(IOptions<AppOptions> opt, ILogger<SolintegClient> log)
    {
        _opt = opt.Value.Inverter;
        _log = log;
    }

    public async Task<InverterStats?> ReadStatsAsync(CancellationToken ct)
    {
        try
        {
            using var sock = new TcpClient();
            await sock.ConnectAsync(_opt.Ip, _opt.Port, ct);
            sock.ReceiveTimeout = 6000;
            var stream = sock.GetStream();

            double pv = await ReadU32(stream, 11028, ct);
            double pv1 = await ReadU32(stream, 11062, ct);
            double pv2 = await ReadU32(stream, 11064, ct);
            double ac = await ReadS32(stream, 11016, ct);
            double grid = await ReadS32(stream, 11000, ct);
            double batt = await ReadS32(stream, 30258, ct);
            double soc = await ReadU16(stream, 33000, ct) * 0.01;
            double gen = await ReadU16(stream, 31005, ct) * 0.1;
            int mode = await ReadU16(stream, ModeRegister, ct);

            return new InverterStats(pv, pv1, pv2, ac, grid, batt, soc, gen,
                $"0x{mode:X4}",
                ModeLabels.GetValueOrDefault(mode, $"Unknown(0x{mode:X4})"),
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Solinteg read failed");
            return null;
        }
    }

    public async Task<double> ReadGenTodayKwhAsync(CancellationToken ct)
    {
        using var sock = new TcpClient();
        await sock.ConnectAsync(_opt.Ip, _opt.Port, ct);
        sock.ReceiveTimeout = 6000;
        return await ReadU16(sock.GetStream(), 31005, ct) * 0.1;
    }

    public async Task WriteModeAsync(ushort value, CancellationToken ct)
    {
        using var sock = new TcpClient();
        await sock.ConnectAsync(_opt.Ip, _opt.Port, ct);
        sock.ReceiveTimeout = 6000;
        await WriteSingle(sock.GetStream(), ModeRegister, value, ct);
        _log.LogInformation("Wrote mode 0x{Value:X4} to register {Reg}", value, ModeRegister);
    }

    public static string ModeLabel(int value) =>
        ModeLabels.GetValueOrDefault(value, $"Unknown(0x{value:X4})");

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
