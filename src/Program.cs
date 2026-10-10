using Avalonia;

namespace FreeMon;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("Unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write("UnobservedTask", e.Exception); e.SetObserved(); };
        AppInfo.SoftwareRender =
            args.Contains("--software-render") ||
            Environment.GetEnvironmentVariable("FREEMON_SOFTWARE_RENDER") == "1" ||
            AppInfo.IsWine();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var b = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        if (AppInfo.SoftwareRender)
        {
            // Wine / broken GPU drivers: render into a framebuffer without GPU and without DirectComposition
            b = b.With(new Win32PlatformOptions
                {
                    RenderingMode = new[] { Win32RenderingMode.Software },
                    CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
                })
                .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } });
        }
        return b;
    }
}

internal static class SingleInstance
{
    public static FileStream? TryAcquire(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

internal static class CrashLog
{
    static readonly object Gate = new();

    public static void Write(string where, Exception? ex)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Paths.CrashFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // nothing else we can do
        }
    }
}
