using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FreeMon;

public readonly record struct ProbeReply(bool Ok, double RttMs);

public interface IIcmpProbe : IDisposable
{
    Task<ProbeReply> SendAsync(IPAddress addr, ushort seq, int size, int timeoutMs, CancellationToken ct);
}

public static class ProbeFactory
{
    static bool _dgram;

    public static string Mode { get; private set; } = "?";

    public static void Init()
    {
        _dgram = false;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp);
                _dgram = true;
            }
            catch (SocketException) { }
            catch (PlatformNotSupportedException) { }
        }
        Mode = _dgram ? L.T("ICMP socket without root privileges") : L.T("system Ping (.NET)");
    }

    public static IIcmpProbe Create()
    {
        if (_dgram)
        {
            try { return new DgramIcmpProbe(); }
            catch (SocketException) { }
        }
        return new SystemPingProbe();
    }
}

/// <summary>
/// Linux/macOS unprivileged ICMP (SOCK_DGRAM + IPPROTO_ICMP / IPPROTO_ICMPV6). The kernel sets the identifier
/// (and the ICMPv6 checksum). If an ICMPv6 datagram socket is not allowed, falls back to System.Net Ping for IPv6.
/// </summary>
sealed class DgramIcmpProbe : IIcmpProbe
{
    static readonly IPEndPoint Any4 = new(IPAddress.Any, 0);
    static readonly IPEndPoint Any6 = new(IPAddress.IPv6Any, 0);
    readonly Socket _s4 = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp);
    Socket? _s6;
    bool _v6Unavailable;
    SystemPingProbe? _fallback;
    readonly byte[] _rx = new byte[65536];

    Socket? SocketFor(AddressFamily af)
    {
        if (af == AddressFamily.InterNetwork) return _s4;
        if (_v6Unavailable) return null;
        try
        {
            return _s6 ??= new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.IcmpV6);
        }
        catch (SocketException)
        {
            _v6Unavailable = true;
            return null;
        }
    }

    public async Task<ProbeReply> SendAsync(IPAddress addr, ushort seq, int size, int timeoutMs, CancellationToken ct)
    {
        var sock = SocketFor(addr.AddressFamily);
        if (sock is null)
        {
            _fallback ??= new SystemPingProbe();
            return await _fallback.SendAsync(addr, seq, size, timeoutMs, ct).ConfigureAwait(false);
        }
        bool v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
        byte requestType = v6 ? (byte)128 : (byte)8;
        byte replyType = v6 ? (byte)129 : (byte)0;

        var pkt = new byte[8 + size];
        pkt[0] = requestType;
        pkt[1] = 0;
        pkt[6] = (byte)(seq >> 8);
        pkt[7] = (byte)seq;
        for (int i = 0; i < size; i++) pkt[8 + i] = (byte)('a' + i % 23);
        if (!v6)
        {
            var cs = Checksum(pkt);
            pkt[2] = (byte)(cs >> 8);
            pkt[3] = (byte)cs;
        }

        var target = addr.GetAddressBytes();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await sock.SendToAsync(pkt, SocketFlags.None, new IPEndPoint(addr, 0), cts.Token).ConfigureAwait(false);
            while (true)
            {
                SocketReceiveFromResult r;
                try
                {
                    r = await sock.ReceiveFromAsync(_rx, SocketFlags.None, v6 ? Any6 : Any4, cts.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // ICMP errors (unreachable etc.) surface as socket errors; keep waiting until timeout
                    continue;
                }
                if (r.ReceivedBytes >= 8 && _rx[0] == replyType &&
                    ((_rx[6] << 8) | _rx[7]) == seq &&
                    r.RemoteEndPoint is IPEndPoint ep && ep.Address.GetAddressBytes().AsSpan().SequenceEqual(target))
                {
                    return new ProbeReply(true, sw.Elapsed.TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeReply(false, 0);
        }
        catch (SocketException)
        {
            return new ProbeReply(false, 0);
        }
    }

    static ushort Checksum(byte[] d)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < d.Length; i += 2) sum += (uint)((d[i] << 8) | d[i + 1]);
        if (i < d.Length) sum += (uint)(d[i] << 8);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    public void Dispose()
    {
        _s4.Dispose();
        _s6?.Dispose();
        _fallback?.Dispose();
    }
}

/// <summary>Windows (IcmpSendEcho, no admin) and fallback for other OS.</summary>
sealed class SystemPingProbe : IIcmpProbe
{
    readonly Ping _ping = new();

    public async Task<ProbeReply> SendAsync(IPAddress addr, ushort seq, int size, int timeoutMs, CancellationToken ct)
    {
        var buf = new byte[size];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)('a' + i % 23);
        try
        {
            var r = await _ping.SendPingAsync(addr, TimeSpan.FromMilliseconds(timeoutMs), buf, null, ct).ConfigureAwait(false);
            return r.Status == IPStatus.Success ? new ProbeReply(true, r.RoundtripTime) : new ProbeReply(false, 0);
        }
        catch (PingException)
        {
            return new ProbeReply(false, 0);
        }
    }

    public void Dispose() => _ping.Dispose();
}
