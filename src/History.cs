using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FreeMon;

public sealed record CycleRow(long Ts, int Sent, int Recv, double Loss, double? Min, double? Avg, double? Max, double? Jitter, DevStatus Raw, string? Error);

public sealed record EventRow(long Ts, DevStatus From, DevStatus To, string Text);

public sealed record CheckRow(long Ts, string Check, bool Ok, double? Ms, DevStatus Status, string Detail);

public sealed record Incident(DevStatus Kind, DateTime StartUtc, DateTime EndUtc, double DurationSec,
    bool StartedBefore, bool Ongoing, bool ParentDown, bool HasGap);

public sealed record Bucket(DateTime StartUtc, double? AvgRtt, double? MaxRtt, double? LossPct, DevStatus Worst, bool NoData);

public sealed class HistoryResult
{
    public required Device Device { get; init; }
    public required DateTime FromUtc { get; init; }
    public required DateTime ToUtc { get; init; }
    public double UpSec { get; init; }
    public double DegradedSec { get; init; }
    public double DownSec { get; init; }
    public double NoDataSec { get; init; }
    /// <summary>Periods without monitoring (FreeMon not running or the device's monitoring off), at least a few minutes long.</summary>
    public List<(DateTime StartUtc, DateTime EndUtc)> Gaps { get; init; } = new();
    public double ObservedSec => UpSec + DegradedSec + DownSec;
    public double? AvailabilityPct => ObservedSec > 0 ? 100.0 * (UpSec + DegradedSec) / ObservedSec : null;
    public double? AvgRtt { get; init; }
    public double? MaxRtt { get; init; }
    public double? AvgLoss { get; init; }
    public int CycleCount => Rows.Count;
    public required List<Incident> Incidents { get; init; }
    public required List<Bucket> Buckets { get; init; }
    public required List<CycleRow> Rows { get; init; }
    public required List<EventRow> Events { get; init; }
    public List<CheckRow> CheckRows { get; init; } = new();
    public IntegrityInfo? Integrity { get; init; }
}

/// <summary>Read-only access to log.db (separate connection, WAL allows reading while the writer works).</summary>
public static class HistoryStore
{
    static SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DbFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        c.Open();
        return c;
    }

    static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static List<CycleRow> Cycles(string dev, long fromMs, long toMs)
    {
        var list = new List<CycleRow>();
        if (!File.Exists(Paths.DbFile)) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,sent,recv,loss,rmin,ravg,rmax,jitter,status,error FROM cycles WHERE dev=$d AND ts>=$f AND ts<$t ORDER BY ts";
        cmd.Parameters.AddWithValue("$d", dev);
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$t", toMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new CycleRow(
                r.GetInt64(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? 0 : r.GetInt32(2),
                r.IsDBNull(3) ? 100 : r.GetDouble(3),
                N(r, 4), N(r, 5), N(r, 6), N(r, 7),
                r.IsDBNull(8) ? DevStatus.Unknown : (DevStatus)r.GetInt32(8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        }
        return list;
    }

    public static List<EventRow> Events(string dev, long fromMs, long toMs)
    {
        var list = new List<EventRow>();
        if (!File.Exists(Paths.DbFile)) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,st_from,st_to,text FROM events WHERE dev=$d AND ts>=$f AND ts<$t ORDER BY ts";
        cmd.Parameters.AddWithValue("$d", dev);
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$t", toMs);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEvent(r));
        return list;
    }

    public static List<CheckRow> Checks(string dev, long fromMs, long toMs)
    {
        var list = new List<CheckRow>();
        if (!File.Exists(Paths.DbFile)) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,chk,ok,ms,status,detail FROM checks WHERE dev=$d AND ts>=$f AND ts<$t ORDER BY ts";
        cmd.Parameters.AddWithValue("$d", dev);
        cmd.Parameters.AddWithValue("$f", fromMs);
        cmd.Parameters.AddWithValue("$t", toMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CheckRow(r.GetInt64(0), r.GetString(1), !r.IsDBNull(2) && r.GetInt32(2) != 0, N(r, 3),
                r.IsDBNull(4) ? DevStatus.Unknown : (DevStatus)r.GetInt32(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        return list;
    }

    public static IntegrityInfo? Verify(string dev, long fromMs, long toMs, byte[]? adminSealKey)
    {
        if (!File.Exists(Paths.DbFile) || !InstallKey.Loaded) return null;
        try
        {
            using var c = Open();
            return Sealer.Verify(c, dev, fromMs, toMs, InstallKey.Derive("seal"), adminSealKey);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Integrity verify", ex);
            return null;
        }
    }

    public static EventRow? LastEventBefore(string dev, long fromMs)
    {
        if (!File.Exists(Paths.DbFile)) return null;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts,st_from,st_to,text FROM events WHERE dev=$d AND ts<$f ORDER BY ts DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$d", dev);
        cmd.Parameters.AddWithValue("$f", fromMs);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEvent(r) : null;
    }

    static EventRow ReadEvent(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.IsDBNull(1) ? DevStatus.Unknown : (DevStatus)r.GetInt32(1),
        r.IsDBNull(2) ? DevStatus.Unknown : (DevStatus)r.GetInt32(2),
        r.IsDBNull(3) ? "" : r.GetString(3));
}

public static class HistoryCalc
{
    public const int BucketCount = 240;

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>
    /// Time accounting uses confirmed statuses (events) limited to the time the monitor actually ran
    /// (coverage = time around ping cycles; gaps longer than 3 median intervals are "no data").
    /// </summary>
    /// <summary>Chart buckets for [f, t): average / max latency, average loss, worst state (rows are sorted by time).</summary>
    public static List<Bucket> Bucketize(IReadOnlyList<CycleRow> cycles, long f, long t, int n)
    {
        n = Math.Max(1, n);
        double bw = Math.Max(1, (t - f) / (double)n);
        var sumRtt = new double[n];
        var cntRtt = new int[n];
        var maxR = new double[n];
        var sumLoss = new double[n];
        var cntLoss = new int[n];
        var worst = new DevStatus[n];
        var any = new bool[n];
        // binary search for the first row of the window
        int lo = 0, hi = cycles.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cycles[mid].Ts < f) lo = mid + 1; else hi = mid;
        }
        for (int k = lo; k < cycles.Count; k++)
        {
            var c = cycles[k];
            if (c.Ts >= t) break;
            int i = Math.Clamp((int)((c.Ts - f) / bw), 0, n - 1);
            bool first = !any[i];
            any[i] = true;
            if (c.Avg is double a) { sumRtt[i] += a; cntRtt[i]++; }
            if (c.Max is double m && m > maxR[i]) maxR[i] = m;
            if (c.Sent > 0) { sumLoss[i] += c.Loss; cntLoss[i]++; }
            if (first || StatusText.Severity(c.Raw) > StatusText.Severity(worst[i])) worst[i] = c.Raw;
        }
        var buckets = new List<Bucket>(n);
        for (int i = 0; i < n; i++)
        {
            buckets.Add(new Bucket(
                Utc(f + (long)(i * bw)),
                cntRtt[i] > 0 ? sumRtt[i] / cntRtt[i] : null,
                cntRtt[i] > 0 ? maxR[i] : null,
                cntLoss[i] > 0 ? sumLoss[i] / cntLoss[i] : null,
                any[i] ? worst[i] : DevStatus.Unknown,
                !any[i]));
        }
        return buckets;
    }

    /// <summary>Event texts are stored in the interface language of the moment: recognise the "parent unreachable" marker in both.</summary>
    static bool IsParentEvent(string text) =>
        text.Contains("батько", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("батьків", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("parent", StringComparison.OrdinalIgnoreCase);

    public static HistoryResult Compute(Device d, DateTime fromUtc, DateTime toUtc, byte[]? adminSealKey = null)
    {
        long f = Ms(fromUtc), t = Ms(toUtc);
        var cycles = HistoryStore.Cycles(d.Id, f, t);
        var events = HistoryStore.Events(d.Id, f, t);
        var before = HistoryStore.LastEventBefore(d.Id, f);

        // --- gap cap from the typical interval between cycles
        long cap = 30_000;
        if (cycles.Count > 1)
        {
            var deltas = new List<long>();
            for (int i = 1; i < Math.Min(cycles.Count, 2000); i++) deltas.Add(cycles[i].Ts - cycles[i - 1].Ts);
            deltas.Sort();
            cap = Math.Max(30_000, deltas[deltas.Count / 2] * 3);
        }

        // --- coverage intervals (merged)
        var cov = new List<(long S, long E)>();
        for (int i = 0; i < cycles.Count; i++)
        {
            long ts = cycles[i].Ts;
            long s = i == 0 && ts - f <= cap ? f : ts;
            long next = i + 1 < cycles.Count ? cycles[i + 1].Ts : t;
            long e = Math.Min(Math.Min(next, ts + cap), t);
            if (e <= s) continue;
            if (cov.Count > 0 && s <= cov[^1].E) cov[^1] = (cov[^1].S, Math.Max(cov[^1].E, e));
            else cov.Add((s, e));
        }
        long covered = cov.Sum(c => c.E - c.S);

        // --- monitoring gaps = the parts of the period not covered (short ones are ignored)
        var gaps = new List<(DateTime, DateTime)>();
        long minGap = Math.Max(cap, 120_000), pos = f;
        foreach (var c in cov)
        {
            if (c.S - pos >= minGap) gaps.Add((Utc(pos), Utc(c.S)));
            pos = Math.Max(pos, c.E);
        }
        long tailEnd = Math.Min(t, Ms(DateTime.UtcNow));
        if (tailEnd - pos >= minGap) gaps.Add((Utc(pos), Utc(tailEnd)));

        // --- confirmed status timeline
        var segs = new List<(long S, long E, DevStatus St, bool Parent)>();
        var cur = before?.To ?? DevStatus.Unknown;
        bool curParent = before is not null && IsParentEvent(before.Text);
        long segStart = f;
        foreach (var ev in events)
        {
            if (ev.Ts > segStart) segs.Add((segStart, ev.Ts, cur, curParent));
            cur = ev.To;
            curParent = IsParentEvent(ev.Text);
            segStart = Math.Max(segStart, ev.Ts);
        }
        if (t > segStart) segs.Add((segStart, t, cur, curParent));

        double up = 0, deg = 0, down = 0, other = 0;
        var incidents = new List<Incident>();
        foreach (var sg in segs)
        {
            long firstS = -1, lastE = -1, sum = 0;
            int pieces = 0;
            foreach (var c in cov)
            {
                long a = Math.Max(sg.S, c.S), b = Math.Min(sg.E, c.E);
                if (b <= a) continue;
                sum += b - a;
                pieces++;
                if (firstS < 0) firstS = a;
                lastE = b;
            }
            double sec = sum / 1000.0;
            switch (sg.St)
            {
                case DevStatus.Up: up += sec; break;
                case DevStatus.Degraded: deg += sec; break;
                case DevStatus.Down: down += sec; break;
                default: other += sec; break;
            }
            if (sg.St is DevStatus.Down or DevStatus.Degraded && pieces > 0)
            {
                incidents.Add(new Incident(
                    sg.St, Utc(firstS), Utc(lastE), sec,
                    StartedBefore: sg.S == f && firstS == f && before?.To == sg.St,
                    Ongoing: sg.E == t && lastE >= t - cap && toUtc >= DateTime.UtcNow.AddMinutes(-1),
                    ParentDown: sg.Parent,
                    HasGap: pieces > 1));
            }
        }
        double noData = (t - f) / 1000.0 - covered / 1000.0 + other;

        // --- cycle stats
        double? avgRtt = null, maxRtt = null, avgLoss = null;
        var withRtt = cycles.Where(c => c.Avg.HasValue).ToList();
        if (withRtt.Count > 0)
        {
            avgRtt = withRtt.Average(c => c.Avg!.Value);
            maxRtt = cycles.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).DefaultIfEmpty(0).Max();
        }
        var sent = cycles.Where(c => c.Sent > 0).ToList();
        if (sent.Count > 0) avgLoss = sent.Average(c => c.Loss);

        // --- chart buckets
        var buckets = Bucketize(cycles, f, t, BucketCount);

        return new HistoryResult
        {
            Device = d,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UpSec = up,
            DegradedSec = deg,
            DownSec = down,
            NoDataSec = Math.Max(0, noData),
            Gaps = gaps,
            AvgRtt = avgRtt,
            MaxRtt = maxRtt,
            AvgLoss = avgLoss,
            Incidents = incidents,
            Buckets = buckets,
            Rows = cycles,
            Events = events,
            CheckRows = HistoryStore.Checks(d.Id, f, t),
            Integrity = HistoryStore.Verify(d.Id, f, t, adminSealKey),
        };
    }
}

/// <summary>Plain-language texts for the window and the PDF.</summary>
public static class HistoryText
{
    static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("uk-UA");

    public static string Num(double v, string fmt = "0.##") => v.ToString(fmt, Uk);

    public static string Dur(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Round(Math.Max(0, sec)));
        if (ts.TotalMinutes < 1) return L.RF("{0} s", ts.Seconds);
        if (ts.TotalHours < 1) return L.RF("{0} min", ts.Minutes);
        if (ts.TotalDays < 1) return ts.Minutes > 0 ? L.RF("{0} h {1} min", (int)ts.TotalHours, ts.Minutes) : L.RF("{0} h", (int)ts.TotalHours);
        return ts.Hours > 0 ? L.RF("{0} d {1} h", (int)ts.TotalDays, ts.Hours) : L.RF("{0} d", (int)ts.TotalDays);
    }

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    public static string Times(int n) => L.ReportLanguage == "uk"
        ? $"{n} {Plural(n, "раз", "рази", "разів")}"
        : $"{n} {(n == 1 ? "time" : "times")}";

    public static string Period(HistoryResult r) =>
        $"{r.FromUtc.ToLocalTime():dd.MM.yyyy HH:mm} — {r.ToUtc.ToLocalTime():dd.MM.yyyy HH:mm}";

    public static string KindText(Incident i) => i.Kind == DevStatus.Down
        ? (i.ParentDown ? L.R("down (parent device unreachable)") : L.R("down"))
        : L.R("unstable operation");

    public static string IncidentLine(Incident i)
    {
        var start = i.StartedBefore ? L.R("before the start of the period") : i.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        var end = i.Ongoing ? L.R("ongoing") : i.EndUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        var notes = i.HasGap ? L.R(" (with a monitoring gap)") : "";
        return $"{start} — {end}   {KindText(i)}, {Dur(i.DurationSec)}{notes}";
    }

    public static string IntegrityText(HistoryResult r)
    {
        var i = r.Integrity;
        if (i is null) return L.R("Could not verify the log integrity.");
        if (!i.Ok)
            return L.RF("WARNING: log records for {0} h were changed or added after sealing: ", i.BadHours.Count) +
                   string.Join(", ", i.BadHours.Take(10).Select(h => h.ToLocalTime().ToString("dd.MM HH:00"))) +
                   (i.BadHours.Count > 10 ? " …" : "") + ".";
        var s = L.RF("Log is intact: {0} h of sealed records verified", i.SealedHours);
        if (i.AdminSealedUntilUtc is { } a)
            s += L.RF("; certified by the administrator until {0:dd.MM.yyyy HH:mm}", a.ToLocalTime()) + (i.AdminVerified ? L.R(" (verified)") : "");
        if (i.SealedUntilUtc is { } u && u < r.ToUtc)
            s += L.RF("; records after {0:dd.MM HH:mm} are not sealed yet (sealing runs every hour)", u.ToLocalTime());
        return s + ".";
    }

    public static string Summary(HistoryResult r)
    {
        var sb = new StringBuilder();
        var d = r.Device;
        if (r.ObservedSec <= 0)
        {
            sb.Append(L.RF("For the period {0} there is no data about device \"{1}\" ({2}): monitoring was not running or the log has already been purged by the retention period.", Period(r), d.Name, d.Address));
            return sb.ToString();
        }

        sb.Append(L.RF("For the period {0} device \"{1}\" ({2}) was up {3}% of the observed time.", Period(r), d.Name, d.Address, Num(r.AvailabilityPct ?? 0)));

        var downs = r.Incidents.Where(i => i.Kind == DevStatus.Down).ToList();
        if (downs.Count > 0)
        {
            var longest = downs.OrderByDescending(i => i.DurationSec).First();
            sb.Append(L.RF(" Connection was lost {0}, {1} in total; the longest outage was {2} ({3:dd.MM HH:mm} – {4}).", Times(downs.Count), Dur(r.DownSec), Dur(longest.DurationSec), longest.StartUtc.ToLocalTime(), (longest.Ongoing ? L.R("ongoing") : longest.EndUtc.ToLocalTime().ToString("dd.MM HH:mm"))));
            int parent = downs.Count(i => i.ParentDown);
            if (parent > 0) sb.Append(L.RF(" {0} of them due to the parent device being unreachable.", Times(parent)));
        }
        else
        {
            sb.Append(L.R(" No connection outages were recorded."));
        }

        var degs = r.Incidents.Where(i => i.Kind == DevStatus.Degraded).ToList();
        if (degs.Count > 0)
            sb.Append(L.RF(" Unstable operation (packet loss or high latency): {0}, {1} in total.", Times(degs.Count), Dur(r.DegradedSec)));

        if (r.AvgRtt is double avg)
        {
            sb.Append(L.RF(" Average response time: {0} ms", Num(avg, "0.#")));
            if (r.MaxRtt is double mx) sb.Append(L.RF(", maximum {0} ms", Num(mx, "0.#")));
            sb.Append('.');
        }
        if (r.AvgLoss is double loss)
            sb.Append(d.Primary is null
                ? L.RF(" Average packet loss: {0}%.", Num(loss, "0.##"))
                : L.RF(" {0}% of checks failed.", Num(loss, "0.##")));

        double total = (r.ToUtc - r.FromUtc).TotalSeconds;
        if (total > 0 && r.NoDataSec / total > 0.01)
            sb.Append(L.RF(" Monitoring was not running for {0} ({1}% of the period): there is no data for this time and it is not included in the availability percentage.", Dur(r.NoDataSec), Num(100 * r.NoDataSec / total, "0.#")));
        return sb.ToString();
    }

    /// <summary>What defines the device state: ICMP profile or the main check.</summary>
    public static string CheckingText(AppConfig cfg, Device d) =>
        d.Primary is { } pc ? PrimaryText(pc) : ProfileText(ProfileResolver.Effective(cfg, d));

    public static string PrimaryText(ExtraCheck c)
    {
        var what = c.Kind switch
        {
            CheckKind.Tcp => L.RF("TCP connection to port {0}", c.Port),
            CheckKind.Http => L.RF("HTTP(S) request {0}, success — codes {1}", (string.IsNullOrWhiteSpace(c.Url) ? "/" : c.Url.Trim()), c.ExpectCodes) +
                              (string.IsNullOrEmpty(c.ExpectText) ? "" : L.RF(" and text \"{0}\"", c.ExpectText)),
            CheckKind.Json => L.RF("HTTP JSON {0}, value \"{1}\", \"up\" when {2}", (string.IsNullOrWhiteSpace(c.Url) ? "/" : c.Url.Trim()), c.JsonPath, JsonValue.Describe(c.OkOp, c.OkValue, c.OkValue2)),
            _ => L.RF("DNS query {0} ({1}) to the device as a DNS server", c.DnsName, c.DnsType),
        };
        var sb = new StringBuilder(L.RF("main check without ping: {0}; every {1} s, timeout {2} ms", what, c.IntervalSec, c.TimeoutMs));
        if (c.SlowMs > 0) sb.Append(L.RF("; \"unstable\" — response slower than {0} ms", c.SlowMs));
        sb.Append(L.RF("; the state changes after {0} consecutive checks", c.ConfirmCycles));
        return sb.ToString();
    }

    public static string ProfileText(EffProfile p)
    {
        var sb = new StringBuilder(L.RF("every {0} s, {1} packets ({2} bytes), response timeout {3} ms; ", p.IntervalSec, p.PacketCount, p.PacketSize, p.TimeoutMs));
        sb.Append(L.RF("\"down\" — loss from {0}%", p.DownLossPct));
        var deg = new List<string>();
        if (p.DegLossPct > 0) deg.Add(L.RF("loss from {0}%", p.DegLossPct));
        if (p.DegAvgMs > 0) deg.Add(L.RF("average latency above {0} ms", p.DegAvgMs));
        if (p.DegMaxMs > 0) deg.Add(L.RF("maximum latency above {0} ms", p.DegMaxMs));
        if (p.DegJitterMs > 0) deg.Add(L.RF("latency jitter above {0} ms", p.DegJitterMs));
        if (deg.Count > 0) sb.Append(L.R("; \"unstable\" — ") + string.Join(L.R(" or "), deg));
        sb.Append(L.RF("; the state changes after {0} consecutive checks", p.ConfirmCycles));
        return sb.ToString();
    }

    /// <summary>Raw log of one device: every ping cycle (tab-separated, invariant numbers) + status changes.</summary>
    public static string RawLog(HistoryResult r, AppConfig cfg)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(double? v) => v.HasValue ? v.Value.ToString("0.###", inv) : "";
        var d = r.Device;
        var sb = new StringBuilder();
        sb.AppendLine(L.R("FreeMon — raw check log"));
        sb.AppendLine(L.RF("Device:\t{0}", d.Name));
        sb.AppendLine(L.RF("Address:\t{0}", d.Address));
        sb.AppendLine(L.RF("Type:\t{0}", AppConfig.TypeLabelReport(d.Type)));
        sb.AppendLine(L.RF("Period:\t{0}", Period(r)));
        sb.AppendLine(L.RF("Parameters:\t{0}", CheckingText(cfg, d)));
        sb.AppendLine(L.RF("Generated:\t{0:dd.MM.yyyy HH:mm:ss}", DateTime.Now));
        sb.AppendLine(L.RF("Checks:\t{0}", r.CycleCount));
        sb.AppendLine();
        sb.AppendLine(L.R("== Checks =="));
        sb.AppendLine(L.R("time\tsent\treceived\tloss_%\tlatency_min_ms\tlatency_avg_ms\tlatency_max_ms\tjitter_ms\tresult\terror"));
        foreach (var c in r.Rows)
        {
            sb.Append(HistoryCalc.Utc(c.Ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\t')
              .Append(c.Sent).Append('\t')
              .Append(c.Recv).Append('\t')
              .Append(c.Loss.ToString("0.##", inv)).Append('\t')
              .Append(F(c.Min)).Append('\t')
              .Append(F(c.Avg)).Append('\t')
              .Append(F(c.Max)).Append('\t')
              .Append(F(c.Jitter)).Append('\t')
              .Append(StatusText.Rep(c.Raw)).Append('\t')
              .Append(c.Error ?? "")
              .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine(L.R("== State changes =="));
        foreach (var e in r.Events)
            sb.Append(HistoryCalc.Utc(e.Ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\t').AppendLine(e.Text);

        if (r.CheckRows.Count > 0)
        {
            var titles = d.Checks.ToDictionary(c => c.Id, c => c.Title());
            sb.AppendLine();
            sb.AppendLine(L.R("== Additional checks =="));
            sb.AppendLine(L.R("time\tcheck\tsuccess\tresponse_time_ms\tresult\tdetails"));
            foreach (var c in r.CheckRows)
            {
                sb.Append(HistoryCalc.Utc(c.Ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\t')
                  .Append(titles.TryGetValue(c.Check, out var tt) ? tt : L.R("(deleted check ") + c.Check[..Math.Min(8, c.Check.Length)] + ")").Append('\t')
                  .Append(c.Ok ? L.R("yes") : L.R("no")).Append('\t')
                  .Append(F(c.Ms)).Append('\t')
                  .Append(StatusText.Rep(c.Status)).Append('\t')
                  .Append(c.Detail)
                  .AppendLine();
            }
        }

        if (r.Gaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L.R("== Monitoring gaps =="));
            sb.AppendLine(L.R("start\tend\tduration"));
            foreach (var (s, e) in r.Gaps)
                sb.Append(s.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\t')
                  .Append(e.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv)).Append('\t')
                  .AppendLine(Dur((e - s).TotalSeconds));
        }

        sb.AppendLine();
        sb.AppendLine(L.R("== Log integrity =="));
        sb.AppendLine(IntegrityText(r));
        if (InstallKey.Loaded)
            sb.AppendLine(L.R("Report verification code:\t") + ReportCode.Create(r));
        return sb.ToString();
    }
}
