using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FreeMon;

public sealed class PasswordDialog : Window
{
    readonly TextBox _pw = Ui.PasswordBox();
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    readonly Func<string, Task<bool>> _check;
    bool _busy;

    PasswordDialog(string title, string prompt, Func<string, Task<bool>> check)
    {
        _check = check;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var p = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        p.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(_pw);
        p.Children.Add(_err);
        p.Children.Add(Ui.ButtonRow(Ui.Btn("OK", async (_, _) => await OnOk(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false))));
        Content = p;
        Opened += (_, _) => _pw.Focus();
    }

    async Task OnOk()
    {
        if (_busy) return;
        _busy = true;
        _err.Text = L.T("Checking…");
        bool ok;
        try { ok = await _check(_pw.Text ?? ""); }
        finally { _busy = false; }
        if (ok)
        {
            Close(true);
            return;
        }
        _err.Text = L.T("Wrong password");
        _pw.Text = "";
        _pw.Focus();
    }

    public static Task<bool> Ask(Window owner, string title, string prompt, Func<string, Task<bool>> check) =>
        new PasswordDialog(title, prompt, check).ShowDialog<bool>(owner);
}

public sealed class TamperDialog : Window
{
    public TamperDialog(IntegrityResult r)
    {
        Title = L.T("Configuration changed outside the program");
        Width = 680;
        Height = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var intro = r.CanAccept
            ? L.T("The configuration file was changed not by FreeMon (after the last save by the administrator). Changes relative to the last signed copy:")
            : L.T("The password in the configuration was changed outside the program. Your password matches the last signed copy. Changes relative to it:");
        var outro = r.CanAccept
            ? L.T("\"Roll back\" — restore the signed copy. \"Accept\" — keep the current file and sign it. \"Later\" — do nothing now.")
            : L.T("\"Roll back\" — restore the signed copy (with the old password). \"Cancel\" — leave administrator mode.");

        var buttons = new List<Control>();
        if (r.SignedCopyValid) buttons.Add(Ui.Btn(L.T("Roll back"), (_, _) => Close(TamperChoice.Revert), true));
        if (r.CanAccept) buttons.Add(Ui.Btn(L.T("Accept"), (_, _) => Close(TamperChoice.Accept)));
        buttons.Add(Ui.Btn(r.CanAccept ? L.T("Later") : L.T("Cancel"), (_, _) => Close(TamperChoice.Cancel)));
        var row = Ui.ButtonRow(buttons.ToArray());
        row.Margin = new Thickness(14, 6, 14, 14);

        var top = new StackPanel { Margin = new Thickness(14, 14, 14, 6), Spacing = 6 };
        top.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap });
        var bottom = new TextBlock { Text = outro, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(14, 0) };

        var list = new TextBox
        {
            Text = string.Join("\n", r.Changes.Select(c => "• " + c)),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 0),
        };

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(row, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(row);
        dock.Children.Add(bottom);
        dock.Children.Add(list);
        Content = dock;
    }
}

public sealed record WizardResult(string? Password, bool ViewMode, string? ReportLanguage = null);

/// <summary>First run: where to store data + optional administrator password.</summary>
public sealed class WizardWindow : Window
{
    readonly RadioButton _rUser = new() { GroupName = "loc", Content = L.T("Only for me"), IsChecked = true };
    readonly RadioButton _rAll = new() { GroupName = "loc", Content = L.T("For all users of this computer") };
    readonly RadioButton _rPortable = new() { GroupName = "loc", Content = L.T("Next to the program (portable)") };
    readonly RadioButton _rCustom = new() { GroupName = "loc", Content = L.T("Custom folder") };
    readonly TextBox _custom = new() { Width = 420 };
    readonly RadioButton _aOpen = new() { GroupName = "access", Content = L.T("No restrictions — anyone can change the list and settings"), IsChecked = true };
    readonly RadioButton _aView = new() { GroupName = "access", Content = L.T("View mode; administrator mode — by button, no password") };
    readonly RadioButton _aPw = new() { GroupName = "access", Content = L.T("View mode; administrator mode — with a password") };
    readonly TextBox _pw1 = Ui.PasswordBox();
    readonly TextBox _pw2 = Ui.PasswordBox();
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    WizardResult? _result;
    readonly ComboBox _uiLang = new() { Width = 220 };
    readonly ComboBox _repLang = new() { Width = 220 };

    /// <summary>The interface language was changed: the caller shows the wizard again in the new language.</summary>
    public bool Relaunch { get; private set; }
    (BackupService.Opened Backup, bool IncludeLog)? _restore;
    readonly TextBlock _restoreInfo = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

    public WizardWindow()
    {
        Title = L.T("FreeMon — first-time setup");
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppInfo.Icon;

        _pw1.IsEnabled = false;
        _pw2.IsEnabled = false;
        _aPw.IsCheckedChanged += (_, _) =>
        {
            var on = _aPw.IsChecked == true;
            _pw1.IsEnabled = on;
            _pw2.IsEnabled = on;
        };

        TextBlock Path(string text) => new()
        {
            Text = text,
            Opacity = 0.65,
            Margin = new Thickness(28, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        var p = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        _uiLang.ItemsSource = L.Languages.Select(x => x.Name).ToList();
        _uiLang.SelectedIndex = Array.FindIndex(L.Languages, x => x.Code == L.Language);
        _uiLang.SelectionChanged += (_, _) =>
        {
            var i = _uiLang.SelectedIndex;
            if (i < 0 || L.Languages[i].Code == L.Language) return;
            UiStateStore.Current.Language = L.Languages[i].Code;
            UiStateStore.Save();
            L.SetLanguage(L.Languages[i].Code);
            Relaunch = true;
            Close();
        };
        _repLang.ItemsSource = new[] { L.T("Same as the user's interface language") }.Concat(L.Languages.Select(x => x.Name)).ToList();
        _repLang.SelectedIndex = 0;
        p.Children.Add(Ui.Row(L.T("Interface language / Мова інтерфейсу"), _uiLang, 260));
        p.Children.Add(Ui.Row(L.T("Report language (PDF, raw log)"), _repLang, 260));
        var readmeLink = new TextBlock
        {
            Text = L.T("What FreeMon can do — read the description (README)"),
            TextDecorations = TextDecorations.Underline,
            Foreground = Brushes.DodgerBlue,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Margin = new Thickness(0, 4, 0, 4),
        };
        readmeLink.PointerPressed += async (_, _) => await new AboutWindow(AboutWindow.ReadmeTab).ShowDialog(this);
        p.Children.Add(readmeLink);
        p.Children.Add(new Border
        {
            BorderBrush = Icons.Brush(DevStatus.Degraded),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = L.T("FreeMon data is stored encrypted and bound to this computer. The data folder cannot be copied to another computer — only via a backup. Remember to make backups."),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    EncryptionNoticeWindow.Link(() => this),
                },
            },
        });
        var restoreBtn = new Button { Content = L.T("Restore from backup…") };
        restoreBtn.Click += async (_, _) =>
        {
            var o = await BackupUi.PickAndOpen(this, e => _err.Text = e);
            if (o is null) return;
            _restore = o;
            var h = o.Value.Backup.Header;
            _restoreInfo.Text = L.F("The backup will be restored: {0}, {1}, {2}. Choose a folder below and click \"Done\"; access settings are taken from the backup.", h.App, h.Host, h.Created);
        };
        var rrow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rrow.Children.Add(restoreBtn);
        p.Children.Add(rrow);
        p.Children.Add(_restoreInfo);
        p.Children.Add(new TextBlock
        {
            Text = L.T("Where to store the device list, settings and check log?"),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        p.Children.Add(_rUser);
        p.Children.Add(Path(Paths.UserDir));

        if (Paths.AllUsersDir is { } au)
        {
            p.Children.Add(_rAll);
            p.Children.Add(Path(au + L.T(" — shared data for all Windows users; the \"Users\" group gets permission to modify only this folder")));
        }

        p.Children.Add(_rPortable);
        p.Children.Add(Path(Paths.PortableDir + L.T(" — convenient for a USB drive; the program folder must be writable")));

        p.Children.Add(_rCustom);
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(28, 0, 0, 6) };
        customRow.Children.Add(_custom);
        customRow.Children.Add(Ui.Btn(L.T("Browse…"), async (_, _) => await PickFolder()));
        p.Children.Add(customRow);


        p.Children.Add(Ui.Header(L.T("Access to changes")));
        p.Children.Add(_aOpen);
        p.Children.Add(_aView);
        p.Children.Add(_aPw);
        var pwRows = new StackPanel { Margin = new Thickness(28, 0, 0, 0), Spacing = 2 };
        pwRows.Children.Add(Ui.Row(L.T("Password (at least 6 characters)"), _pw1, 232));
        pwRows.Children.Add(Ui.Row(L.T("Repeat password"), _pw2, 232));
        p.Children.Add(pwRows);
        p.Children.Add(Ui.Hint(L.T("In view mode the state, log and reports are visible, while changing the list and settings or exiting the program is possible only in administrator mode. Without a password this protects against accidental changes, with a password — against outsiders. You can change it later in \"Settings → Security\".")));
        p.Children.Add(_err);
        p.Children.Add(Ui.ButtonRow(Ui.Btn(L.T("Done"), (_, _) => Finish(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close())));
        Content = p;
    }

    public Task<WizardResult?> RunAsync()
    {
        var tcs = new TaskCompletionSource<WizardResult?>();
        Closed += (_, _) => tcs.TrySetResult(_result);
        Show();
        return tcs.Task;
    }

    async Task PickFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.T("Folder for FreeMon data"),
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path is null)
        {
            _err.Text = L.T("This folder has no local path — choose another one");
            return;
        }
        _custom.Text = path;
        _rCustom.IsChecked = true;
    }

    void Finish()
    {
        var kind = _rAll.IsChecked == true ? StorageKind.AllUsers
            : _rPortable.IsChecked == true ? StorageKind.Portable
            : _rCustom.IsChecked == true ? StorageKind.Custom
            : StorageKind.User;

        string? custom = null;
        if (kind == StorageKind.Custom)
        {
            custom = (_custom.Text ?? "").Trim();
            if (custom.Length == 0) { _err.Text = L.T("Enter a folder or click \"Browse…\""); return; }
        }

        string? pw = null;
        if (_restore is null && _aPw.IsChecked == true)
        {
            pw = _pw1.Text ?? "";
            if (pw.Length < PasswordCrypto.MinLength) { _err.Text = L.F("The password must be at least {0} characters long", PasswordCrypto.MinLength); return; }
            if (pw != (_pw2.Text ?? "")) { _err.Text = L.T("Passwords do not match"); return; }
        }

        try
        {
            Paths.Apply(kind, custom);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Wizard apply", ex);
            _err.Text = L.T("Cannot use this folder: ") + ex.Message;
            return;
        }

        if (_restore is { } rs)
        {
            try
            {
                if (Paths.HasConfig(Paths.DataDir))
                {
                    _err.Text = L.T("The selected folder already contains FreeMon data — choose another folder for the restore.");
                    return;
                }
                BackupService.Apply(rs.Backup, rs.IncludeLog);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Wizard restore", ex);
                _err.Text = L.T("Could not restore the backup: ") + ex.Message;
                return;
            }
            _result = new WizardResult(null, false);
            Close();
            return;
        }
        _result = new WizardResult(pw, _aView.IsChecked == true || _aPw.IsChecked == true, ReportLanguageCode());
        Close();
    }

    string ReportLanguageCode() => _repLang.SelectedIndex > 0 ? L.Languages[_repLang.SelectedIndex - 1].Code : "";
}
