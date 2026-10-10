using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeMon;

/// <summary>
/// Route diagnostics to a device (MTR-like) with the tools of the operating system:
/// Linux — mtr (report), otherwise tracepath / traceroute; Windows — pathping, otherwise tracert.
/// FreeMon only starts the tool and shows its output.
/// </summary>
public sealed class MtrWindow : Window
{
    readonly string _host;
    readonly TextBox _out = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Liberation Mono, monospace"),
        FontSize = 12,
    };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly Button _again = new() { Content = L.T("Run again") };
    readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 0, 0, 6), IsVisible = false };
    readonly TextBlock _running = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6), IsVisible = false };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _startedAt;
    Process? _proc;

    public MtrWindow(Device d)
    {
        _host = d.Address.Trim();
        Title = L.F("Route to {0} ({1})", d.Name, _host);
        Width = 860;
        Height = 560;
        MinWidth = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        var copy = new Button { Content = L.T("Copy") };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(_out.Text ?? "");
        };
        _again.Click += (_, _) => Start();
        var close = new Button { Content = L.T("Close") };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_again);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        _status.Margin = new Thickness(0, 0, 0, 8);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_running, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_running);
        root.Children.Add(_progress);
        root.Children.Add(buttons);
        root.Children.Add(_out);
        Content = root;

        _tick.Tick += (_, _) => UpdateRunning();
        Opened += (_, _) => Start();
        Closed += (_, _) =>
        {
            _tick.Stop();
            Stop();
        };
    }

    static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in new[] { "/usr/sbin", "/sbin", "/usr/bin" })
        {
            var p = Path.Combine(dir, exe);
            if (!OperatingSystem.IsWindows() && File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>The first available tool: (path, arguments, description).</summary>
    (string Exe, string[] Args, string What)? PickTool()
    {
        if (OperatingSystem.IsWindows())
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var pathping = Path.Combine(sys, "PATHPING.EXE");
            if (File.Exists(pathping))
                return (pathping, new[] { "-n", "-q", "10", "-p", "250", _host }, L.T("pathping: route and loss / latency on every hop (takes about a minute)"));
            var tracert = Path.Combine(sys, "TRACERT.EXE");
            if (File.Exists(tracert)) return (tracert, new[] { "-d", _host }, L.T("tracert: route only"));
            return null;
        }
        if (FindInPath("mtr") is { } mtr)
            return (mtr, new[] { "-r", "-w", "-n", "-c", "10", _host }, L.T("mtr: 10 rounds, loss and latency on every hop (takes about 10–20 s)"));
        if (FindInPath("tracepath") is { } tp) return (tp, new[] { "-n", _host }, L.T("tracepath: route only (mtr is not installed)"));
        if (FindInPath("traceroute") is { } tr) return (tr, new[] { "-n", _host }, L.T("traceroute: route only (mtr is not installed)"));
        return null;
    }

    void Start()
    {
        Stop();
        _out.Text = "";
        var tool = PickTool();
        if (tool is null)
        {
            _status.Text = OperatingSystem.IsWindows()
                ? L.T("No route tool found (pathping / tracert).")
                : L.T("No route tool found. Install mtr (openSUSE: sudo zypper install mtr; Debian / Ubuntu: sudo apt install mtr-tiny; RHEL: sudo dnf install mtr) or traceroute.");
            return;
        }
        var (exe, args, what) = tool.Value;
        _status.Text = L.F("{0}\nRunning: {1} {2}", what, Path.GetFileName(exe), string.Join(" ", args));
        _again.IsEnabled = false;
        SetRunning(true);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            // console tools write in the OEM code page (e.g. 866 for Ukrainian Windows)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = oem;
            psi.StandardErrorEncoding = oem;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                SetRunning(false);
                _again.IsEnabled = true;
                _status.Text += "\n" + L.F("Finished (exit code {0}).", SafeExit(p));
            });
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
        }
        catch (Exception ex)
        {
            _status.Text = L.T("Could not start the tool: ") + ex.Message;
            SetRunning(false);
            _again.IsEnabled = true;
        }
    }

    /// <summary>Visible "please wait" state: mtr / pathping print their report only at the end.</summary>
    void SetRunning(bool running)
    {
        _progress.IsVisible = running;
        _running.IsVisible = running;
        if (running)
        {
            _startedAt = DateTime.Now;
            UpdateRunning();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    void UpdateRunning() =>
        _running.Text = L.F("Running… {0} s. Please wait — the tool prints its result when it finishes.",
            (int)(DateTime.Now - _startedAt).TotalSeconds);

    static string SafeExit(Process p)
    {
        try { return p.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch { return "?"; }
    }

    void Append(string? line)
    {
        if (line is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _out.Text += line + "\n";
            _out.CaretIndex = _out.Text.Length;
        });
    }

    void Stop()
    {
        var p = _proc;
        _proc = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(true);
        }
        catch
        {
            // already finished
        }
        p.Dispose();
    }
}
