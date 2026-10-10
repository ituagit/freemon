using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Win32;

namespace FreeMon;

/// <summary>Collects status changes (batched ~1.5 s) and shows one popup in the bottom-right corner.</summary>
public sealed class Notifier
{
    readonly AppCore _core;
    readonly Action _openMain;
    readonly List<StatusChange> _pending = new();
    readonly DispatcherTimer _batch;
    NotifyWindow? _win;

    public Notifier(AppCore core, Action openMain)
    {
        _core = core;
        _openMain = openMain;
        _batch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _batch.Tick += (_, _) => Flush();
        core.Monitor.StatusChanged += OnChanged; // worker thread
    }

    void OnChanged(StatusChange c)
    {
        var n = _core.Config.Notify;
        if (c.Initial) return;
        if (c.To == DevStatus.Down && c.ParentDown && n.SkipUnreachableChildren) return;
        bool want = c.To switch
        {
            DevStatus.Down => n.OnDown,
            DevStatus.Degraded => n.OnDegraded,
            DevStatus.Up => n.OnRecovery && c.From is DevStatus.Down or DevStatus.Degraded,
            _ => false,
        };
        if (!want) return;
        Dispatcher.UIThread.Post(() =>
        {
            _pending.Add(c);
            _batch.Stop();
            _batch.Start();
        });
    }

    void Flush()
    {
        _batch.Stop();
        if (_pending.Count == 0) return;
        var items = _pending.ToList();
        _pending.Clear();

        if (_win is null || !_win.IsVisible)
        {
            _win = new NotifyWindow(_openMain);
            _win.Closed += (_, _) => _win = null;
            _win.Add(items, _core.Config.Notify.PopupSeconds);
            _win.Show();
        }
        else
        {
            _win.Add(items, _core.Config.Notify.PopupSeconds);
        }

        var n = _core.Config.Notify;
        var audible = items.Where(i => i.Device.PlaySounds).ToList();
        if (n.Sound && audible.Any(i => i.To == DevStatus.Down)) Sound.Play(SoundKind.Down);
        else if (n.Sound && audible.Any(i => i.To == DevStatus.Degraded)) Sound.Play(SoundKind.Degraded);
        else if (n.SoundRecovery && audible.Any(i => i.To == DevStatus.Up && i.From is DevStatus.Down or DevStatus.Degraded))
            Sound.Play(SoundKind.Recovery);
    }
}

public sealed class NotifyWindow : Window
{
    const int MaxLines = 10;
    readonly StackPanel _lines = new() { Spacing = 4 };
    readonly TextBlock _more = new() { Opacity = 0.7, IsVisible = false };
    readonly Border _frame;
    readonly List<(StatusChange C, DateTime At)> _all = new();
    readonly Action _openMain;
    DispatcherTimer? _close;

    public NotifyWindow(Action openMain)
    {
        _openMain = openMain;
        Title = "FreeMon";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowDecorations = WindowDecorations.None;

        var close = new Button { Content = "✕", Padding = new Thickness(6, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var title = new TextBlock { Text = L.T("FreeMon — state changes"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(title);

        var body = new StackPanel { Margin = new Thickness(12, 8), Spacing = 2 };
        body.Children.Add(head);
        body.Children.Add(_lines);
        body.Children.Add(_more);
        body.Children.Add(new TextBlock { Text = L.T("Click to open FreeMon"), Opacity = 0.55, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });

        _frame = new Border { BorderThickness = new Thickness(3), Child = body };
        Content = _frame;

        body.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && IsInside(v, close)) return;
            _openMain();
            Close();
        };
        Opened += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    static bool IsInside(Visual v, Visual target)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (ReferenceEquals(cur, target)) return true;
        return false;
    }

    public void Add(List<StatusChange> items, int seconds)
    {
        var now = DateTime.Now;
        _all.AddRange(items.Select(i => (i, now)));
        _lines.Children.Clear();
        foreach (var (c, at) in _all.AsEnumerable().Reverse().Take(MaxLines))
        {
            // time | dot | name (bold) · check: state
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            var time = new TextBlock
            {
                Text = at.ToString("HH:mm"),
                Opacity = 0.6,
                FontSize = 12,
                Width = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Icons.Brush(c.To),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(c.Device.Name) { FontWeight = FontWeight.SemiBold });
            text.Inlines.Add(new Avalonia.Controls.Documents.Run((c.Check is null ? "" : " · " + c.Check) + ": " + StatusText.Ua(c.To)));
            Grid.SetColumn(time, 0);
            Grid.SetColumn(dot, 1);
            Grid.SetColumn(text, 2);
            row.Children.Add(time);
            row.Children.Add(dot);
            row.Children.Add(text);
            ToolTip.SetTip(row, $"{c.Device.Name} ({c.Device.Address}){(c.Check is null ? "" : L.T("\nCheck: ") + c.Check)}\n{StatusText.Ua(c.From)} → {StatusText.Ua(c.To)}; {c.Detail}");
            _lines.Children.Add(row);
        }
        _more.IsVisible = _all.Count > MaxLines;
        _more.Text = L.F("… and {0} more", _all.Count - MaxLines);

        var worst = _all.Select(x => x.C.To).OrderByDescending(StatusText.Severity).First();
        _frame.BorderBrush = Icons.Brush(worst);

        _close?.Stop();
        if (seconds > 0)
        {
            _close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _close.Tick += (_, _) =>
            {
                _close?.Stop();
                Close();
            };
            _close.Start();
        }
    }

    void Reposition()
    {
        var scr = Screens.Primary;
        if (scr is null) return;
        var wa = scr.WorkingArea;
        var sc = scr.Scaling;
        int w = (int)(Bounds.Width * sc);
        int h = (int)(Bounds.Height * sc);
        Position = new PixelPoint(wa.X + wa.Width - w - 16, wa.Y + wa.Height - h - 16);
    }
}

public enum SoundKind { Down, Degraded, Recovery }

/// <summary>
/// Notification sounds: own WAV files embedded in the program (generated for FreeMon, MIT).
/// Windows: played from memory (winmm). Linux: paplay → pw-play → aplay from a cached copy, then the desktop theme
/// sound (canberra). Last resort on both: a beep.
/// </summary>
public static class Sound
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    static extern bool PlaySoundMem(byte[] data, IntPtr hmod, uint flags);

    [DllImport("kernel32.dll")]
    static extern bool Beep(uint freq, uint durationMs);

    const uint SndSync = 0x0000;
    const uint SndNoDefault = 0x0002;
    const uint SndMemory = 0x0004;

    static readonly object Gate = new();
    static bool _busy;

    static string ResName(SoundKind k) => k switch
    {
        SoundKind.Down => "sound-down.wav",
        SoundKind.Degraded => "sound-degraded.wav",
        _ => "sound-recovery.wav",
    };

    static byte[]? Data(SoundKind k)
    {
        using var s = typeof(Sound).Assembly.GetManifestResourceStream(ResName(k));
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Plays in the background; a sound that is already playing is not interrupted or queued.</summary>
    public static void Play(SoundKind k)
    {
        lock (Gate)
        {
            if (_busy) return;
            _busy = true;
        }
        Task.Run(() =>
        {
            try { PlayCore(k); }
            catch (Exception ex) { CrashLog.Write("Sound", ex); }
            finally { lock (Gate) _busy = false; }
        });
    }

    static void PlayCore(SoundKind k)
    {
        var data = Data(k);
        if (OperatingSystem.IsWindows())
        {
            if (data is not null && PlaySoundMem(data, IntPtr.Zero, SndSync | SndMemory | SndNoDefault)) return;
            BeepPattern(k);
            return;
        }
        if (data is not null && Cached(k, data) is { } file)
        {
            if (Run("paplay", file) || Run("pw-play", file) || Run("aplay", "-q", file)) return;
        }
        if (Run("canberra-gtk-play", "-i", k == SoundKind.Recovery ? "complete" : "dialog-warning")) return;
        BeepPattern(k);
    }

    /// <summary>Linux players need a file: a copy in the user's cache folder, rewritten only when it differs.</summary>
    static string? Cached(SoundKind k, byte[] data)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "FreeMon", "sounds");
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) dir = Path.Combine(xdg, "FreeMon", "sounds");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, ResName(k));
            if (!File.Exists(file) || new FileInfo(file).Length != data.Length) File.WriteAllBytes(file, data);
            return file;
        }
        catch
        {
            return null;
        }
    }

    static void BeepPattern(SoundKind k)
    {
        (uint F, uint Ms)[] pattern = k switch
        {
            SoundKind.Down => new[] { (988u, 160u), (740u, 220u), (988u, 160u), (740u, 220u) },
            SoundKind.Degraded => new[] { (660u, 120u), (0u, 80u), (660u, 120u) },
            _ => new[] { (523u, 90u), (784u, 140u) },
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var (f, ms) in pattern)
            {
                if (f == 0) Thread.Sleep((int)ms);
                else Beep(f, ms);
            }
            return;
        }
        // Linux: the "beep" utility (PC speaker) if it is installed; otherwise silence
        foreach (var (f, ms) in pattern)
        {
            if (f == 0) { Thread.Sleep((int)ms); continue; }
            if (!Run("beep", "-f", f.ToString(), "-l", ms.ToString())) return;
        }
    }

    /// <summary>Runs a player and waits for it; true only if it exists and finished successfully.</summary>
    static bool Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                return true;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Per-user autostart: Windows HKCU\...\Run, Linux ~/.config/autostart/freemon.desktop. Starts minimized to tray.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FreeMon";

    static string Exe => Environment.ProcessPath ?? "";

    static string DesktopFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", "freemon.desktop");

    public static string Where => OperatingSystem.IsWindows()
        ? @"HKCU\" + RunKey + @" → " + ValueName
        : DesktopFile;

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WinIsEnabled();
            return File.Exists(DesktopFile);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Autostart read", ex);
            return false;
        }
    }

    public static void Set(bool on)
    {
        if (OperatingSystem.IsWindows())
        {
            WinSet(on);
            return;
        }
        if (on)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile,
                L.F("[Desktop Entry]\nType=Application\nName=FreeMon\nComment=Network device availability monitoring\nExec=\"{0}\" --tray\nTerminal=false\nX-GNOME-Autostart-enabled=true\n", Exe));
        }
        else if (File.Exists(DesktopFile))
        {
            File.Delete(DesktopFile);
        }
    }

    [SupportedOSPlatform("windows")]
    static bool WinIsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    [SupportedOSPlatform("windows")]
    static void WinSet(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, $"\"{Exe}\" --tray");
        else k.DeleteValue(ValueName, false);
    }
}
