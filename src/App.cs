using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace FreeMon;

public sealed class AppCore
{
    Task? _stop;
    byte[]? _key;
    AlertDispatcher? _alerts;
    System.Threading.Timer? _backupTimer;

    /// <summary>Key for countersigning log seals; exists only while the administrator (with password) is logged in.</summary>
    public byte[]? AdminSealKey => _key is null
        ? null
        : System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, _key, 32, null,
            System.Text.Encoding.UTF8.GetBytes("FreeMon/admin-seal"));
    bool _adminNoPassword;

    public AppConfig Config { get; private set; }
    public LogStore Log { get; }
    public FreeMonitor Monitor { get; }
    public string? Warning { get; private set; }
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public event Action? LockChanged;

    /// <summary>View mode exists (with or without password).</summary>
    public bool Protected => Config.Security is not null || Config.ViewMode;
    public bool PasswordRequired => Config.Security is not null;
    public bool Unlocked => !Protected || (PasswordRequired ? _key is not null : _adminNoPassword);

    public AppCore((SecurityInfo Info, byte[] Key)? initial, bool initialViewMode = false, string? initialReportLanguage = null)
    {
        bool existed = Paths.HasConfig(Paths.DataDir);
        // a new key only for a new installation or when migrating a plain (pre-1.8) config;
        // an encrypted config without its key cannot be opened — restore from a backup instead
        InstallKey.Ensure(createIfMissing: !File.Exists(Paths.ConfigFile));
        Config = ConfigStore.Load();
        if (!existed)
        {
            Config.ViewMode = initialViewMode;
            if (initialReportLanguage is not null) Config.Report.Language = initialReportLanguage;
        }
        L.SetReportLanguage(Config.Report.Language);
        ProbeFactory.Init();
        Log = new LogStore(Paths.DbFile, Paths.EventsFile, () => Config.RetentionDays);
        Monitor = new FreeMonitor(Log);

        if (initial is { } ini && Config.Security is null)
        {
            Config.Security = ini.Info;
            _key = ini.Key;
            ConfigStore.Save(Config, _key);
        }
        else if (!existed)
        {
            ConfigStore.Save(Config, null);
        }

        if (ConfigStore.LoadWarning is { } lw) Warning = lw;
        if (Config.Security is null && File.Exists(Paths.ProtectedFlag))
        {
            Warning = L.T("Warning: password protection was removed outside the program — the configuration has no password, although it had one before. Check the device list and set a password in Settings → Security.");
        }
        else if (Config.Security is not null && !File.Exists(Paths.ProtectedFlag))
        {
            try { File.WriteAllText(Paths.ProtectedFlag, "FreeMon: config is password-protected\n"); }
            catch (Exception ex) { CrashLog.Write("Protected flag", ex); }
        }
    }

    public void Start()
    {
        Monitor.SystemEvent(L.F("FreeMon started. Data: {0} ({1}). ICMP: {2}. Devices: {3}", Paths.DataDir, Paths.KindText(Paths.Kind), ProbeFactory.Mode, Config.Devices.Count));
        if (Warning is not null) Monitor.SystemEvent(Warning);
        if (Config.Version > AppConfig.CurrentVersion)
            Monitor.SystemEvent(L.F("The configuration file has a newer schema ({0}); unknown parameters are kept unchanged", Config.Version));
        _alerts = new AlertDispatcher(this);
        _backupTimer = new System.Threading.Timer(_ => BackupService.AutoTick(this), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        _ = Monitor.ReloadAsync(Config);
    }

    public void Touch() => LastActivityUtc = DateTime.UtcNow;

    public void Lock()
    {
        if (!Protected || !Unlocked) return;
        _key = null;
        _adminNoPassword = false;
        Monitor.SystemEvent(L.T("View mode (administrator logged out)"));
        LockChanged?.Invoke();
    }

    public async Task<bool> VerifyPasswordAsync(string password)
    {
        var s = Config.Security;
        if (s is null) return true;
        var k = await Task.Run(() => PasswordCrypto.Verify(s, password));
        if (k is null) Monitor.SystemEvent(L.T("Wrong administrator password entered"));
        return k is not null;
    }

    public async Task<UnlockOutcome> UnlockAsync(string password)
    {
        var sec = Config.Security;
        if (sec is null)
        {
            if (Config.ViewMode && !_adminNoPassword)
            {
                _adminNoPassword = true;
                Touch();
                Monitor.SystemEvent(L.T("Administrator mode (no password)"));
                LockChanged?.Invoke();
            }
            return new UnlockOutcome(UnlockStatus.Ok, null);
        }

        var key = await Task.Run(() => PasswordCrypto.Verify(sec, password));
        if (key is not null)
        {
            _key = key;
            Touch();
            if (AdminSealKey is { } ask) Log.Countersign(ask);
            var r = await Task.Run(() => Integrity.Check(key));
            Monitor.SystemEvent(r.Ok ? L.T("Administrator login") : L.T("Administrator login: configuration changes made outside the program were detected"));
            LockChanged?.Invoke();
            return new UnlockOutcome(r.Ok ? UnlockStatus.Ok : UnlockStatus.Tampered, r);
        }

        var rescue = await Task.Run(() => Integrity.TryRescue(password));
        if (rescue is { } rs)
        {
            _key = rs.Key;
            Touch();
            Monitor.SystemEvent(L.T("Administrator login with the signed copy: the password in the configuration was changed outside the program"));
            LockChanged?.Invoke();
            return new UnlockOutcome(UnlockStatus.Tampered, rs.Result);
        }

        Monitor.SystemEvent(L.T("Failed administrator login attempt"));
        return new UnlockOutcome(UnlockStatus.Wrong, null);
    }

    public async Task ApplyAsync(AppConfig cfg, byte[]? newKey = null, bool reloadMonitor = true)
    {
        if (!Unlocked) throw new InvalidOperationException(L.T("Administrator login required"));
        bool wasAdmin = Unlocked;
        if (cfg.Security is null) _key = null;
        else if (newKey is not null) _key = newKey;
        if (cfg.Security is null && cfg.ViewMode && wasAdmin) _adminNoPassword = true;
        if (cfg.Security is not null && _key is null) throw new InvalidOperationException(L.T("No signing key — log in as administrator again"));

        ConfigStore.Save(cfg, _key);
        L.SetReportLanguage(cfg.Report.Language);
        // new or changed password: re-countersign the whole log with the new key
        if (newKey is not null && AdminSealKey is { } nk) Log.Countersign(nk, all: true);
        Config = cfg;
        if (cfg.Security is not null) Warning = null;
        Touch();
        LockChanged?.Invoke();
        if (reloadMonitor) await Monitor.ReloadAsync(cfg);
        else Monitor.UpdateConfig(cfg);
    }

    public Task StopAsync() => _stop ??= StopCoreAsync();

    async Task StopCoreAsync()
    {
        _alerts?.Stop();
        _backupTimer?.Dispose();
        await Monitor.StopAsync().ConfigureAwait(false);
        Monitor.SystemEvent(L.T("FreeMon stopping"));
        await Log.CloseAsync().ConfigureAwait(false);
    }
}

public sealed class App : Application
{
    AppCore? _core;
    MainWindow? _main;
    TrayIcon? _tray;
    DispatcherTimer? _timer;
    Notifier? _notifier;
    readonly CancellationTokenSource _ipc = new();
    FileStream? _lock;
    DevStatus _trayStatus = (DevStatus)(-1);
    string _trayTip = "";

    public static bool IsExiting { get; private set; }
    static App? _instance;

    /// <summary>Stops everything, writes the backup into the data folder and starts FreeMon again.</summary>
    public static async Task RestoreAndRestartAsync(BackupService.Opened backup, bool includeLog)
    {
        var app = _instance;
        if (app?._core is null) return;
        IsExiting = true;
        app._timer?.Stop();
        await app._core.StopAsync();
        BackupService.Apply(backup, includeLog);
        app.Relaunch();
    }

    public static void ApplyTheme(UiTheme t)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = t switch
        {
            UiTheme.Light => ThemeVariant.Light,
            UiTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _instance = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => OnExit();
            _ = StartAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    void OnExit()
    {
        IsExiting = true;
        _ipc.Cancel();
        try { _core?.StopAsync().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { CrashLog.Write("Exit", ex); }
        _lock?.Dispose();
    }

    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            (SecurityInfo Info, byte[] Key)? initial = null;
            bool wizardViewMode = false;
            L.SetLanguage(UiStateStore.Current.Language);
            string? wizardReportLanguage = null;
            if (!Paths.TryDetect())
            {
                WizardResult? res;
                while (true)
                {
                    var w = new WizardWindow();
                    res = await w.RunAsync();
                    if (!w.Relaunch) break; // the interface language was changed: show the wizard again
                }
                if (res is null)
                {
                    desktop.Shutdown();
                    return;
                }
                if (res.Password is { } pw)
                    initial = await Task.Run(() => PasswordCrypto.Create(pw));
                wizardViewMode = res.ViewMode;
                wizardReportLanguage = res.ReportLanguage;
            }

            _lock = SingleInstance.TryAcquire(Paths.LockFile);
            // after a restore the previous copy may still be closing: wait for its lock a little
            if (_lock is null && (desktop.Args ?? Array.Empty<string>()).Contains("--after-restore"))
                for (int i = 0; i < 30 && _lock is null; i++)
                {
                    await Task.Delay(500);
                    _lock = SingleInstance.TryAcquire(Paths.LockFile);
                }
            if (_lock is null)
            {
                // Already running: ask that copy to show its window; message only if it cannot be reached
                if (!await InstanceChannel.SignalExistingAsync())
                    await Dialogs.Standalone("FreeMon", L.T("FreeMon is already running for this data folder:\n") + Paths.DataDir +
                                                        L.T("\n\nCould not open the window of that instance (it may have been started by another user)."));
                desktop.Shutdown(1);
                return;
            }

            var core = new AppCore(initial, wizardViewMode, wizardReportLanguage);
            _core = core;
            ApplyTheme(core.Config.Theme);
            core.Start();
            _main = new MainWindow(core);
            _notifier = new Notifier(core, ShowMain);
            InstanceChannel.StartServer(ShowMain, _ipc.Token);
            SetupTray(desktop);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateTray();
            _timer.Start();

            var args = desktop.Args ?? Array.Empty<string>();
            if (!args.Contains("--tray")) _main.Show();
        }
        catch (Exception ex) when (ex is InstallKeyMissingException or System.Security.Cryptography.CryptographicException)
        {
            CrashLog.Write("Start: key", ex);
            await Dialogs.Standalone(L.T("FreeMon — data cannot be decrypted"),
                L.T("Data in the folder\n") + Paths.DataDir + L.T("\ncannot be decrypted on this computer: the key of this installation is missing or belongs to another computer or user account (for example, the folder was copied or the OS was reinstalled).\n\nIf you have a backup: rename or move this folder, start FreeMon again and click \"Restore from backup\" in the wizard."));
            desktop.Shutdown(3);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Start", ex);
            await Dialogs.Standalone(L.T("FreeMon — startup error"), ex.Message + L.T("\n\nDetails: ") + Paths.CrashFile);
            desktop.Shutdown(2);
        }
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem(L.T("Open"));
        open.Click += (_, _) => ShowMain();
        var exit = new NativeMenuItem(L.T("Exit"));
        exit.Click += async (_, _) => await ExitAppAsync(desktop);
        _restartItem = new NativeMenuItem(L.T("Restart"));
        _restartItem.Click += async (_, _) => await RestartAppAsync();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_restartItem);
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = Icons.ForStatus(DevStatus.Unknown),
            ToolTipText = "FreeMon",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    async Task ExitAppAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (IsExiting || _core is null) return;
        if (_core.Protected && !_core.Unlocked && _main is not null)
        {
            ShowMain();
            var core = _core;
            bool ok = core.PasswordRequired
                ? await PasswordDialog.Ask(_main, L.T("Exit FreeMon"),
                    L.T("Exiting stops monitoring. Enter the administrator password:"),
                    p => core.VerifyPasswordAsync(p))
                : await Dialogs.Confirm(_main, L.T("Exit FreeMon"), L.T("Exiting stops monitoring. Exit?"));
            if (!ok) return;
        }
        IsExiting = true;
        _timer?.Stop();
        await _core.StopAsync();
        if (_tray is not null) _tray.IsVisible = false;
        desktop.Shutdown();
    }

    NativeMenuItem? _restartItem;

    /// <summary>Restart (administrator only): stop monitoring cleanly and start FreeMon again.</summary>
    async Task RestartAppAsync()
    {
        if (IsExiting || _core is null || !_core.Unlocked || _main is null) return;
        ShowMain();
        if (!await Dialogs.Confirm(_main, L.T("Restart FreeMon"),
                L.T("Restart FreeMon? Monitoring stops for a few seconds while the program restarts."))) return;
        if (IsExiting) return;
        IsExiting = true;
        _timer?.Stop();
        await _core.StopAsync();
        Relaunch();
    }

    /// <summary>Releases the single-instance lock, starts a new FreeMon process (it waits for the lock) and exits.</summary>
    void Relaunch()
    {
        _ipc.Cancel();
        _lock?.Dispose();
        _lock = null;
        if (Environment.ProcessPath is { } exe)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--after-restore");
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        if (_tray is not null) _tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void UpdateTray()
    {
        if (_core is null || _tray is null) return;
        if (_restartItem is not null) _restartItem.IsVisible = _core.Unlocked;
        int up = 0, deg = 0, down = 0, off = 0;
        var worst = DevStatus.Up;
        bool any = false;
        foreach (var d in _core.Config.Devices)
        {
            if (!d.Enabled) { off++; continue; }
            var rt = _core.Monitor.Get(d.Id);
            var eff = rt is null ? DevStatus.Unknown : _core.Monitor.Effective(rt);
            any = true;
            if (StatusText.Severity(eff) > StatusText.Severity(worst)) worst = eff;
            if (eff == DevStatus.Up) up++;
            else if (eff == DevStatus.Degraded) deg++;
            else if (eff is DevStatus.Down or DevStatus.Unreachable) down++;
        }
        if (!any) worst = DevStatus.Unknown;
        if (worst == DevStatus.Unreachable) worst = DevStatus.Down;
        if (worst != _trayStatus)
        {
            _trayStatus = worst;
            _tray.Icon = Icons.ForStatus(worst);
        }
        var tip = L.F("FreeMon — up: {0}, unstable: {1}, down: {2}", up, deg, down) + (off > 0 ? L.F(", disabled: {0}", off) : "");
        if (tip != _trayTip)
        {
            _trayTip = tip;
            _tray.ToolTipText = tip;
        }
    }
}
