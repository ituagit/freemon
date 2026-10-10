using Avalonia.Media;

namespace FreeMon;

public sealed record MdiIcon(string Key, string Title, string Source, string PathData);

/// <summary>
/// Icons from Material Design Icons by Pictogrammers (https://pictogrammers.com), Apache License 2.0.
/// See THIRD_PARTY_NOTICES.txt.
/// </summary>
public static class MdiIcons
{
    public static readonly MdiIcon[] All =
    {
        new("router", "Роутер", "mdi-router-network",
            "M5 9C3.9 9 3 9.9 3 11V15C3 16.11 3.9 17 5 17H11V19H10C9.45 19 9 19.45 9 20H2V22H9C9 22.55 9.45 23 10 23H14C14.55 23 15 22.55 15 22H22V20H15C15 19.45 14.55 19 14 19H13V17H19C20.11 17 21 16.11 21 15V11C21 9.9 20.11 9 19 9H5M6 12H8V14H6V12M9.5 12H11.5V14H9.5V12M13 12H15V14H13V12Z"),
        new("switch", "Світч", "mdi-switch",
            "M13,18H14A1,1 0 0,1 15,19H22V21H15A1,1 0 0,1 14,22H10A1,1 0 0,1 9,21H2V19H9A1,1 0 0,1 10,18H11V16H8A1,1 0 0,1 7,15V3A1,1 0 0,1 8,2H16A1,1 0 0,1 17,3V15A1,1 0 0,1 16,16H13V18M13,6H14V4H13V6M9,4V6H11V4H9M9,8V10H11V8H9M9,12V14H11V12H9Z"),
        new("ap", "Точка доступу", "mdi-access-point-network",
            "M4.93,3.93C3.12,5.74 2,8.24 2,11C2,13.76 3.12,16.26 4.93,18.07L6.34,16.66C4.89,15.22 4,13.22 4,11C4,8.79 4.89,6.78 6.34,5.34L4.93,3.93M19.07,3.93L17.66,5.34C19.11,6.78 20,8.79 20,11C20,13.22 19.11,15.22 17.66,16.66L19.07,18.07C20.88,16.26 22,13.76 22,11C22,8.24 20.88,5.74 19.07,3.93M7.76,6.76C6.67,7.85 6,9.35 6,11C6,12.65 6.67,14.15 7.76,15.24L9.17,13.83C8.45,13.11 8,12.11 8,11C8,9.89 8.45,8.89 9.17,8.17L7.76,6.76M16.24,6.76L14.83,8.17C15.55,8.89 16,9.89 16,11C16,12.11 15.55,13.11 14.83,13.83L16.24,15.24C17.33,14.15 18,12.65 18,11C18,9.35 17.33,7.85 16.24,6.76M12,9A2,2 0 0,0 10,11A2,2 0 0,0 12,13A2,2 0 0,0 14,11A2,2 0 0,0 12,9M11,15V19H10A1,1 0 0,0 9,20H2V22H9A1,1 0 0,0 10,23H14A1,1 0 0,0 15,22H22V20H15A1,1 0 0,0 14,19H13V15H11Z"),
        new("server", "Сервер", "mdi-server",
            "M4,1H20A1,1 0 0,1 21,2V6A1,1 0 0,1 20,7H4A1,1 0 0,1 3,6V2A1,1 0 0,1 4,1M4,9H20A1,1 0 0,1 21,10V14A1,1 0 0,1 20,15H4A1,1 0 0,1 3,14V10A1,1 0 0,1 4,9M4,17H20A1,1 0 0,1 21,18V22A1,1 0 0,1 20,23H4A1,1 0 0,1 3,22V18A1,1 0 0,1 4,17M9,5H10V3H9V5M9,13H10V11H9V13M9,21H10V19H9V21M5,3V5H7V3H5M5,11V13H7V11H5M5,19V21H7V19H5Z"),
        new("camera", "Камера", "mdi-cctv",
            "M6.03 12.03L8.03 15.5L5.5 18.68L2 12.62L6.03 12.03M17 18V15.29C17.88 14.9 18.5 14.03 18.5 13C18.5 12.43 18.3 11.9 17.97 11.5L19.94 10.35C20.95 9.76 21.3 8.47 20.71 7.46L19.33 5.06C18.74 4.05 17.45 3.7 16.44 4.28L8.31 9C7.36 9.53 7.03 10.75 7.58 11.71L9.08 14.31C9.63 15.26 10.86 15.59 11.81 15.04L13.69 13.96C13.94 14.55 14.41 15.03 15 15.29V18C15 19.1 15.9 20 17 20H22V18H17Z"),
        new("pc", "ПК", "mdi-desktop-classic",
            "M6,2C4.89,2 4,2.89 4,4V12C4,13.11 4.89,14 6,14H18C19.11,14 20,13.11 20,12V4C20,2.89 19.11,2 18,2H6M6,4H18V12H6V4M4,15C2.89,15 2,15.89 2,17V20C2,21.11 2.89,22 4,22H20C21.11,22 22,21.11 22,20V17C22,15.89 21.11,15 20,15H4M8,17H20V20H8V17M9,17.75V19.25H13V17.75H9M15,17.75V19.25H19V17.75H15Z"),
        new("printer", "Принтер", "mdi-printer",
            "M18,3H6V7H18M19,12A1,1 0 0,1 18,11A1,1 0 0,1 19,10A1,1 0 0,1 20,11A1,1 0 0,1 19,12M16,19H8V14H16M19,8H5A3,3 0 0,0 2,11V17H6V21H18V17H22V11A3,3 0 0,0 19,8Z"),
        new("phone", "Телефон", "mdi-phone-voip",
            "M13,17V19H14A1,1 0 0,1 15,20H22V22H15A1,1 0 0,1 14,23H10A1,1 0 0,1 9,22H2V20H9A1,1 0 0,1 10,19H11V17H13M23.7,7.67C23.88,7.85 24,8.09 24,8.37C24,8.65 23.89,8.9 23.71,9.08L21.23,11.56C21.05,11.74 20.8,11.85 20.5,11.85C20.25,11.85 20,11.75 19.82,11.57C19,10.84 18.13,10.21 17.15,9.72C16.82,9.56 16.59,9.21 16.59,8.82V5.72C15.14,5.25 13.59,5 12,5C10.4,5 8.85,5.25 7.4,5.73V8.83C7.4,9.23 7.17,9.57 6.84,9.73C5.87,10.22 4.97,10.84 4.18,11.58C4,11.75 3.75,11.86 3.5,11.86C3.2,11.86 2.95,11.75 2.77,11.57L0.29,9.09C0.11,8.91 0,8.66 0,8.38C0,8.1 0.11,7.85 0.29,7.67C3.34,4.78 7.46,3 12,3C16.53,3 20.65,4.78 23.7,7.67M11,10V15H10V10H11M12,10H15V13H13V15H12V10M14,12V11H13V12H14Z"),
        new("other", "Інше", "mdi-help-network-outline",
            "M15,20A1,1 0 0,0 14,19H13V17H17A2,2 0 0,0 19,15V5A2,2 0 0,0 17,3H7A2,2 0 0,0 5,5V15A2,2 0 0,0 7,17H11V19H10A1,1 0 0,0 9,20H2V22H9A1,1 0 0,0 10,23H14A1,1 0 0,0 15,22H22V20H15M7,15V5H17V15H7M11.95,6C11.17,6 10.55,6.18 10.07,6.5C9.61,6.89 9.38,7.4 9.39,8.1L9.4,8.13H11.11C11.12,7.86 11.2,7.65 11.36,7.5C11.5,7.38 11.72,7.31 11.95,7.31C12.22,7.31 12.45,7.4 12.61,7.56C12.77,7.73 12.85,7.96 12.85,8.22C12.85,8.5 12.78,8.75 12.64,8.95C12.5,9.16 12.33,9.33 12.1,9.5C11.65,9.78 11.34,10.05 11.17,10.29C11,10.5 10.89,10.89 10.89,11.33H12.67C12.67,11.06 12.7,10.84 12.78,10.68C12.86,10.5 13,10.36 13.24,10.21C13.64,10 13.97,9.74 14.22,9.39C14.5,9.03 14.62,8.67 14.62,8.22C14.62,7.55 14.38,7 13.9,6.61C13.42,6.2 12.77,6 11.95,6M10.89,12.22V14H12.67V12.22H10.89Z"),
        new("lan", L.T("Network device"), "mdi-lan",
            "M10,2C8.89,2 8,2.89 8,4V7C8,8.11 8.89,9 10,9H11V11H2V13H6V15H5C3.89,15 3,15.89 3,17V20C3,21.11 3.89,22 5,22H9C10.11,22 11,21.11 11,20V17C11,15.89 10.11,15 9,15H8V13H16V15H15C13.89,15 13,15.89 13,17V20C13,21.11 13.89,22 15,22H19C20.11,22 21,21.11 21,20V17C21,15.89 20.11,15 19,15H18V13H22V11H13V9H14C15.11,9 16,8.11 16,7V4C16,2.89 15.11,2 14,2H10M10,4H14V7H10V4M5,17H9V20H5V17M15,17H19V20H15V17Z"),
        new("devices", L.T("Devices"), "mdi-devices",
            "M3 6H21V4H3C1.9 4 1 4.9 1 6V18C1 19.1 1.9 20 3 20H7V18H3V6M13 12H9V13.78C8.39 14.33 8 15.11 8 16C8 16.89 8.39 17.67 9 18.22V20H13V18.22C13.61 17.67 14 16.88 14 16S13.61 14.33 13 13.78V12M11 17.5C10.17 17.5 9.5 16.83 9.5 16S10.17 14.5 11 14.5 12.5 15.17 12.5 16 11.83 17.5 11 17.5M22 8H16C15.5 8 15 8.5 15 9V19C15 19.5 15.5 20 16 20H22C22.5 20 23 19.5 23 19V9C23 8.5 22.5 8 22 8M21 18H17V10H21V18Z"),
        new("wifi", "Wi-Fi", "mdi-wifi",
            "M12,21L15.6,16.2C14.6,15.45 13.35,15 12,15C10.65,15 9.4,15.45 8.4,16.2L12,21M12,3C7.95,3 4.21,4.34 1.2,6.6L3,9C5.5,7.12 8.62,6 12,6C15.38,6 18.5,7.12 21,9L22.8,6.6C19.79,4.34 16.05,3 12,3M12,9C9.3,9 6.81,9.89 4.8,11.4L6.6,13.8C8.1,12.67 9.97,12 12,12C14.03,12 15.9,12.67 17.4,13.8L19.2,11.4C17.19,9.89 14.7,9 12,9Z"),
        new("monitor", L.T("Monitor / display"), "mdi-monitor",
            "M21,16H3V4H21M21,2H3C1.89,2 1,2.89 1,4V16A2,2 0 0,0 3,18H10V20H8V22H16V20H14V18H21A2,2 0 0,0 23,16V4C23,2.89 22.1,2 21,2Z"),
        new("server-network", L.T("Network server"), "mdi-server-network",
            "M13,19H14A1,1 0 0,1 15,20H22V22H15A1,1 0 0,1 14,23H10A1,1 0 0,1 9,22H2V20H9A1,1 0 0,1 10,19H11V17H4A1,1 0 0,1 3,16V12A1,1 0 0,1 4,11H20A1,1 0 0,1 21,12V16A1,1 0 0,1 20,17H13V19M4,3H20A1,1 0 0,1 21,4V8A1,1 0 0,1 20,9H4A1,1 0 0,1 3,8V4A1,1 0 0,1 4,3M9,7H10V5H9V7M9,15H10V13H9V15M5,5V7H7V5H5M5,13V15H7V13H5Z"),
    };

    public const string FullPrefix = "mdi:";

    static readonly Dictionary<string, Geometry> Cache = new();
    static readonly object FullGate = new();
    static Dictionary<string, string>? _full;

    /// <summary>Complete Material Design Icons set (@mdi/js), key = kebab-case name. Loaded on first use.</summary>
    public static IReadOnlyDictionary<string, string> Full
    {
        get
        {
            lock (FullGate) return _full ??= LoadFull();
        }
    }

    static Dictionary<string, string> LoadFull()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var s = typeof(MdiIcons).Assembly.GetManifestResourceStream("mdi.js");
        if (s is null) return d;
        using var r = new StreamReader(s);
        const string head = "export var mdi";
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (!line.StartsWith(head, StringComparison.Ordinal)) continue;
            int eq = line.IndexOf(" = \"", StringComparison.Ordinal);
            int end = line.LastIndexOf('"');
            if (eq < 0 || end <= eq + 4) continue;
            d[Kebab(line.Substring(head.Length, eq - head.Length))] = line.Substring(eq + 4, end - eq - 4);
        }
        return d;
    }

    static string Kebab(string camel)
    {
        var sb = new System.Text.StringBuilder(camel.Length + 8);
        for (int i = 0; i < camel.Length; i++)
        {
            char c = camel[i];
            bool boundary = i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(camel[i - 1])));
            if (boundary) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static bool Exists(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (key.StartsWith(FullPrefix, StringComparison.Ordinal) ? Full.ContainsKey(key[FullPrefix.Length..]) : All.Any(i => i.Key == key));

    public static string Title(string key) =>
        key.StartsWith(FullPrefix, StringComparison.Ordinal) ? key[FullPrefix.Length..] : (All.FirstOrDefault(i => i.Key == key)?.Title is { } tt ? AppConfig.TypeLabel(tt) : key);

    /// <summary>Any icon of the full set by its name, e.g. Named("lock").</summary>
    public static Geometry Named(string name) => Get(FullPrefix + name);

    static readonly Dictionary<string, string> BuiltInTypeIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Роутер"] = "router",
        ["Світч"] = "switch",
        ["Точка доступу"] = "ap",
        ["Сервер"] = "server",
        ["Камера"] = "camera",
        ["ПК"] = "pc",
        ["Принтер"] = "printer",
        ["Телефон"] = "phone",
        ["Інше"] = "other",
    };

    public static string DefaultKeyForType(string? typeName) =>
        typeName is not null && BuiltInTypeIcon.TryGetValue(typeName.Trim(), out var k) ? k : "lan";

    public static string KeyForDevice(AppConfig cfg, Device d)
    {
        var t = cfg.Types.FirstOrDefault(x => string.Equals(x.Name, d.Type, StringComparison.OrdinalIgnoreCase));
        if (t is not null && Exists(t.Icon)) return t.Icon;
        return string.IsNullOrWhiteSpace(d.Type) ? "lan" : DefaultKeyForType(d.Type);
    }

    public static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var g)) return g;
        string data;
        if (key.StartsWith(FullPrefix, StringComparison.Ordinal) && Full.TryGetValue(key[FullPrefix.Length..], out var full))
            data = full;
        else
            data = (All.FirstOrDefault(i => i.Key == key) ?? All.First(i => i.Key == "lan")).PathData;
        g = Geometry.Parse(data);
        Cache[key] = g;
        return g;
    }
}
