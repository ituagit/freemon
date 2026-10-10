using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

/// <summary>
/// Opens a device connection outside FreeMon: web links in the default browser, SSH in a terminal
/// (or via the system ssh:// handler, or a custom command). FreeMon itself does not connect.
/// </summary>
public static class Connector
{
    public static async Task OpenAsync(TopLevel top, AppConfig cfg, Device d, DeviceLink link)
    {
        var ui = UiStateStore.Current;
        var sshMode = ui.SshMode ?? cfg.SshMode;
        var sshCommand = ui.SshCommand ?? cfg.SshCommand;
        var url = link.Url(d.Address);

        if (link.Kind != LinkKind.Ssh && ui.WebMode == 1 && !string.IsNullOrWhiteSpace(ui.WebCommand))
        {
            var cmd = ui.WebCommand.Contains("{url}") ? ui.WebCommand.Replace("{url}", url) : ui.WebCommand + " " + url;
            var parts = SplitArgs(cmd);
            Start(parts[0], parts.Skip(1));
            return;
        }
        if (link.Kind != LinkKind.Ssh || sshMode == SshMode.UriHandler)
        {
            var ok = await top.Launcher.LaunchUriAsync(new Uri(url));
            if (!ok) throw new InvalidOperationException(L.T("the system could not open the link: ") + url);
            return;
        }
        var host = d.Address.Trim();
        var user = link.User.Trim();
        var target = (user.Length > 0 ? user + "@" : "") + host;
        var port = link.Port.ToString(CultureInfo.InvariantCulture);
        if (sshMode == SshMode.Custom)
        {
            var cmd = sshCommand.Replace("{host}", host).Replace("{port}", port).Replace("{user}", user).Replace("{target}", target);
            var parts = SplitArgs(cmd);
            if (parts.Count == 0) throw new InvalidOperationException(L.T("no SSH command set in the settings"));
            Start(parts[0], parts.Skip(1));
            return;
        }
        var ssh = new[] { "ssh", "-p", port, target };
        if (OperatingSystem.IsWindows())
        {
            // OpenSSH client of Windows (optional feature, usually C:\Windows\System32\OpenSSH\ssh.exe)
            var inbox = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            var sshExe = File.Exists(inbox) ? inbox : FindInPath("ssh.exe");
            if (sshExe is null)
                throw new InvalidOperationException(
                    L.T("SSH client (ssh.exe) not found. Install the Windows feature \"OpenSSH Client\" (Settings → System → Optional features) or choose \"Custom command\" in the FreeMon settings, for example for PuTTY: putty.exe -ssh {target} -P {port}"));
            Start("cmd.exe", new[] { "/c", "start", "FreeMon SSH " + host, sshExe, "-p", port, target });
            return;
        }
        (string Exe, string[] Pre)[] terminals =
        {
            ("x-terminal-emulator", new[] { "-e" }),
            ("konsole", new[] { "-e" }),
            ("gnome-terminal", new[] { "--" }),
            ("kgx", new[] { "--" }),
            ("xfce4-terminal", new[] { "-x" }),
            ("mate-terminal", new[] { "-x" }),
            ("tilix", new[] { "-e" }),
            ("alacritty", new[] { "-e" }),
            ("kitty", Array.Empty<string>()),
            ("xterm", new[] { "-e" }),
        };
        foreach (var (exe, pre) in terminals)
        {
            if (FindInPath(exe) is null) continue;
            Start(exe, pre.Concat(ssh));
            return;
        }
        throw new InvalidOperationException(L.T("no terminal found; choose \"Custom command\" in the SSH settings"));
    }

    /// <summary>Browsers found on this computer: (name, command template with {url}).</summary>
    public static List<(string Name, string Command)> FindBrowsers()
    {
        var res = new List<(string, string)>();
        if (OperatingSystem.IsWindows())
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            (string Name, string[] Paths)[] known =
            {
                ("Firefox", new[] { System.IO.Path.Combine(pf, "Mozilla Firefox", "firefox.exe"), System.IO.Path.Combine(pf86, "Mozilla Firefox", "firefox.exe") }),
                ("Google Chrome", new[] { System.IO.Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"), System.IO.Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"), System.IO.Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") }),
                ("Microsoft Edge", new[] { System.IO.Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"), System.IO.Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe") }),
                ("Brave", new[] { System.IO.Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"), System.IO.Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe") }),
                ("Vivaldi", new[] { System.IO.Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"), System.IO.Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe") }),
                ("Opera", new[] { System.IO.Path.Combine(local, "Programs", "Opera", "opera.exe") }),
            };
            foreach (var (name, paths) in known)
            {
                var p = paths.FirstOrDefault(File.Exists);
                if (p is not null) res.Add((name, $"\"{p}\" {{url}}"));
            }
            return res;
        }
        (string Name, string Exe)[] linux =
        {
            ("Firefox", "firefox"), ("Firefox ESR", "firefox-esr"), ("Chromium", "chromium"), ("Chromium", "chromium-browser"),
            ("Google Chrome", "google-chrome-stable"), ("Google Chrome", "google-chrome"), ("Microsoft Edge", "microsoft-edge-stable"),
            ("Microsoft Edge", "microsoft-edge"), ("Brave", "brave-browser"), ("Vivaldi", "vivaldi"), ("Opera", "opera"),
            ("Falkon", "falkon"), ("Konqueror", "konqueror"), ("GNOME Web", "epiphany"),
        };
        foreach (var (name, exe) in linux)
            if (FindInPath(exe) is not null && res.All(r => r.Item1 != name)) res.Add((name, exe + " {url}"));
        return res;
    }

    static void Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = System.IO.Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Splits a command line on spaces, keeping "quoted parts" together.</summary>
    static List<string> SplitArgs(string s)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        foreach (var c in s)
        {
            if (c == '"') { q = !q; continue; }
            if (char.IsWhiteSpace(c) && !q)
            {
                if (cur.Length > 0) { res.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }
}

public sealed class LinkDialog : Window
{
    readonly DeviceLink _l;
    readonly string _address;
    readonly ComboBox _kind = new() { Width = 200 };
    readonly TextBox _name = new() { Width = 300, PlaceholderText = L.T("optional, for example \"Settings\"") };
    readonly TextBox _port = new() { Width = 100 };
    readonly TextBox _user = new() { Width = 200, PlaceholderText = L.T("optional") };
    readonly TextBox _path = new() { Width = 300, PlaceholderText = L.T("optional, for example settings") };
    readonly StackPanel _userRow;
    readonly StackPanel _pathRow;
    readonly TextBlock _preview = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    LinkKind _prevKind;

    public LinkDialog(DeviceLink l, string address)
    {
        _l = l;
        _address = string.IsNullOrWhiteSpace(address) ? L.T("address") : address.Trim();
        Title = L.T("Connection to the device");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        _kind.ItemsSource = new[] { "SSH", L.T("Web — HTTP"), L.T("Web — HTTPS") };
        _kind.SelectedIndex = (int)l.Kind;
        _prevKind = l.Kind;
        _name.Text = l.Name;
        _port.Text = l.Port.ToString(CultureInfo.InvariantCulture);
        _user.Text = l.User;
        _path.Text = l.Path;
        _userRow = Ui.Row(L.T("SSH user"), _user, 180);
        _pathRow = Ui.Row(L.T("Path after the address"), _path, 180);

        var p = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        p.Children.Add(_err);
        p.Children.Add(Ui.Row(L.T("Type"), _kind, 180));
        p.Children.Add(Ui.Row(L.T("Menu name"), _name, 180));
        p.Children.Add(Ui.Row(L.T("Port"), _port, 180));
        p.Children.Add(_userRow);
        p.Children.Add(_pathRow);
        p.Children.Add(_preview);
        p.Children.Add(Ui.ButtonRow(Ui.Btn(L.T("Save"), (_, _) => Save(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false))));
        Content = p;

        _kind.SelectionChanged += (_, _) =>
        {
            var k = Kind;
            // keep a port the user typed; replace only the previous type's default
            if ((_port.Text ?? "").Trim() == DeviceLink.DefaultPort(_prevKind).ToString(CultureInfo.InvariantCulture))
                _port.Text = DeviceLink.DefaultPort(k).ToString(CultureInfo.InvariantCulture);
            _prevKind = k;
            UpdateUi();
        };
        _port.TextChanged += (_, _) => UpdateUi();
        _user.TextChanged += (_, _) => UpdateUi();
        _path.TextChanged += (_, _) => UpdateUi();
        UpdateUi();
    }

    LinkKind Kind => _kind.SelectedIndex is >= 0 and <= 2 ? (LinkKind)_kind.SelectedIndex : LinkKind.Https;

    void UpdateUi()
    {
        _userRow.IsVisible = Kind == LinkKind.Ssh;
        _pathRow.IsVisible = Kind != LinkKind.Ssh;
        var tmp = new DeviceLink
        {
            Kind = Kind,
            Port = int.TryParse((_port.Text ?? "").Trim(), out var pt) && pt is >= 1 and <= 65535 ? pt : DeviceLink.DefaultPort(Kind),
            User = _user.Text ?? "",
            Path = _path.Text ?? "",
        };
        _preview.Text = L.T("Will open: ") + (Kind == LinkKind.Ssh ? "ssh -p " + tmp.Port + " " + (tmp.User.Trim().Length > 0 ? tmp.User.Trim() + "@" : "") + _address : tmp.Url(_address));
    }

    void Save()
    {
        if (!int.TryParse((_port.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535)
        {
            _err.Text = L.T("Port: 1..65535");
            return;
        }
        var path = (_path.Text ?? "").Trim();
        if (path.Contains(' ') || path.Contains("://")) { _err.Text = L.T("Path — only the part after the address, without spaces and without http://"); return; }
        _l.Kind = Kind;
        _l.Name = (_name.Text ?? "").Trim();
        _l.Port = port;
        _l.User = Kind == LinkKind.Ssh ? (_user.Text ?? "").Trim() : "";
        _l.Path = Kind == LinkKind.Ssh ? "" : path;
        Close(true);
    }
}
