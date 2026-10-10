using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FreeMon;

public sealed class ChannelSecret
{
    public string Url { get; set; } = "";
    /// <summary>Extra HTTP headers, one per line: "Name: value".</summary>
    public string Headers { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
}

/// <summary>
/// Secrets of alert channels live outside config.json: secrets.json in the data folder,
/// on Linux/macOS readable only by the owner (0600).
/// </summary>
public static class SecretStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly object Gate = new();

    static byte[] Key => InstallKey.Derive("secrets");

    public static Dictionary<string, ChannelSecret> Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Paths.SecretsFile))
                {
                    var plain = Aead.Decrypt(Key, File.ReadAllBytes(Paths.SecretsFile), "secrets");
                    return JsonSerializer.Deserialize<Dictionary<string, ChannelSecret>>(plain, Opts) ?? new();
                }
                if (File.Exists(Paths.LegacySecretsFile))
                {
                    // 1.5–1.7: plain secrets.json -> encrypt, keep the old file renamed
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, ChannelSecret>>(File.ReadAllText(Paths.LegacySecretsFile), Opts) ?? new();
                    SaveLocked(legacy);
                    File.Move(Paths.LegacySecretsFile, Paths.LegacySecretsFile + ".pre-encryption", true);
                    return legacy;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Secrets load", ex);
            }
            return new();
        }
    }

    public static byte[]? ReadPlain()
    {
        lock (Gate)
        {
            try { return File.Exists(Paths.SecretsFile) ? Aead.Decrypt(Key, File.ReadAllBytes(Paths.SecretsFile), "secrets") : null; }
            catch (Exception ex) { CrashLog.Write("Secrets read", ex); return null; }
        }
    }

    public static void WritePlain(byte[] plain)
    {
        lock (Gate)
        {
            var tmp = Paths.SecretsFile + ".tmp";
            File.WriteAllBytes(tmp, Aead.Encrypt(Key, plain, "secrets"));
            File.Move(tmp, Paths.SecretsFile, true);
        }
    }

    public static void Save(Dictionary<string, ChannelSecret> secrets)
    {
        lock (Gate) SaveLocked(secrets);
    }

    static void SaveLocked(Dictionary<string, ChannelSecret> secrets)
    {
        var tmp = Paths.SecretsFile + ".tmp";
        File.WriteAllBytes(tmp, Aead.Encrypt(Key, JsonSerializer.SerializeToUtf8Bytes(secrets, Opts), "secrets"));
        File.Move(tmp, Paths.SecretsFile, true);
    }
}

public sealed record WebhookPreset(string Name, string Url, string Method, string ContentType, string Headers, string Body, bool IgnoreCert, string Hint);

public static class WebhookPresets
{
    const string FullJson =
        "{\n  \"app\": \"FreeMon\",\n  \"host\": \"{host}\",\n  \"time\": \"{time_iso}\",\n  \"count\": {count},\n  \"device\": \"{device}\",\n  \"address\": \"{address}\",\n  \"type\": \"{type}\",\n  \"parent\": \"{parent}\",\n  \"check\": \"{check}\",\n  \"from\": \"{from}\",\n  \"to\": \"{to}\",\n  \"status\": \"{status_code}\",\n  \"detail\": \"{detail}\",\n  \"text\": \"{text}\"\n}";

    public static readonly WebhookPreset[] All =
    {
        new(L.T("Telegram (bot)"),
            L.T("https://api.telegram.org/bot<BOT_TOKEN>/sendMessage"), "POST", "application/json", "",
            L.T("{\"chat_id\": \"<CHAT_ID>\", \"text\": \"{text}\"}"), false,
            L.T("The token is given by @BotFather; chat ID — of the group or user the bot was added to. This computer needs access to api.telegram.org.")),
        new("Virola",
            L.T("https://<SERVER>:<PORT>/api/v1/rooms/<ROOM_ID>/messages"), "POST", "application/json",
            "Authorization: Bearer <WEB_API_TOKEN>",
            "{\"content\": \"{text}\"}", true,
            L.T("Token: Virola Client → Tools → Web API Access Token (messages are sent on behalf of this user; a new token revokes the previous one). Room ID — from the Rooms API.")),
        new(L.T("Node-RED / generic JSON"),
            "http://<NODE-RED>:1880/freemon", "POST", "application/json", "",
            FullJson, false,
            L.T("All event fields separately — for automations (Node-RED, n8n, custom scripts).")),
        new("ntfy",
            L.T("http://<NTFY_SERVER>/<TOPIC>"), "POST", "text/plain; charset=utf-8",
            "Title: FreeMon: {device}",
            "{text}", false,
            L.T("Message — the request body, title — in the Title header.")),
        new("Gotify",
            L.T("http://<GOTIFY_SERVER>/message?token=<APP_TOKEN>"), "POST", "application/json", "",
            "{\"title\": \"FreeMon: {device}\", \"message\": \"{text}\", \"priority\": 5}", false,
            L.T("Application token (App token) from the Gotify web interface.")),
        new("Mattermost / Slack / Rocket.Chat",
            L.T("https://<SERVER>/hooks/<KEY>"), "POST", "application/json", "",
            "{\"text\": \"{text}\"}", false,
            L.T("Incoming webhook of the channel.")),
        new(L.T("Custom"),
            "", "POST", "application/json", "",
            "{\"text\": \"{text}\"}", false,
            L.T("Any HTTP receiver.")),
    };
}

/// <summary>One notification = one or several status changes collected in a short window.</summary>
public static class AlertText
{
    public static string Emoji(DevStatus s) => s switch
    {
        DevStatus.Up => "✅",
        DevStatus.Degraded => "⚠️",
        DevStatus.Down => "🔴",
        _ => "⚪",
    };

    public static string Code(DevStatus s) => s switch
    {
        DevStatus.Up => "UP",
        DevStatus.Degraded => "DEGRADED",
        DevStatus.Down => "DOWN",
        DevStatus.Unreachable => "UNREACHABLE",
        DevStatus.Disabled => "DISABLED",
        _ => "UNKNOWN",
    };

    public static string Line(StatusChange c, DateTime local) =>
        $"{Emoji(c.To)} {local:dd.MM HH:mm:ss}  {c.Device.Name} ({c.Device.Address})" +
        (c.Check is null ? "" : $" · {c.Check}") +
        $": {StatusText.Ua(c.From)} → {StatusText.Ua(c.To)}" +
        (c.ParentDown ? L.T(" (parent unreachable)") : "") +
        (string.IsNullOrWhiteSpace(c.Detail) ? "" : $"; {c.Detail}");

    public static Dictionary<string, string> Vars(AppConfig cfg, IReadOnlyList<(StatusChange C, DateTime Local)> items)
    {
        var (first, t) = items[0];
        var d = first.Device;
        var parent = d.ParentId is null ? null : cfg.Devices.FirstOrDefault(x => x.Id == d.ParentId);
        return new Dictionary<string, string>
        {
            ["text"] = string.Join("\n", items.Select(i => Line(i.C, i.Local))),
            ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
            ["device"] = d.Name,
            ["address"] = d.Address,
            ["type"] = d.Type,
            ["parent"] = parent?.Name ?? "",
            ["check"] = first.Check ?? "",
            ["from"] = StatusText.Ua(first.From),
            ["to"] = StatusText.Ua(first.To),
            ["status_code"] = Code(first.To),
            ["detail"] = first.Detail,
            ["time"] = t.ToString("dd.MM.yyyy HH:mm:ss"),
            ["time_iso"] = new DateTimeOffset(t).ToString("o"),
            ["host"] = Environment.MachineName,
            ["app"] = "FreeMon",
        };
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> vars, Func<string, string> escape)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in vars) sb.Replace("{" + k + "}", k == "count" ? v : escape(v));
        return sb.ToString();
    }

    public static string JsonEscape(string s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return json.Substring(1, json.Length - 2);
    }

    public static string PlaceholdersHelp =>
        L.T("{text} — ready-made text (all changes, one line each), {count} — number of changes, {device}, {address}, {type}, {parent}, {check}, {from}, {to}, {status_code} (UP/DEGRADED/DOWN), {detail}, {time}, {time_iso}, {host}, {app}. For several changes the single fields are taken from the first one.");
}

public static class AlertSender
{
    public static async Task SendAsync(AppConfig cfg, AlertChannel ch, ChannelSecret secret,
        IReadOnlyList<(StatusChange C, DateTime Local)> items, CancellationToken ct)
    {
        var vars = AlertText.Vars(cfg, items);
        if (ch.Kind == ChannelKind.Email) await SendEmailAsync(ch, secret, vars, items, ct).ConfigureAwait(false);
        else await SendWebhookAsync(ch, secret, vars, ct).ConfigureAwait(false);
    }

    static async Task SendWebhookAsync(AlertChannel ch, ChannelSecret secret, Dictionary<string, string> vars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret.Url)) throw new InvalidOperationException(L.T("no webhook address set"));
        var ctype = string.IsNullOrWhiteSpace(ch.ContentType) ? "application/json" : ch.ContentType.Trim();
        Func<string, string> esc = ctype.Contains("json", StringComparison.OrdinalIgnoreCase) ? AlertText.JsonEscape
            : ctype.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ? Uri.EscapeDataString
            : s => s;
        var url = AlertText.Render(secret.Url.Trim(), vars, Uri.EscapeDataString);

        // network policy: only the configured address, no proxy, no redirects, no certificate downloads
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        if (ch.IgnoreCertErrors) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        using var req = new HttpRequestMessage(ch.Method == "GET" ? HttpMethod.Get : HttpMethod.Post, url);
        foreach (var line in (secret.Headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var name = line[..i].Trim();
            var value = AlertText.Render(line[(i + 1)..].Trim(), vars, s => s.Replace("\r", " ").Replace("\n", " "));
            req.Headers.TryAddWithoutValidation(name, value);
        }
        if (req.Method == HttpMethod.Post)
        {
            var body = AlertText.Render(ch.BodyTemplate ?? "", vars, esc);
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctype);
        }
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 300) text = text[..300] + "…";
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {text}");
        }
    }

    static async Task SendEmailAsync(AlertChannel ch, ChannelSecret secret, Dictionary<string, string> vars,
        IReadOnlyList<(StatusChange C, DateTime Local)> items, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ch.SmtpHost)) throw new InvalidOperationException(L.T("no SMTP server set"));
        var to = ch.MailTo.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (to.Length == 0) throw new InvalidOperationException(L.T("no recipients set"));
        if (string.IsNullOrWhiteSpace(ch.MailFrom)) throw new InvalidOperationException(L.T("no sender address set"));

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(ch.MailFrom.Trim()));
        foreach (var a in to) msg.To.Add(MailboxAddress.Parse(a));
        var first = items[0].C;
        msg.Subject = items.Count == 1
            ? $"FreeMon: {first.Device.Name}{(first.Check is null ? "" : " · " + first.Check)} — {StatusText.Ua(first.To)}"
            : L.F("FreeMon: {0} state changes", items.Count);
        msg.Body = new TextPart("plain")
        {
            Text = vars["text"] + $"\n\n—\nFreeMon, {vars["host"]}\n",
        };

        using var smtp = new SmtpClient
        {
            Timeout = 20000,
            CheckCertificateRevocation = false,
        };
        if (ch.IgnoreCertErrors) smtp.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var sec = ch.SmtpSecurity switch
        {
            SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await smtp.ConnectAsync(ch.SmtpHost.Trim(), ch.SmtpPort, sec, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ch.SmtpUser))
            await smtp.AuthenticateAsync(ch.SmtpUser.Trim(), secret.SmtpPassword ?? "", ct).ConfigureAwait(false);
        await smtp.SendAsync(msg, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}

/// <summary>Collects status changes (~3 s window) and sends them to every matching channel, with retries.</summary>
public sealed class AlertDispatcher
{
    readonly AppCore _core;
    readonly ConcurrentQueue<(StatusChange C, DateTime Local)> _queue = new();
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;

    public AlertDispatcher(AppCore core)
    {
        _core = core;
        core.Monitor.StatusChanged += c =>
        {
            if (!c.Initial) _queue.Enqueue((c, DateTime.Now));
        };
        _loop = Task.Run(LoopAsync);
    }

    static bool Wants(AlertChannel ch, StatusChange c)
    {
        if (!ch.Enabled || !c.Device.SendAlerts) return false;
        if (c.Check is not null && !ch.OnChecks) return false;
        if (c.To == DevStatus.Down && c.ParentDown && ch.SkipUnreachableChildren) return false;
        return c.To switch
        {
            DevStatus.Down => ch.OnDown,
            DevStatus.Degraded => ch.OnDegraded,
            DevStatus.Up => ch.OnRecovery && c.From is DevStatus.Down or DevStatus.Degraded,
            _ => false,
        };
    }

    async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (_queue.IsEmpty) continue;
            var batch = new List<(StatusChange C, DateTime Local)>();
            while (_queue.TryDequeue(out var it)) batch.Add(it);

            var cfg = _core.Config;
            if (cfg.Channels.Count == 0 || !cfg.Notify.ChannelsEnabled) continue;
            var secrets = SecretStore.Load();
            foreach (var ch in cfg.Channels)
            {
                var items = batch.Where(i => Wants(ch, i.C)).ToList();
                if (items.Count == 0) continue;
                var secret = secrets.TryGetValue(ch.Id, out var s) ? s : new ChannelSecret();
                _ = Task.Run(() => SendWithRetryAsync(cfg, ch, secret, items, ct));
            }
        }
    }

    async Task SendWithRetryAsync(AppConfig cfg, AlertChannel ch, ChannelSecret secret,
        List<(StatusChange C, DateTime Local)> items, CancellationToken ct)
    {
        int[] delays = { 0, 10, 60, 300 };
        Exception? last = null;
        foreach (var d in delays)
        {
            try
            {
                if (d > 0) await Task.Delay(TimeSpan.FromSeconds(d), ct).ConfigureAwait(false);
                await AlertSender.SendAsync(cfg, ch, secret, items, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        _core.Monitor.SystemEvent(L.F("Could not send to channel \"{0}\" ({1} changes): {2}", ch.Name, items.Count, last?.Message));
    }

    public void Stop() => _cts.Cancel();

    /// <summary>Test message for the channel editor (uses the values currently entered, not the saved ones).</summary>
    public static Task TestAsync(AppConfig cfg, AlertChannel ch, ChannelSecret secret)
    {
        var dev = new Device { Name = L.T("Test device"), Address = "192.0.2.1", Type = "Інше" };
        var c = new StatusChange(dev, DevStatus.Up, DevStatus.Down, L.T("FreeMon test message — the channel is configured correctly"), false, false);
        return AlertSender.SendAsync(cfg, ch, secret, new List<(StatusChange, DateTime)> { (c, DateTime.Now) }, CancellationToken.None);
    }
}
