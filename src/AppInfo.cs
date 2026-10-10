using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace FreeMon;

public static class AppInfo
{
    public const string Author = "ITUASUPPORT";
    public const string LicenseName = "MIT";
    public const string Year = "2026";

    public static string Version =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "?";

    public static bool SoftwareRender { get; set; }

    public static bool IsWine()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return NativeLibrary.TryLoad("ntdll.dll", out var h) && NativeLibrary.TryGetExport(h, "wine_get_version", out _);
        }
        catch
        {
            return false;
        }
    }

    public static string Runtime => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public static string Resource(string name)
    {
        using var s = typeof(AppInfo).Assembly.GetManifestResourceStream(name);
        if (s is null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static WindowIcon? _icon;

    /// <summary>Application icon (window, dialogs). The tray uses status-colored icons.</summary>
    public static WindowIcon Icon
    {
        get
        {
            if (_icon is not null) return _icon;
            using var s = typeof(AppInfo).Assembly.GetManifestResourceStream("freemon.png");
            _icon = s is null ? Icons.ForStatus(DevStatus.Up) : new WindowIcon(new Bitmap(s));
            return _icon;
        }
    }
}

/// <summary>
/// A second launch for the same data folder asks the running copy to show its window (named pipe),
/// so the window can always be brought back even when there is no tray.
/// </summary>
public static class InstanceChannel
{
    const string ShowCommand = "show";

    static string PipeName
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Paths.DataDir.ToLowerInvariant()));
            return "freemon-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }

    public static async Task<bool> SignalExistingAsync()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await c.ConnectAsync(1500);
            var b = Encoding.UTF8.GetBytes(ShowCommand + "\n");
            await c.WriteAsync(b);
            await c.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("IPC client", ex);
            return false;
        }
    }

    public static void StartServer(Action onShow, CancellationToken ct)
    {
        var name = PipeName;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var s = new NamedPipeServerStream(name, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var r = new StreamReader(s);
                    var line = await r.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == ShowCommand) Dispatcher.UIThread.Post(onShow);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("IPC server", ex);
                    try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        });
    }
}

public static class CloseBehavior
{
    public static bool IsGnome =>
        OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    /// <summary>true = minimize to taskbar, false = hide to tray.</summary>
    public static bool Minimize(AppConfig cfg) => cfg.CloseAction switch
    {
        CloseAction.Minimize => true,
        CloseAction.Tray => false,
        _ => IsGnome,
    };
}
