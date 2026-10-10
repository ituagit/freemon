using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FreeMon;

public sealed record CheckResult(DateTime TimeUtc, bool Ok, double? Ms, string Detail, DevStatus Raw);

public sealed record CheckSnapshot(DevStatus Status, DateTime SinceUtc, CheckResult? Last);

public sealed class CheckRuntime
{
    volatile CheckSnapshot _snap;

    public Device Device { get; internal set; }
    public ExtraCheck Check { get; internal set; }
    public CheckSnapshot Snap { get => _snap; internal set => _snap = value; }
    public string Key => Device.Id + "#" + Check.Id;

    internal DevStatus Pending;
    internal int PendingCount;

    public CheckRuntime(Device d, ExtraCheck c)
    {
        Device = d;
        Check = c;
        _snap = new CheckSnapshot(DevStatus.Unknown, DateTime.UtcNow, null);
    }
}

/// <summary>One probe of an additional check. Stateless except for the HttpClient passed in.</summary>
public static class CheckProbes
{
    public static HttpClient CreateHttpClient(ExtraCheck c)
    {
        // Network policy: connect ONLY to the configured address.
        // No system/env proxy, no redirects to other hosts, no certificate/CRL/OCSP downloads from the internet.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(c.TimeoutMs),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        if (c.IgnoreCertErrors)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(c.TimeoutMs) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeMon/" + AppInfo.Version.Split('+')[0]);
        return client;
    }

    public static async Task<CheckResult> RunAsync(Device d, ExtraCheck c, HttpClient? http, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            return c.Kind switch
            {
                CheckKind.Tcp => await TcpAsync(d.Address.Trim(), c, started, ct).ConfigureAwait(false),
                CheckKind.Http => await HttpAsync(d.Address.Trim(), c, http, started, ct).ConfigureAwait(false),
                CheckKind.Json => await JsonAsync(d.Address.Trim(), c, http, started, ct).ConfigureAwait(false),
                _ => await DnsAsync(d.Address.Trim(), c, started, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(started, ex.Message);
        }
    }

    static CheckResult Fail(DateTime t, string detail) => new(t, false, null, detail, DevStatus.Down);

    static CheckResult Ok(DateTime t, double ms, string detail, ExtraCheck c) =>
        new(t, true, ms, detail, c.SlowMs > 0 && ms > c.SlowMs ? DevStatus.Degraded : DevStatus.Up);

    static string Ms(double ms) => ms.ToString("0.#", CultureInfo.InvariantCulture) + L.T(" ms");

    // ---------------------------------------------------------------- TCP
    static async Task<CheckResult> TcpAsync(string host, ExtraCheck c, DateTime started, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);
        using var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        try
        {
            if (IPAddress.TryParse(host, out var ip)) await s.ConnectAsync(ip, c.Port, cts.Token).ConfigureAwait(false);
            else await s.ConnectAsync(host, c.Port, cts.Token).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            return Ok(started, ms, L.F("port {0} open, connected in {1}", c.Port, Ms(ms)), c);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(started, L.F("port {0}: no answer within {1} ms", c.Port, c.TimeoutMs));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return Fail(started, L.F("port {0} closed (connection refused)", c.Port));
        }
        catch (SocketException ex)
        {
            return Fail(started, L.F("port {0}: {1}", c.Port, ex.Message));
        }
    }

    // ---------------------------------------------------------------- HTTP(S)
    public static Uri BuildUri(string address, string url)
    {
        url = (url ?? "").Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(url);
        var host = address.Contains(':') ? $"[{address}]" : address;
        var path = url.Length == 0 ? "/" : url.StartsWith('/') ? url : "/" + url;
        return new Uri($"http://{host}{path}");
    }

    public static bool CodeMatches(int code, string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) spec = "200-399";
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 &&
                int.TryParse(part[..dash], out var a) &&
                int.TryParse(part[(dash + 1)..], out var b))
            {
                if (code >= a && code <= b) return true;
            }
            else if (int.TryParse(part, out var one) && code == one)
            {
                return true;
            }
        }
        return false;
    }

    public static bool CodesValid(string spec) =>
        !string.IsNullOrWhiteSpace(spec) &&
        spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(p =>
        {
            var dash = p.IndexOf('-');
            return dash > 0
                ? int.TryParse(p[..dash], out var a) && int.TryParse(p[(dash + 1)..], out var b) && a >= 100 && b <= 599 && a <= b
                : int.TryParse(p, out var one) && one >= 100 && one <= 599;
        });

    static async Task<CheckResult> HttpAsync(string host, ExtraCheck c, HttpClient? http, DateTime started, CancellationToken ct)
    {
        var own = http is null;
        http ??= CreateHttpClient(c);
        try
        {
            var uri = BuildUri(host, c.Url);
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (!CodeMatches(code, c.ExpectCodes))
                return Fail(started, L.F("HTTP {0} {1} (expected {2})", code, resp.ReasonPhrase, c.ExpectCodes));
            if (!string.IsNullOrEmpty(c.ExpectText))
            {
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buf = new byte[1024 * 1024];
                int total = 0, n;
                while (total < buf.Length && (n = await body.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false)) > 0) total += n;
                var text = Encoding.UTF8.GetString(buf, 0, total);
                if (!text.Contains(c.ExpectText, StringComparison.OrdinalIgnoreCase))
                    return Fail(started, L.F("HTTP {0}, but the text \"{1}\" was not found in the response", code, c.ExpectText));
            }
            var ms = sw.Elapsed.TotalMilliseconds;
            return Ok(started, ms, L.F("HTTP {0} in {1}", code, Ms(ms)), c);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(started, L.F("no answer within {0} ms", c.TimeoutMs));
        }
        catch (HttpRequestException ex)
        {
            return Fail(started, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (own) http.Dispose();
        }
    }

    // ---------------------------------------------------------------- HTTP JSON
    /// <summary>
    /// GET the URL, take a value from the JSON response by a simple path and compare it:
    /// the "Up" condition → Up; otherwise the "Unstable" condition (if set) → Degraded; otherwise Down.
    /// </summary>
    static async Task<CheckResult> JsonAsync(string host, ExtraCheck c, HttpClient? http, DateTime started, CancellationToken ct)
    {
        var own = http is null;
        http ??= CreateHttpClient(c);
        try
        {
            var uri = BuildUri(host, c.Url);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.ParseAdd("application/json");
            foreach (var line in (c.Headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = line.IndexOf(':');
                if (i > 0) req.Headers.TryAddWithoutValidation(line[..i].Trim(), line[(i + 1)..].Trim());
            }
            var sw = Stopwatch.StartNew();
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code < 200 || code > 299)
                return Fail(started, L.F("HTTP {0} {1}", code, resp.ReasonPhrase ?? ""));
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, default, ct).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (!JsonValue.TryGet(doc.RootElement, c.JsonPath, out var el))
            {
                bool okIfMissing = c.OkOp == JsonOp.None;
                return okIfMissing
                    ? Ok(started, ms, L.F("no value at \"{0}\"", c.JsonPath), c)
                    : Fail(started, L.F("no value at \"{0}\" in the response", c.JsonPath));
            }
            var value = JsonValue.Text(el);
            if (JsonValue.Matches(el, c.OkOp, c.OkValue, c.OkValue2))
                return new CheckResult(started, true, ms, L.F("value {0}", value), c.SlowMs > 0 && ms > c.SlowMs ? DevStatus.Degraded : DevStatus.Up);
            if (c.WarnOp != JsonOp.None && JsonValue.Matches(el, c.WarnOp, c.WarnValue, c.WarnValue2))
                return new CheckResult(started, true, ms, L.F("value {0} (warning condition)", value), DevStatus.Degraded);
            return new CheckResult(started, false, ms, L.F("value {0} does not meet the condition {1}", value, JsonValue.Describe(c.OkOp, c.OkValue, c.OkValue2)), DevStatus.Down);
        }
        catch (JsonException ex)
        {
            return Fail(started, L.T("the response is not valid JSON: ") + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(started, L.F("no answer within {0} ms", c.TimeoutMs));
        }
        catch (HttpRequestException ex)
        {
            return Fail(started, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (own) http.Dispose();
        }
    }

    // ---------------------------------------------------------------- DNS
    /// <summary>
    /// The device is queried AS A DNS SERVER (UDP 53): "does this server answer for name X".
    /// OK = NOERROR with at least one A/AAAA answer (and the expected address, if set).
    /// </summary>
    static async Task<CheckResult> DnsAsync(string server, ExtraCheck c, DateTime started, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.DnsName)) return Fail(started, L.T("no name for the DNS query"));
        IPAddress addr;
        if (!IPAddress.TryParse(server, out var parsed))
        {
            var list = await Dns.GetHostAddressesAsync(server, AddressFamily.Unspecified, ct).ConfigureAwait(false);
            addr = list.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? list.First();
        }
        else
        {
            addr = parsed;
        }

        bool aaaa = c.DnsType == "AAAA";
        ushort id = (ushort)Random.Shared.Next(1, 65535);
        var query = BuildQuery(id, c.DnsName.Trim(), aaaa ? (ushort)28 : (ushort)1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(c.TimeoutMs);
        using var udp = new UdpClient(addr.AddressFamily);
        var ep = new IPEndPoint(addr, 53);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, ep, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var b = r.Buffer;
                if (b.Length < 12 || ((b[0] << 8) | b[1]) != id || (b[2] & 0x80) == 0) continue;
                var ms = sw.Elapsed.TotalMilliseconds;
                int rcode = b[3] & 0x0F;
                if (rcode != 0) return Fail(started, L.F("DNS server answered {0}", RcodeText(rcode)));
                var ips = ParseAnswers(b, aaaa ? 28 : 1);
                if (ips.Count == 0) return Fail(started, L.F("DNS server answered, but with no {0} record for {1}", (aaaa ? "AAAA" : "A"), c.DnsName));
                if (!string.IsNullOrWhiteSpace(c.DnsExpect) &&
                    IPAddress.TryParse(c.DnsExpect.Trim(), out var want) &&
                    !ips.Any(i => i.Equals(want)))
                    return Fail(started, L.F("{0} → {1}, expected {2}", c.DnsName, string.Join(", ", ips), want));
                return Ok(started, ms, L.F("{0} → {1} in {2}", c.DnsName, string.Join(", ", ips.Take(4)), Ms(ms)), c);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(started, L.F("DNS server did not answer within {0} ms", c.TimeoutMs));
        }
        catch (SocketException ex)
        {
            return Fail(started, "DNS: " + ex.Message);
        }
    }

    static string RcodeText(int rcode) => rcode switch
    {
        1 => L.T("FORMERR (query format error)"),
        2 => L.T("SERVFAIL (server failure)"),
        3 => L.T("NXDOMAIN (no such name)"),
        4 => L.T("NOTIMP (query not supported)"),
        5 => L.T("REFUSED (server refused)"),
        _ => L.F("with error code {0}", rcode),
    };

    static byte[] BuildQuery(ushort id, string name, ushort qtype)
    {
        var ascii = new IdnMapping().GetAscii(name.TrimEnd('.'));
        var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)id);
        ms.WriteByte(0x01); // RD
        ms.WriteByte(0x00);
        ms.Write(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 8); // QD=1, AN=0, NS=0, AR=0
        foreach (var label in ascii.Split('.'))
        {
            var lb = Encoding.ASCII.GetBytes(label);
            if (lb.Length is 0 or > 63) throw new ArgumentException(L.T("invalid name for the DNS query"));
            ms.WriteByte((byte)lb.Length);
            ms.Write(lb, 0, lb.Length);
        }
        ms.WriteByte(0);
        ms.WriteByte((byte)(qtype >> 8));
        ms.WriteByte((byte)qtype);
        ms.WriteByte(0);
        ms.WriteByte(1); // IN
        return ms.ToArray();
    }

    static int SkipName(byte[] b, int i)
    {
        while (i < b.Length)
        {
            int len = b[i];
            if (len == 0) return i + 1;
            if ((len & 0xC0) == 0xC0) return i + 2;
            i += len + 1;
        }
        return -1;
    }

    static List<IPAddress> ParseAnswers(byte[] b, int wantType)
    {
        var res = new List<IPAddress>();
        int qd = (b[4] << 8) | b[5];
        int an = (b[6] << 8) | b[7];
        int i = 12;
        for (int q = 0; q < qd && i >= 0; q++)
        {
            i = SkipName(b, i);
            if (i < 0) return res;
            i += 4;
        }
        for (int a = 0; a < an && i >= 0 && i < b.Length; a++)
        {
            i = SkipName(b, i);
            if (i < 0 || i + 10 > b.Length) break;
            int type = (b[i] << 8) | b[i + 1];
            int rdlen = (b[i + 8] << 8) | b[i + 9];
            i += 10;
            if (i + rdlen > b.Length) break;
            if (type == wantType && (rdlen == 4 || rdlen == 16))
                res.Add(new IPAddress(b.AsSpan(i, rdlen)));
            i += rdlen;
        }
        return res;
    }
}

/// <summary>Value lookup in JSON by a simple path and comparison (numbers numerically, text case-insensitively).</summary>
public static class JsonValue
{
    public static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        var p = (path ?? "").Trim();
        if (p.StartsWith("$")) p = p[1..];
        p = p.TrimStart('.');
        if (p.Length == 0) return true;
        foreach (var raw in p.Split('.'))
        {
            var part = raw;
            int br = part.IndexOf('[');
            var name = br >= 0 ? part[..br] : part;
            if (name.Length > 0)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return false;
            }
            while (br >= 0)
            {
                int end = part.IndexOf(']', br);
                if (end < 0 || !int.TryParse(part[(br + 1)..end], out var idx)) return false;
                if (value.ValueKind != JsonValueKind.Array || idx < 0 || idx >= value.GetArrayLength()) return false;
                value = value[idx];
                br = part.IndexOf('[', end);
            }
        }
        return true;
    }

    public static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    static bool Num(string s, out double v) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    public static bool Matches(JsonElement e, JsonOp op, string a, string b)
    {
        var text = Text(e);
        bool isNum = Num(text, out var x);
        switch (op)
        {
            case JsonOp.None:
            case JsonOp.Exists:
                return true;
            case JsonOp.Equals:
                return isNum && Num(a, out var ea) ? x == ea : string.Equals(text, a.Trim(), StringComparison.OrdinalIgnoreCase);
            case JsonOp.NotEquals:
                return !(isNum && Num(a, out var na) ? x == na : string.Equals(text, a.Trim(), StringComparison.OrdinalIgnoreCase));
            case JsonOp.Contains:
                return text.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase);
            case JsonOp.Regex:
                try { return System.Text.RegularExpressions.Regex.IsMatch(text, a, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
                catch { return false; }
            case JsonOp.Less: return isNum && Num(a, out var l1) && x < l1;
            case JsonOp.LessOrEqual: return isNum && Num(a, out var l2) && x <= l2;
            case JsonOp.Greater: return isNum && Num(a, out var g1) && x > g1;
            case JsonOp.GreaterOrEqual: return isNum && Num(a, out var g2) && x >= g2;
            case JsonOp.Between:
                return isNum && Num(a, out var lo) && Num(b, out var hi) && x >= Math.Min(lo, hi) && x <= Math.Max(lo, hi);
            default:
                return false;
        }
    }

    public static string Symbol(JsonOp op) => op switch
    {
        JsonOp.Exists => L.T("exists"),
        JsonOp.Equals => "=",
        JsonOp.NotEquals => "≠",
        JsonOp.Contains => L.T("contains"),
        JsonOp.Less => "<",
        JsonOp.LessOrEqual => "≤",
        JsonOp.Greater => ">",
        JsonOp.GreaterOrEqual => "≥",
        JsonOp.Between => L.T("between"),
        JsonOp.Regex => L.T("matches regex"),
        _ => L.T("not set"),
    };

    public static string Describe(JsonOp op, string a, string b) => op switch
    {
        JsonOp.Exists or JsonOp.None => Symbol(op),
        JsonOp.Between => $"{Symbol(op)} {a.Trim()} … {b.Trim()}",
        _ => $"{Symbol(op)} {a.Trim()}",
    };
}
