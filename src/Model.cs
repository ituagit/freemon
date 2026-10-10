using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace FreeMon;

public enum CloseAction { Auto = 0, Tray = 1, Minimize = 2 }

public enum UiTheme { Auto = 0, Light = 1, Dark = 2 }

/// <summary>Where the family map starts when it is smaller than the window.</summary>
public enum MapOrigin { TopLeft = 0, TopCenter = 1, Center = 2 }

public enum DevStatus { Unknown = 0, Up = 1, Degraded = 2, Down = 3, Unreachable = 4, Disabled = 5 }

public static class StatusText
{
    public static string Ua(DevStatus s) => s switch
    {
        DevStatus.Up => L.T("Up"),
        DevStatus.Degraded => L.T("Unstable"),
        DevStatus.Down => L.T("Down"),
        DevStatus.Unreachable => L.T("Unreachable via parent"),
        DevStatus.Disabled => L.T("Disabled"),
        _ => L.T("Unknown"),
    };

    public static int Severity(DevStatus s) => s switch
    {
        DevStatus.Down => 5,
        DevStatus.Unreachable => 4,
        DevStatus.Degraded => 3,
        DevStatus.Unknown => 2,
        DevStatus.Up => 1,
        _ => 0,
    };

    /// <summary>Status name in the report language.</summary>
    public static string Rep(DevStatus s) => s switch
    {
        DevStatus.Up => L.R("Up"),
        DevStatus.Degraded => L.R("Unstable"),
        DevStatus.Down => L.R("Down"),
        DevStatus.Unreachable => L.R("Unreachable via parent"),
        DevStatus.Disabled => L.R("Disabled"),
        _ => L.R("Unknown"),
    };
}

/// <summary>Ping parameters. null = inherit from the level above (device -> type -> global).</summary>
public sealed class PingProfile
{
    public int? IntervalSec { get; set; }
    public int? PacketCount { get; set; }
    public int? PacketIntervalMs { get; set; }
    public int? TimeoutMs { get; set; }
    public int? PacketSize { get; set; }
    public int? DownLossPct { get; set; }
    public int? DegLossPct { get; set; }
    public int? DegAvgMs { get; set; }
    public int? DegMaxMs { get; set; }
    public int? DegJitterMs { get; set; }
    public int? ConfirmCycles { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static PingProfile Defaults() => new()
    {
        IntervalSec = 10,
        PacketCount = 5,
        PacketIntervalMs = 200,
        TimeoutMs = 1000,
        PacketSize = 32,
        DownLossPct = 100,
        DegLossPct = 20,
        DegAvgMs = 100,
        DegMaxMs = 500,
        DegJitterMs = 100,
        ConfirmCycles = 2,
    };
}

public sealed record ProfileField(string Key, string Label, string Unit, int Min, int Max,
    Func<PingProfile, int?> Get, Action<PingProfile, int?> Set);

public static class ProfileFields
{
    public static readonly ProfileField Interval = new("interval_s", L.T("Check interval"), L.T("s"), 1, 86400, p => p.IntervalSec, (p, v) => p.IntervalSec = v);
    public static readonly ProfileField PacketCount = new("packet_count", L.T("Packets per check"), L.T("pcs"), 1, 100, p => p.PacketCount, (p, v) => p.PacketCount = v);
    public static readonly ProfileField PacketInterval = new("packet_interval_ms", L.T("Pause between packets"), L.T("ms"), 0, 10000, p => p.PacketIntervalMs, (p, v) => p.PacketIntervalMs = v);
    public static readonly ProfileField Timeout = new("timeout_ms", L.T("Response timeout"), L.T("ms"), 50, 10000, p => p.TimeoutMs, (p, v) => p.TimeoutMs = v);
    public static readonly ProfileField PacketSize = new("packet_size", L.T("Packet data size"), L.T("bytes"), 0, 65000, p => p.PacketSize, (p, v) => p.PacketSize = v);
    public static readonly ProfileField DownLoss = new("down_loss_pct", L.T("\"Down\" when loss is at least"), "%", 1, 100, p => p.DownLossPct, (p, v) => p.DownLossPct = v);
    public static readonly ProfileField DegLoss = new("deg_loss_pct", L.T("\"Unstable\" when loss is at least (0 = off)"), "%", 0, 100, p => p.DegLossPct, (p, v) => p.DegLossPct = v);
    public static readonly ProfileField DegAvg = new("deg_avg_ms", L.T("\"Unstable\" when average latency exceeds (0 = off)"), L.T("ms"), 0, 60000, p => p.DegAvgMs, (p, v) => p.DegAvgMs = v);
    public static readonly ProfileField DegMax = new("deg_max_ms", L.T("\"Unstable\" when max latency exceeds (0 = off)"), L.T("ms"), 0, 60000, p => p.DegMaxMs, (p, v) => p.DegMaxMs = v);
    public static readonly ProfileField DegJitter = new("deg_jitter_ms", L.T("\"Unstable\" when latency jitter exceeds (0 = off)"), L.T("ms"), 0, 60000, p => p.DegJitterMs, (p, v) => p.DegJitterMs = v);
    public static readonly ProfileField Confirm = new("confirm_cycles", L.T("Consecutive checks to change the state"), "", 1, 50, p => p.ConfirmCycles, (p, v) => p.ConfirmCycles = v);

    public static readonly ProfileField[] All =
    {
        Interval, PacketCount, PacketInterval, Timeout, PacketSize,
        DownLoss, DegLoss, DegAvg, DegMax, DegJitter, Confirm,
    };
}

public sealed record EffProfile(int IntervalSec, int PacketCount, int PacketIntervalMs, int TimeoutMs, int PacketSize,
    int DownLossPct, int DegLossPct, int DegAvgMs, int DegMaxMs, int DegJitterMs, int ConfirmCycles);

public static class ProfileResolver
{
    static readonly PingProfile Fallback = PingProfile.Defaults();

    public static PingProfile? TypeProfile(AppConfig cfg, string? typeName) =>
        string.IsNullOrWhiteSpace(typeName)
            ? null
            : cfg.Types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))?.Profile;

    public static int Value(ProfileField f, PingProfile global, PingProfile? type, PingProfile? device)
    {
        int v = (device is null ? null : f.Get(device))
                ?? (type is null ? null : f.Get(type))
                ?? f.Get(global)
                ?? f.Get(Fallback)
                ?? f.Min;
        return Math.Clamp(v, f.Min, f.Max);
    }

    public static EffProfile Effective(AppConfig cfg, Device d)
    {
        var t = TypeProfile(cfg, d.Type);
        int V(ProfileField f) => Value(f, cfg.Global, t, d.Profile);
        return new EffProfile(
            V(ProfileFields.Interval), V(ProfileFields.PacketCount), V(ProfileFields.PacketInterval),
            V(ProfileFields.Timeout), V(ProfileFields.PacketSize), V(ProfileFields.DownLoss),
            V(ProfileFields.DegLoss), V(ProfileFields.DegAvg), V(ProfileFields.DegMax),
            V(ProfileFields.DegJitter), V(ProfileFields.Confirm));
    }
}

public enum CheckKind { Tcp = 0, Http = 1, Dns = 2, Json = 3 }

/// <summary>Comparison of a value taken from a JSON response.</summary>
public enum JsonOp { None = 0, Exists = 1, Equals = 2, NotEquals = 3, Contains = 4, Less = 5, LessOrEqual = 6, Greater = 7, GreaterOrEqual = 8, Between = 9, Regex = 10 }

public enum ChannelKind { Email = 0, Webhook = 1 }

public enum LinkKind { Ssh = 0, Http = 1, Https = 2 }

public enum SshMode { Terminal = 0, UriHandler = 1, Custom = 2 }

/// <summary>Quick connection to a device from its context menu (opened in the user's browser / SSH client).</summary>
public sealed class DeviceLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LinkKind Kind { get; set; } = LinkKind.Https;
    public string Name { get; set; } = "";
    public int Port { get; set; } = 443;
    /// <summary>HTTP(S): path after the address, e.g. "settings" or "/cgi-bin/luci".</summary>
    public string Path { get; set; } = "";
    /// <summary>SSH: user name (optional).</summary>
    public string User { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static int DefaultPort(LinkKind k) => k switch { LinkKind.Ssh => 22, LinkKind.Http => 80, _ => 443 };

    static string HostPart(string address)
    {
        var a = address.Trim();
        return a.Contains(':') && !a.StartsWith('[') ? $"[{a}]" : a;
    }

    public string Target(string address) => Kind switch
    {
        LinkKind.Ssh => (string.IsNullOrWhiteSpace(User) ? "" : User.Trim() + "@") + address.Trim() + (Port == 22 ? "" : $":{Port}"),
        _ => Url(address),
    };

    public string Url(string address)
    {
        var scheme = Kind == LinkKind.Http ? "http" : Kind == LinkKind.Https ? "https" : "ssh";
        bool defPort = Port == DefaultPort(Kind);
        var path = Kind == LinkKind.Ssh ? "" : "/" + (Path ?? "").Trim().TrimStart('/');
        var user = Kind == LinkKind.Ssh && !string.IsNullOrWhiteSpace(User) ? Uri.EscapeDataString(User.Trim()) + "@" : "";
        return $"{scheme}://{user}{HostPart(address)}{(defPort ? "" : ":" + Port)}{(path == "/" ? "/" : path)}";
    }

    public string Title(string address)
    {
        var what = Kind == LinkKind.Ssh ? "SSH" : L.T("Web");
        var name = string.IsNullOrWhiteSpace(Name) ? "" : Name.Trim() + " — ";
        return $"{what}: {name}{Target(address)}";
    }
}

public enum SmtpSecurity { StartTls = 0, Tls = 1, None = 2 }

/// <summary>
/// Outgoing notification channel (email / webhook). Secrets (webhook URL, headers, SMTP password)
/// are NOT stored here but in secrets.json (see SecretStore).
/// </summary>
public sealed class AlertChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ChannelKind Kind { get; set; } = ChannelKind.Webhook;

    public bool OnDown { get; set; } = true;
    public bool OnDegraded { get; set; } = true;
    public bool OnRecovery { get; set; } = true;
    public bool OnChecks { get; set; } = true;
    public bool SkipUnreachableChildren { get; set; } = true;

    // email
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;
    public string SmtpUser { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public string MailTo { get; set; } = "";

    // webhook (URL and headers are secrets)
    public string Preset { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string ContentType { get; set; } = "application/json";
    public string BodyTemplate { get; set; } = "{\"text\": \"{text}\"}";

    public bool IgnoreCertErrors { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Additional service check of a device (like RouterOS Netwatch tcp-conn / http-get / dns).</summary>
public sealed class ExtraCheck
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CheckKind Kind { get; set; }
    /// <summary>Optional display name; empty = generated from parameters.</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int IntervalSec { get; set; } = 60;
    public int TimeoutMs { get; set; } = 3000;
    /// <summary>&gt; 0: slower answer = "Нестабільно".</summary>
    public int SlowMs { get; set; }
    public int ConfirmCycles { get; set; } = 2;

    // TCP
    public int Port { get; set; } = 443;

    // HTTP(S)
    /// <summary>Empty = http://address/; "/path" = http://address/path; or a full http(s):// URL.</summary>
    public string Url { get; set; } = "";
    public string ExpectCodes { get; set; } = "200-399";
    public string ExpectText { get; set; } = "";
    public bool IgnoreCertErrors { get; set; }

    // HTTP JSON (e.g. a Home Assistant sensor: GET /api/states/sensor.x, value "state")
    /// <summary>Request headers, one per line "Name: value" (e.g. Authorization: Bearer …).</summary>
    public string Headers { get; set; } = "";
    /// <summary>Path in the JSON: state, attributes.temperature, data[0].value ("$." is optional).</summary>
    public string JsonPath { get; set; } = "";
    /// <summary>"Up" when this condition holds.</summary>
    public JsonOp OkOp { get; set; } = JsonOp.Exists;
    public string OkValue { get; set; } = "";
    public string OkValue2 { get; set; } = "";
    /// <summary>Otherwise "Unstable" when this condition holds (None = straight to "Down").</summary>
    public JsonOp WarnOp { get; set; } = JsonOp.None;
    public string WarnValue { get; set; } = "";
    public string WarnValue2 { get; set; } = "";

    // DNS
    public string DnsName { get; set; } = "";
    public string DnsType { get; set; } = "A";
    public string DnsExpect { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Title() => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : Kind switch
    {
        CheckKind.Tcp => $"TCP {Port}",
        CheckKind.Http => "HTTP " + (string.IsNullOrWhiteSpace(Url) ? "/" : Url.Trim()),
        CheckKind.Json => "JSON " + (string.IsNullOrWhiteSpace(JsonPath) ? "$" : JsonPath.Trim()),
        _ => $"DNS {DnsName} ({DnsType})",
    };
}

public sealed class MapPoint
{
    /// <summary>Offset from the family center on the map, in device-independent pixels.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Type { get; set; } = "";
    public string? ParentId { get; set; }
    public string Group { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public PingProfile Profile { get; set; } = new();
    /// <summary>Position as a child inside the parent's family card.</summary>
    public MapPoint? MapPos { get; set; }
    /// <summary>Position as the center of its own family card.</summary>
    public MapPoint? MapCenterPos { get; set; }
    /// <summary>Position of its family card on the map (null = automatic layout).</summary>
    public MapPoint? MapCardPos { get; set; }
    public List<ExtraCheck> Checks { get; set; } = new();
    public List<DeviceLink> Links { get; set; } = new();
    /// <summary>Send this device's events to email/webhook channels.</summary>
    public bool SendAlerts { get; set; } = true;
    /// <summary>Play notification sounds for this device's events.</summary>
    public bool PlaySounds { get; set; } = true;
    /// <summary>Main check that defines the device state and color. null = ICMP ping.</summary>
    public ExtraCheck? Primary { get; set; }

    /// <summary>Fields written by other (newer) versions are kept as-is when saving.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class DeviceType
{
    public string Name { get; set; } = "";
    /// <summary>MdiIcons key; empty = default for the type.</summary>
    public string Icon { get; set; } = "";
    public PingProfile Profile { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BackupSettings
{
    public bool AutoEnabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public string Folder { get; set; } = "";
    public int Keep { get; set; } = 14;
    public bool IncludeLog { get; set; } = true;
    /// <summary>Public key for automatic backups (SubjectPublicKeyInfo, base64).</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Private key encrypted with the backup password (encrypted PKCS#8, base64).</summary>
    public string EncryptedPrivateKey { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ReportSettings
{
    /// <summary>Language of PDF reports and raw logs: "" = interface language of the user, or "en" / "uk".</summary>
    public string Language { get; set; } = "";
    public string Organization { get; set; } = "";
    public string PreparedBy { get; set; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NotifySettings
{
    public bool OnDown { get; set; } = true;
    public bool OnDegraded { get; set; } = true;
    public bool OnRecovery { get; set; } = true;
    public bool SkipUnreachableChildren { get; set; } = true;
    public bool Sound { get; set; }
    /// <summary>Quiet chime when the connection is restored.</summary>
    public bool SoundRecovery { get; set; } = true;
    public int PopupSeconds { get; set; } = 15;
    /// <summary>Global switch for email / webhook channels.</summary>
    public bool ChannelsEnabled { get; set; } = true;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AppConfig
{
    public const string OtherType = "Інше";

    /// <summary>Built-in type names are stored identifiers (Ukrainian, kept for compatibility); these are their English display names.</summary>
    static readonly Dictionary<string, string> BuiltInTypeEn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Роутер"] = "Router", ["Світч"] = "Switch", ["Точка доступу"] = "Access point", ["Сервер"] = "Server",
        ["Камера"] = "Camera", ["ПК"] = "PC", ["Принтер"] = "Printer", ["Телефон"] = "Phone", ["Інше"] = "Other",
    };

    /// <summary>Display name of a type in the interface language.</summary>
    public static string TypeLabel(string name) => BuiltInTypeEn.TryGetValue(name ?? "", out var en) ? L.T(en) : name ?? "";

    /// <summary>Display name of a type in the report language.</summary>
    public static string TypeLabelReport(string name) => BuiltInTypeEn.TryGetValue(name ?? "", out var en) ? L.R(en) : name ?? "";

    public static readonly string[] BuiltInTypes =
        { "Роутер", "Світч", "Точка доступу", "Сервер", "Камера", "ПК", "Принтер", "Телефон", "Інше" };

    /// <summary>Config schema version. Newer programs migrate older files; older programs keep unknown fields.</summary>
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;
    public int RetentionDays { get; set; } = 30;
    public bool SuppressChildrenWhenParentDown { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 10;
    public CloseAction CloseAction { get; set; } = CloseAction.Auto;
    public UiTheme Theme { get; set; } = UiTheme.Auto;
    /// <summary>Start in view mode even without password (switch to administrator mode by a button).</summary>
    public bool ViewMode { get; set; }
    /// <summary>Show the last event in the status bar when the "Події" panel is collapsed.</summary>
    public bool ShowLastEventInStatusBar { get; set; } = true;
    /// <summary>"Історія та звіти" is available in view mode.</summary>
    public bool AllowReportsInViewMode { get; set; } = true;
    /// <summary>Connections (SSH / web) from the device context menu are available in view mode.</summary>
    public bool AllowLinksInViewMode { get; set; } = true;
    /// <summary>Legacy (1.7.0): now per user in ui.json; kept as a fallback.</summary>
    public SshMode SshMode { get; set; } = SshMode.Terminal;
    /// <summary>Legacy (1.7.0) custom SSH command: {host} {port} {user} {target}.</summary>
    public string SshCommand { get; set; } = "";
    /// <summary>Position of the "Окремі пристрої" card on the map (null = automatic layout).</summary>
    public MapPoint? MapLoosePos { get; set; }
    public MapOrigin MapOrigin { get; set; } = MapOrigin.TopLeft;
    public List<AlertChannel> Channels { get; set; } = new();
    public SecurityInfo? Security { get; set; }
    public NotifySettings Notify { get; set; } = new();
    public ReportSettings Report { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public PingProfile Global { get; set; } = PingProfile.Defaults();
    public List<DeviceType> Types { get; set; } = DefaultTypes();
    public List<Device> Devices { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static List<DeviceType> DefaultTypes() => BuiltInTypes.Select(n => new DeviceType { Name = n }).ToList();

    public static bool IsBuiltInType(string? name) =>
        name is not null && BuiltInTypes.Any(b => string.Equals(b, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public static class AddressUtil
{
    static readonly Regex HostRx = new(
        @"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?$",
        RegexOptions.Compiled);

    public static bool IsValid(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.Contains(':'))
            return IPAddress.TryParse(s, out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6;
        if (s.All(c => char.IsDigit(c) || c == '.'))
            return IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3;
        return HostRx.IsMatch(s);
    }
}
