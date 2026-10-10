using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FreeMon;

/// <summary>Short important notice with a link to the full encryption policy.</summary>
public sealed class EncryptionNoticeWindow : Window
{
    public EncryptionNoticeWindow()
    {
        Title = L.T("Important: encryption and backups");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;
        var p = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        p.Children.Add(new TextBlock { Text = EncryptionPolicy.Notice, TextWrapping = TextWrapping.Wrap });
        var more = new Button { Content = L.T("More: encryption policy") };
        more.Click += async (_, _) => await new AboutWindow(AboutWindow.EncryptionTab).ShowDialog(this);
        p.Children.Add(Ui.ButtonRow(more, Ui.Btn(L.T("Got it"), (_, _) => Close(), true)));
        Content = p;
    }

    /// <summary>A small clickable "important" line for wizard and settings tabs.</summary>
    public static Control Link(Func<Window?> owner)
    {
        var t = new TextBlock
        {
            Text = L.T("⚠ Important about encryption and backups — please read"),
            TextDecorations = TextDecorations.Underline,
            Foreground = Icons.Brush(DevStatus.Degraded),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            TextWrapping = TextWrapping.Wrap,
        };
        t.PointerPressed += async (_, _) =>
        {
            var o = owner();
            if (o is not null) await new EncryptionNoticeWindow().ShowDialog(o);
        };
        return t;
    }
}

/// <summary>Asks for a backup password (twice when creating).</summary>
public sealed class BackupPasswordDialog : Window
{
    readonly TextBox _p1 = Ui.PasswordBox();
    readonly TextBox _p2 = Ui.PasswordBox();
    readonly CheckBox _log = new() { IsChecked = true };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    readonly bool _create;

    public string Password => _p1.Text ?? "";
    public bool IncludeLog => _log.IsChecked == true;

    public BackupPasswordDialog(string title, bool create, bool askLog)
    {
        _create = create;
        _log.Content = create
            ? L.T("Include the check log (the file will be larger)")
            : L.T("Also restore the check log (if the backup contains it)");
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;
        var p = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        p.Children.Add(Ui.Row(create ? L.T("Backup password (at least 8 characters)") : L.T("Backup copy password"), _p1, 230));
        if (create) p.Children.Add(Ui.Row(L.T("Repeat password"), _p2, 230));
        if (askLog) p.Children.Add(_log);
        if (create) p.Children.Add(Ui.Hint(L.T("The password is not stored anywhere. Without it the backup cannot be opened — keep it in a safe place.")));
        p.Children.Add(_err);
        p.Children.Add(Ui.ButtonRow(Ui.Btn("OK", (_, _) => Ok(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false))));
        Content = p;
        Opened += (_, _) => _p1.Focus();
    }

    void Ok()
    {
        if (_create && Password.Length < 8) { _err.Text = L.T("The password must be at least 8 characters long"); return; }
        if (_create && Password != (_p2.Text ?? "")) { _err.Text = L.T("Passwords do not match"); return; }
        if (Password.Length == 0) { _err.Text = L.T("Enter the password"); return; }
        Close(true);
    }
}

public static class BackupUi
{
    /// <summary>Asks for a file and a password and writes a manual backup.</summary>
    public static async Task CreateManualAsync(Window owner, AppCore core, Action<string> status)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Save backup"),
            SuggestedFileName = $"freemon-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.fmbackup",
            DefaultExtension = "fmbackup",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { FileType },
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var d = new BackupPasswordDialog(L.T("Backup password"), true, true);
        if (!await d.ShowDialog<bool>(owner)) return;
        status(L.T("Creating the backup…"));
        try
        {
            var pw = d.Password;
            var log = d.IncludeLog;
            await Task.Run(() => BackupService.CreateWithPassword(path, pw, log));
            status("");
            core.Monitor.SystemEvent(L.T("Backup created: ") + path);
            await Dialogs.Info(owner, L.T("Backup"), L.T("Backup saved:\n") + path);
        }
        catch (Exception ex)
        {
            status(L.T("Could not create the backup: ") + ex.Message);
        }
    }

    /// <summary>Asks for a backup file and password, confirms, writes it and restarts FreeMon.</summary>
    public static async Task RestoreAsync(Window owner, Action<string> error)
    {
        var o = await PickAndOpen(owner, error);
        if (o is not { } opened) return;
        var h = opened.Backup.Header;
        var msg = L.F("Backup: {0}, {1}, {2}\n\nThe following will be COMPLETELY REPLACED (not merged) with the data from the backup:\n  • device list, families, types, checks, connections, map positions;\n  • all settings, notification channels with their secrets;\n  • administrator password and access mode — as they were when the backup was made;\n  • interface settings of the current user.\n", h.App, h.Host, h.Created) +
                  (opened.IncludeLog
                      ? L.T("  • the check log; the current log will be kept next to it with the .before-restore extension.\n")
                      : L.T("The check log will stay as it is.\n")) +
                  L.T("\nFreeMon will restart. Continue?");
        if (!await Dialogs.Confirm(owner, L.T("Restore from backup"), msg)) return;
        await App.RestoreAndRestartAsync(opened.Backup, opened.IncludeLog);
    }

    public static FilePickerFileType FileType => new(L.T("FreeMon backup")) { Patterns = new[] { "*.fmbackup" } };

    /// <summary>Asks for a file and a password and opens the backup (validates the password).</summary>
    public static async Task<(BackupService.Opened Backup, bool IncludeLog)?> PickAndOpen(Window owner, Action<string> error)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("FreeMon backup"),
            AllowMultiple = false,
            FileTypeFilter = new[] { FileType, FilePickerFileTypes.All },
        });
        if (files.Count == 0) return null;
        var path = files[0].TryGetLocalPath();
        if (path is null) { error(L.T("The file must be on a local disk or a mounted folder")); return null; }
        var dlg = new BackupPasswordDialog(L.T("Open backup"), false, true);
        if (!await dlg.ShowDialog<bool>(owner)) return null;
        var password = dlg.Password;
        var restoreLog = dlg.IncludeLog;
        try
        {
            var o = await Task.Run(() => BackupService.Open(path, password));
            return (o, restoreLog && o.Header.HasLog);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            error(L.T("Wrong password or the file is damaged"));
        }
        catch (Exception ex)
        {
            error(ex.Message);
        }
        return null;
    }
}

/// <summary>"Перевірити звіт": paste the code from a PDF / TXT report.</summary>
public sealed class VerifyReportWindow : Window
{
    readonly AppCore _core;
    readonly TextBox _code = new() { Width = 620, PlaceholderText = "FMV1.…" };
    readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };

    public VerifyReportWindow(AppCore core)
    {
        _core = core;
        Title = L.T("Report verification");
        Width = 700;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;
        var check = new Button { Content = L.T("Verify") };
        check.Click += async (_, _) => await CheckAsync(check);
        var p = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        p.Children.Add(Ui.Hint(L.T("Paste the verification code from a PDF report (\"Report authenticity\") or from a raw log. The program recalculates the figures for the same period from the log and compares them with the code.")));
        p.Children.Add(_code);
        p.Children.Add(check);
        p.Children.Add(_result);
        p.Children.Add(Ui.ButtonRow(Ui.Btn(L.T("Close"), (_, _) => Close(), true)));
        Content = p;
    }

    async Task CheckAsync(Button b)
    {
        if (!ReportCode.TryParse(_code.Text ?? "", out var devId, out var from, out var to, out var mac))
        {
            _result.Text = L.T("This does not look like a FreeMon verification code.");
            _result.Foreground = Icons.Brush(DevStatus.Down);
            return;
        }
        b.IsEnabled = false;
        _result.Text = L.T("Recalculating…");
        _result.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            var dev = _core.Config.Devices.FirstOrDefault(d => d.Id == devId) ?? new Device { Id = devId, Name = L.T("(device removed from the list)") };
            var adminKey = _core.AdminSealKey;
            var r = await Task.Run(() => HistoryCalc.Compute(dev, from, to, adminKey));
            bool ok = ReportCode.Matches(r, mac);
            _result.Foreground = Icons.Brush(ok ? DevStatus.Up : DevStatus.Down);
            _result.Text = (ok
                    ? L.T("Code is valid: the report was made by this FreeMon installation and the log data for the period has not changed since.\n")
                    : L.T("Code does NOT match: the report was made by another installation, changed after it was made, or the log data for this period was changed.\n")) +
                L.F("\nDevice: {0}\nPeriod: {1}\nActual figures: up {2}, down {3}, incidents {4}, checks in the log {5}.\n", dev.Name, HistoryText.Period(r), (r.AvailabilityPct is double a ? HistoryText.Num(a) + "%" : "—"), HistoryText.Dur(r.DownSec), r.Incidents.Count, r.CycleCount) +
                HistoryText.IntegrityText(r);
        }
        catch (Exception ex)
        {
            _result.Text = L.T("Check error: ") + ex.Message;
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}
