using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FreeMon;

public sealed class CycleStats
{
    public DateTime TimeUtc { get; init; }
    public int Sent { get; init; }
    public int Recv { get; init; }
    public double LossPct { get; init; }
    public double? Min { get; init; }
    public double? Avg { get; init; }
    public double? Max { get; init; }
    public double? Jitter { get; init; }
    public DevStatus Raw { get; init; }
    public string? Error { get; init; }
    /// <summary>Human-readable result of a non-ICMP main check (not stored in the log).</summary>
    public string? Note { get; init; }
}

public sealed record StatusChange(Device Device, DevStatus From, DevStatus To, string Detail, bool Initial, bool ParentDown, string? Check = null);

public sealed record DeviceSnapshot(DevStatus Status, DateTime SinceUtc, CycleStats? Last);

public sealed class DeviceRuntime
{
    volatile DeviceSnapshot _snap;

    public Device Device { get; internal set; }
    public DeviceSnapshot Snap { get => _snap; internal set => _snap = value; }

    internal DevStatus Pending;
    internal int PendingCount;
    internal ushort Seq;

    public DeviceRuntime(Device d)
    {
        Device = d;
        _snap = new DeviceSnapshot(d.Enabled ? DevStatus.Unknown : DevStatus.Disabled, DateTime.UtcNow, null);
        Seq = (ushort)Random.Shared.Next(0, 65536);
    }
}

/// <summary>One async worker per enabled device, Netwatch-like cycle: N packets -> loss/RTT/jitter -> status.</summary>
public sealed class FreeMonitor
{
    readonly LogStore _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentQueue<string> _recent = new();
    volatile AppConfig _cfg = new();
    volatile Dictionary<string, DeviceRuntime> _rts = new();
    volatile Dictionary<string, List<CheckRuntime>> _checks = new();
    CancellationTokenSource? _cts;
    List<Task> _tasks = new();
    bool _stopped;
    int _recentVersion;

    public FreeMonitor(LogStore log) => _log = log;

    /// <summary>Raised on a worker thread.</summary>
    public event Action<StatusChange>? StatusChanged;

    public int RecentVersion => Volatile.Read(ref _recentVersion);

    public string[] Recent() => _recent.Reverse().ToArray();

    public DeviceRuntime? Get(string id) => _rts.TryGetValue(id, out var r) ? r : null;

    static readonly IReadOnlyList<CheckRuntime> NoChecks = Array.Empty<CheckRuntime>();

    public IReadOnlyList<CheckRuntime> ChecksOf(string deviceId) =>
        _checks.TryGetValue(deviceId, out var l) ? l : NoChecks;

    public DevStatus Effective(DeviceRuntime rt)
    {
        var s = rt.Snap.Status;
        if (s == DevStatus.Down && _cfg.SuppressChildrenWhenParentDown && AncestorDown(rt.Device))
            return DevStatus.Unreachable;
        return s;
    }

    bool AncestorDown(Device d)
    {
        var rts = _rts;
        var pid = d.ParentId;
        for (int i = 0; i < 32 && pid is not null; i++)
        {
            if (!rts.TryGetValue(pid, out var p)) return false;
            if (p.Snap.Status == DevStatus.Down) return true;
            pid = p.Device.ParentId;
        }
        return false;
    }

    public void SystemEvent(string text)
    {
        _log.AddEvent("", "FreeMon", "", DevStatus.Unknown, DevStatus.Unknown, text);
        PushRecent($"{DateTime.Now:dd.MM HH:mm:ss}  {text}");
    }

    void PushRecent(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > 300 && _recent.TryDequeue(out _)) { }
        Interlocked.Increment(ref _recentVersion);
    }

    /// <summary>Config change that does not affect pinging (e.g. map layout): swap references, keep workers running.</summary>
    public void UpdateConfig(AppConfig cfg)
    {
        _cfg = cfg;
        var byId = cfg.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var rt in _rts.Values)
            if (byId.TryGetValue(rt.Device.Id, out var d)) rt.Device = d;
        foreach (var list in _checks.Values)
            foreach (var crt in list)
                if (byId.TryGetValue(crt.Device.Id, out var d)) crt.Device = d;
    }

    public async Task ReloadAsync(AppConfig cfg)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopWorkersAsync().ConfigureAwait(false);
            _cfg = cfg;
            var old = _rts;
            var map = new Dictionary<string, DeviceRuntime>();
            foreach (var d in cfg.Devices)
            {
                if (map.ContainsKey(d.Id)) continue;
                if (old.TryGetValue(d.Id, out var rt) && rt.Device.Address == d.Address && d.Enabled && rt.Device.Enabled)
                {
                    rt.Device = d;
                    map[d.Id] = rt;
                }
                else
                {
                    map[d.Id] = new DeviceRuntime(d);
                }
            }
            _rts = map;

            var oldChecks = _checks.Values.SelectMany(l => l).ToDictionary(c => c.Key);
            var checks = new Dictionary<string, List<CheckRuntime>>();
            foreach (var d in cfg.Devices.Where(x => x.Enabled))
            {
                var list = new List<CheckRuntime>();
                foreach (var c in d.Checks.Where(x => x.Enabled))
                {
                    var key = d.Id + "#" + c.Id;
                    if (oldChecks.TryGetValue(key, out var o) &&
                        o.Device.Address == d.Address &&
                        ConfigStore.ToJson(o.Check) == ConfigStore.ToJson(c))
                    {
                        o.Device = d;
                        o.Check = c;
                        list.Add(o);
                    }
                    else
                    {
                        list.Add(new CheckRuntime(d, c));
                    }
                }
                if (list.Count > 0) checks[d.Id] = list;
            }
            _checks = checks;
            if (_stopped) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _tasks = map.Values.Where(r => r.Device.Enabled)
                .Select(r => Task.Run(() => RunAsync(r, token)))
                .Concat(checks.Values.SelectMany(l => l).Select(c => Task.Run(() => RunCheckAsync(c, token))))
                .ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Monitor reload", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            await StopWorkersAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task StopWorkersAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
        catch { /* workers handle their own errors */ }
        _cts.Dispose();
        _cts = null;
        _tasks = new List<Task>();
    }

    async Task RunAsync(DeviceRuntime rt, CancellationToken ct)
    {
        IIcmpProbe? probe = null;
        HttpClient? http = null;
        try
        {
            probe = ProbeFactory.Create();
            await Task.Delay(Random.Shared.Next(0, 1500), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var p = ProfileResolver.Effective(_cfg, rt.Device);
                var pc = rt.Device.Primary;
                int intervalSec = pc?.IntervalSec ?? p.IntervalSec;
                int confirm = pc?.ConfirmCycles ?? p.ConfirmCycles;
                var started = DateTime.UtcNow;
                CycleStats st;
                try
                {
                    if (pc is null)
                    {
                        st = await RunCycleAsync(rt, probe, p, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (pc.Kind is CheckKind.Http or CheckKind.Json) http ??= CheckProbes.CreateHttpClient(pc);
                        st = await RunPrimaryAsync(rt.Device, pc, http, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    st = Fail(started, ex.GetType().Name + ": " + ex.Message);
                }

                Apply(rt, st, confirm);
                _log.AddCycle(rt.Device.Id, st);

                var wait = TimeSpan.FromSeconds(intervalSec) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashLog.Write("Worker " + rt.Device.Name, ex);
        }
        finally
        {
            probe?.Dispose();
            http?.Dispose();
        }
    }

    async Task RunCheckAsync(CheckRuntime crt, CancellationToken ct)
    {
        HttpClient? http = null;
        try
        {
            if (crt.Check.Kind is CheckKind.Http or CheckKind.Json) http = CheckProbes.CreateHttpClient(crt.Check);
            await Task.Delay(Random.Shared.Next(500, 3000), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var r = await CheckProbes.RunAsync(crt.Device, crt.Check, http, ct).ConfigureAwait(false);
                ApplyCheck(crt, r);
                _log.AddCheck(crt.Device.Id, crt.Check.Id, r);
                var wait = TimeSpan.FromSeconds(crt.Check.IntervalSec) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CrashLog.Write("Check " + crt.Key, ex);
        }
        finally
        {
            http?.Dispose();
        }
    }

    void ApplyCheck(CheckRuntime crt, CheckResult r)
    {
        var snap = crt.Snap;
        var cur = snap.Status;
        var raw = r.Raw;
        var now = DateTime.UtcNow;
        if (cur == DevStatus.Unknown)
        {
            crt.PendingCount = 0;
            crt.Snap = new CheckSnapshot(raw, now, r);
            RaiseCheckEvent(crt, cur, raw, r);
            return;
        }
        if (raw == cur)
        {
            crt.PendingCount = 0;
            crt.Snap = snap with { Last = r };
            return;
        }
        if (raw == crt.Pending) crt.PendingCount++;
        else
        {
            crt.Pending = raw;
            crt.PendingCount = 1;
        }
        if (crt.PendingCount >= crt.Check.ConfirmCycles)
        {
            crt.PendingCount = 0;
            crt.Snap = new CheckSnapshot(raw, now, r);
            RaiseCheckEvent(crt, cur, raw, r);
        }
        else
        {
            crt.Snap = snap with { Last = r };
        }
    }

    void RaiseCheckEvent(CheckRuntime crt, DevStatus from, DevStatus to, CheckResult r)
    {
        var d = crt.Device;
        var title = crt.Check.Title();
        var text = $"{title}: {StatusText.Ua(from)} → {StatusText.Ua(to)}; {r.Detail}";
        // separate "dev" key keeps ping availability statistics clean
        _log.AddEvent(crt.Key, d.Name, d.Address, from, to, text);
        PushRecent($"{DateTime.Now:dd.MM HH:mm:ss}  {d.Name} ({d.Address})  {text}");
        try
        {
            StatusChanged?.Invoke(new StatusChange(d, from, to, r.Detail, from == DevStatus.Unknown, false, title));
        }
        catch (Exception ex)
        {
            CrashLog.Write("StatusChanged handler", ex);
        }
    }

    static CycleStats Fail(DateTime startedUtc, string error) => new()
    {
        TimeUtc = startedUtc,
        Sent = 0,
        Recv = 0,
        LossPct = 100,
        Raw = DevStatus.Down,
        Error = error,
    };

    static async Task<CycleStats> RunCycleAsync(DeviceRuntime rt, IIcmpProbe probe, EffProfile p, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var host = rt.Device.Address.Trim();
        IPAddress addr;
        if (IPAddress.TryParse(host, out var parsed))
        {
            if (parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return Fail(started, L.T("unsupported address type"));
            addr = parsed;
        }
        else
        {
            IPAddress[] list;
            try
            {
                list = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                return Fail(started, "DNS: " + ex.Message);
            }
            var pick = list.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? list.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (pick is null) return Fail(started, L.T("DNS: no IP address"));
            addr = pick;
        }

        var rtts = new List<double>(p.PacketCount);
        for (int i = 0; i < p.PacketCount; i++)
        {
            if (i > 0 && p.PacketIntervalMs > 0) await Task.Delay(p.PacketIntervalMs, ct).ConfigureAwait(false);
            var r = await probe.SendAsync(addr, rt.Seq++, p.PacketSize, p.TimeoutMs, ct).ConfigureAwait(false);
            if (r.Ok) rtts.Add(r.RttMs);
        }

        int sent = p.PacketCount;
        int recv = rtts.Count;
        double loss = 100.0 * (sent - recv) / sent;
        double? min = null, avg = null, max = null, jit = null;
        if (recv > 0)
        {
            min = rtts.Min();
            avg = rtts.Average();
            max = rtts.Max();
        }
        if (recv > 1)
        {
            double s = 0;
            for (int i = 1; i < recv; i++) s += Math.Abs(rtts[i] - rtts[i - 1]);
            jit = s / (recv - 1);
        }

        DevStatus raw;
        if (recv == 0 || loss >= p.DownLossPct)
            raw = DevStatus.Down;
        else if ((p.DegLossPct > 0 && loss >= p.DegLossPct) ||
                 (p.DegAvgMs > 0 && avg > p.DegAvgMs) ||
                 (p.DegMaxMs > 0 && max > p.DegMaxMs) ||
                 (p.DegJitterMs > 0 && jit > p.DegJitterMs))
            raw = DevStatus.Degraded;
        else
            raw = DevStatus.Up;

        return new CycleStats
        {
            TimeUtc = started,
            Sent = sent,
            Recv = recv,
            LossPct = loss,
            Min = min,
            Avg = avg,
            Max = max,
            Jitter = jit,
            Raw = raw,
        };
    }

    /// <summary>Main check other than ICMP: one probe = one "cycle" (1 sent, 1 or 0 received), so history and reports stay the same.</summary>
    static async Task<CycleStats> RunPrimaryAsync(Device d, ExtraCheck pc, HttpClient? http, CancellationToken ct)
    {
        var r = await CheckProbes.RunAsync(d, pc, http, ct).ConfigureAwait(false);
        return new CycleStats
        {
            TimeUtc = r.TimeUtc,
            Sent = 1,
            Recv = r.Ok ? 1 : 0,
            LossPct = r.Ok ? 0 : 100,
            Min = r.Ms,
            Avg = r.Ms,
            Max = r.Ms,
            Raw = r.Raw,
            Error = r.Ok ? null : r.Detail,
            Note = r.Ok ? r.Detail : null,
        };
    }

    void Apply(DeviceRuntime rt, CycleStats st, int confirmCycles)
    {
        var snap = rt.Snap;
        var cur = snap.Status;
        var raw = st.Raw;
        var now = DateTime.UtcNow;

        if (cur is DevStatus.Unknown or DevStatus.Disabled)
        {
            rt.PendingCount = 0;
            rt.Snap = new DeviceSnapshot(raw, now, st);
            RaiseEvent(rt, cur, raw, st);
            return;
        }
        if (raw == cur)
        {
            rt.PendingCount = 0;
            rt.Snap = snap with { Last = st };
            return;
        }
        if (raw == rt.Pending)
        {
            rt.PendingCount++;
        }
        else
        {
            rt.Pending = raw;
            rt.PendingCount = 1;
        }
        if (rt.PendingCount >= confirmCycles)
        {
            rt.PendingCount = 0;
            rt.Snap = new DeviceSnapshot(raw, now, st);
            RaiseEvent(rt, cur, raw, st);
        }
        else
        {
            rt.Snap = snap with { Last = st };
        }
    }

    void RaiseEvent(DeviceRuntime rt, DevStatus from, DevStatus to, CycleStats st)
    {
        var d = rt.Device;
        bool parentDown = to == DevStatus.Down && AncestorDown(d);
        var extra = parentDown && _cfg.SuppressChildrenWhenParentDown ? L.T(" (parent unreachable)") : "";
        var detail = st.Error ?? (L.F("loss {0:0}%", st.LossPct) + (st.Avg is double a ? L.F(", average latency {0:0.#} ms", a) : ""));
        var text = $"{StatusText.Ua(from)} → {StatusText.Ua(to)}{extra}; {detail}";
        _log.AddEvent(d.Id, d.Name, d.Address, from, to, text);
        PushRecent($"{DateTime.Now:dd.MM HH:mm:ss}  {d.Name} ({d.Address})  {text}");
        try
        {
            StatusChanged?.Invoke(new StatusChange(d, from, to, detail,
                from is DevStatus.Unknown or DevStatus.Disabled, parentDown));
        }
        catch (Exception ex)
        {
            CrashLog.Write("StatusChanged handler", ex);
        }
    }
}
