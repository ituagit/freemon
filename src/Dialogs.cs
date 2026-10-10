using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace FreeMon;

public static class Ui
{
    public static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 4, 8, 4),
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 4),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBox PasswordBox() => new() { PasswordChar = '•', Width = 260 };

    public static Button Btn(string text, EventHandler<RoutedEventArgs> onClick, bool isDefault = false)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault };
        b.Click += onClick;
        return b;
    }

    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var p = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static StackPanel Row(string label, Control c, double labelWidth = 300)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var l = Label(label);
        l.Width = labelWidth;
        l.TextWrapping = TextWrapping.Wrap;
        p.Children.Add(l);
        p.Children.Add(c);
        return p;
    }
}

public static class Dialogs
{
    public static Task Info(Window owner, string title, string text) => Show(owner, title, text, false);

    public static Task<bool> Confirm(Window owner, string title, string text) => Show(owner, title, text, true);

    static Window Build(string title, string text, out StackPanel panel)
    {
        var w = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 480,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(body);
        w.Content = panel;
        return w;
    }

    static Task<bool> Show(Window owner, string title, string text, bool yesNo)
    {
        var w = Build(title, text, out var panel);
        panel.Children.Add(yesNo
            ? Ui.ButtonRow(Ui.Btn(L.T("Yes"), (_, _) => w.Close(true), true), Ui.Btn(L.T("No"), (_, _) => w.Close(false)))
            : Ui.ButtonRow(Ui.Btn("OK", (_, _) => w.Close(true), true)));
        return w.ShowDialog<bool>(owner);
    }

    /// <summary>Message without owner window (used before the main window exists).</summary>
    public static Task Standalone(string title, string text)
    {
        var tcs = new TaskCompletionSource();
        var w = Build(title, text, out var panel);
        w.ShowInTaskbar = true;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        panel.Children.Add(Ui.ButtonRow(Ui.Btn("OK", (_, _) => w.Close(), true)));
        w.Closed += (_, _) => tcs.TrySetResult();
        w.Show();
        return tcs.Task;
    }
}

/// <summary>Editor for a PingProfile. Empty box = inherit (unless requireAll).</summary>
public sealed class ProfileEditor : Grid
{
    readonly TextBox[] _boxes;
    readonly bool _requireAll;

    public ProfileEditor(bool requireAll)
    {
        _requireAll = requireAll;
        var fields = ProfileFields.All;
        ColumnDefinitions = new ColumnDefinitions("Auto,110,Auto");
        RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", fields.Length)));
        _boxes = new TextBox[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            var lbl = Ui.Label(fields[i].Label);
            Grid.SetRow(lbl, i);
            Grid.SetColumn(lbl, 0);
            var tb = new TextBox { Margin = new Thickness(0, 2) };
            Grid.SetRow(tb, i);
            Grid.SetColumn(tb, 1);
            var unit = Ui.Label(fields[i].Unit);
            unit.Margin = new Thickness(6, 4, 0, 4);
            Grid.SetRow(unit, i);
            Grid.SetColumn(unit, 2);
            Children.Add(lbl);
            Children.Add(tb);
            Children.Add(unit);
            _boxes[i] = tb;
        }
    }

    public void Load(PingProfile p)
    {
        for (int i = 0; i < _boxes.Length; i++)
            _boxes[i].Text = ProfileFields.All[i].Get(p)?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    public void SetInherited(Func<ProfileField, int>? inherited)
    {
        for (int i = 0; i < _boxes.Length; i++)
            _boxes[i].PlaceholderText = inherited is null ? "" : L.T("inherited: ") + inherited(ProfileFields.All[i]);
    }

    public bool TryRead(PingProfile target, out string error)
    {
        var tmp = new PingProfile();
        for (int i = 0; i < _boxes.Length; i++)
        {
            var f = ProfileFields.All[i];
            var s = (_boxes[i].Text ?? "").Trim();
            if (s.Length == 0)
            {
                if (_requireAll)
                {
                    error = L.F("\"{0}\": a value is required", f.Label);
                    return false;
                }
                f.Set(tmp, null);
                continue;
            }
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < f.Min || v > f.Max)
            {
                error = L.F("\"{0}\": allowed {1}..{2}", f.Label, f.Min, f.Max);
                return false;
            }
            f.Set(tmp, v);
        }
        foreach (var f in ProfileFields.All) f.Set(target, f.Get(tmp));
        error = "";
        return true;
    }
}

public sealed class DeviceDialog : Window
{
    readonly AppConfig _cfg;
    readonly Device? _dev;
    readonly TextBox _name = new(), _addr = new(), _group = new();
    readonly TextBox _desc = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
    readonly ComboBox _type = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _parent = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox _enabled = new() { Content = L.T("Monitoring enabled") };
    readonly CheckBox _sendAlerts = new() { Content = L.T("Send this device's events to channels (e-mail, webhooks)") };
    readonly CheckBox _playSounds = new() { Content = L.T("Play notification sounds for this device") };
    readonly ProfileEditor _prof = new(false);
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    readonly List<string?> _parentIds = new();
    readonly List<string> _typeNames = new();
    readonly List<ExtraCheck> _checks;
    readonly ComboBox _primaryKind = new() { Width = 260 };
    readonly Button _primaryEdit = new() { Content = L.T("Configure…") };
    readonly TextBlock _primaryInfo = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _profHeader = Ui.Header(L.T("Ping parameters (empty field = inherit from the type / global)"));
    ExtraCheck? _primary;
    int _primaryIndex;
    bool _primaryGuard;
    readonly ListBox _checkList = new() { Height = 110 };
    readonly List<DeviceLink> _links;
    readonly ListBox _linkList = new() { Height = 90 };

    public DeviceDialog(AppConfig cfg, string? deviceId, string? defaultParentId)
    {
        _cfg = cfg;
        _dev = deviceId is null ? null : cfg.Devices.FirstOrDefault(d => d.Id == deviceId);
        _checks = _dev is null ? new List<ExtraCheck>() : ConfigStore.Copy(_dev.Checks);
        _primary = _dev?.Primary is null ? null : ConfigStore.Copy(_dev.Primary);
        _links = _dev is null ? new List<DeviceLink>() : ConfigStore.Copy(_dev.Links);
        Title = _dev is null ? L.T("New device") : L.T("Device: ") + _dev.Name;
        Width = 680;
        Height = 780;
        MinWidth = 520;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _typeNames.Add(L.T("(no type)"));
        _typeNames.AddRange(cfg.Types.Select(t => t.Name));
        _type.ItemsSource = _typeNames.Select((n, i) => i == 0 ? n : AppConfig.TypeLabel(n)).ToList();

        var excluded = _dev is null ? new HashSet<string>() : Descendants(cfg, _dev.Id);
        if (_dev is not null) excluded.Add(_dev.Id);
        var parentNames = new List<string> { L.T("(none — root)") };
        _parentIds.Add(null);
        foreach (var d in cfg.Devices.Where(d => !excluded.Contains(d.Id)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            parentNames.Add($"{d.Name} ({d.Address})");
            _parentIds.Add(d.Id);
        }
        _parent.ItemsSource = parentNames;

        var src = _dev ?? new Device { ParentId = defaultParentId };
        _name.Text = src.Name;
        _addr.Text = src.Address;
        _group.Text = src.Group;
        _desc.Text = src.Description;
        _enabled.IsChecked = src.Enabled;
        _sendAlerts.IsChecked = src.SendAlerts;
        _playSounds.IsChecked = src.PlaySounds;
        var ti = _typeNames.FindIndex(1, n => string.Equals(n, src.Type, StringComparison.OrdinalIgnoreCase));
        _type.SelectedIndex = ti < 0 ? 0 : ti;
        var pi = _parentIds.IndexOf(src.ParentId);
        _parent.SelectedIndex = pi < 0 ? 0 : pi;
        _prof.Load(src.Profile);
        UpdateInherited();
        _type.SelectionChanged += (_, _) => UpdateInherited();

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
        };
        AddRow(form, 0, L.T("Name"), _name);
        AddRow(form, 1, L.T("IP address / host name"), _addr);
        AddRow(form, 2, L.T("Type"), _type);
        AddRow(form, 3, L.T("Parent device"), _parent);
        AddRow(form, 4, L.T("Group"), _group);
        AddRow(form, 5, L.T("Description (shown in the tooltip)"), _desc);
        Grid.SetRow(_enabled, 6);
        Grid.SetColumn(_enabled, 1);
        form.Children.Add(_enabled);
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_sendAlerts, 7);
        Grid.SetColumn(_sendAlerts, 1);
        form.Children.Add(_sendAlerts);
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_playSounds, 8);
        Grid.SetColumn(_playSounds, 1);
        form.Children.Add(_playSounds);

        var stack = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        stack.Children.Add(form);
        stack.Children.Add(Ui.Header(L.T("How to determine the device state")));
        var prow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        prow.Children.Add(_primaryKind);
        prow.Children.Add(_primaryEdit);
        stack.Children.Add(prow);
        stack.Children.Add(_primaryInfo);
        _primaryKind.ItemsSource = new[] { L.T("Ping (ICMP)"), L.T("TCP port (no ping)"), L.T("HTTP(S) request (no ping)"), L.T("DNS query (no ping)"), L.T("HTTP JSON value (no ping)") };
        _primaryIndex = _primary is null ? 0 : (int)_primary.Kind + 1;
        _primaryKind.SelectedIndex = _primaryIndex;
        _primaryKind.SelectionChanged += async (_, _) => await OnPrimaryKindChanged();
        _primaryEdit.Click += async (_, _) => await EditPrimary();
        stack.Children.Add(_profHeader);
        stack.Children.Add(_prof);
        UpdatePrimaryUi();

        stack.Children.Add(Ui.Header(L.T("Connections (right-click a device on the map or in the tree)")));
        stack.Children.Add(_linkList);
        var lbtn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (text, kind) in new[] { ("+ SSH", LinkKind.Ssh), (L.T("+ Web HTTP"), LinkKind.Http), (L.T("+ Web HTTPS"), LinkKind.Https) })
        {
            var b = new Button { Content = text };
            var k = kind;
            b.Click += async (_, _) =>
            {
                var l = new DeviceLink { Kind = k, Port = DeviceLink.DefaultPort(k) };
                if (await new LinkDialog(l, (_addr.Text ?? "").Trim()).ShowDialog<bool>(this))
                {
                    _links.Add(l);
                    RefreshLinks();
                }
            };
            lbtn.Children.Add(b);
        }
        var editL = new Button { Content = L.T("Edit") };
        var delL = new Button { Content = L.T("Remove") };
        editL.Click += async (_, _) =>
        {
            var i = _linkList.SelectedIndex;
            if (i < 0 || i >= _links.Count) return;
            var copy = ConfigStore.Copy(_links[i]);
            if (await new LinkDialog(copy, (_addr.Text ?? "").Trim()).ShowDialog<bool>(this))
            {
                _links[i] = copy;
                RefreshLinks();
            }
        };
        delL.Click += (_, _) =>
        {
            var i = _linkList.SelectedIndex;
            if (i < 0 || i >= _links.Count) return;
            _links.RemoveAt(i);
            RefreshLinks();
        };
        _linkList.DoubleTapped += (_, _) => editL.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        lbtn.Children.Add(editL);
        lbtn.Children.Add(delL);
        stack.Children.Add(lbtn);
        RefreshLinks();

        stack.Children.Add(Ui.Header(L.T("Additional checks (TCP port, HTTP(S), DNS)")));
        stack.Children.Add(_checkList);
        var cbtn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addC = new Button { Content = L.T("Add check") };
        var editC = new Button { Content = L.T("Edit") };
        var delC = new Button { Content = L.T("Remove") };
        addC.Click += async (_, _) =>
        {
            var c = new ExtraCheck();
            if (await new CheckDialog(c, (_addr.Text ?? "").Trim()).ShowDialog<bool>(this))
            {
                _checks.Add(c);
                RefreshChecks();
            }
        };
        editC.Click += async (_, _) =>
        {
            var i = _checkList.SelectedIndex;
            if (i < 0 || i >= _checks.Count) return;
            var copy = ConfigStore.Copy(_checks[i]);
            if (await new CheckDialog(copy, (_addr.Text ?? "").Trim()).ShowDialog<bool>(this))
            {
                _checks[i] = copy;
                RefreshChecks();
            }
        };
        delC.Click += (_, _) =>
        {
            var i = _checkList.SelectedIndex;
            if (i < 0 || i >= _checks.Count) return;
            _checks.RemoveAt(i);
            RefreshChecks();
        };
        _checkList.DoubleTapped += (_, _) => editC.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        cbtn.Children.Add(addC);
        cbtn.Children.Add(editC);
        cbtn.Children.Add(delC);
        stack.Children.Add(cbtn);
        RefreshChecks();

        var buttons = Ui.ButtonRow(Ui.Btn(L.T("Save"), (_, _) => Save(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false)));
        buttons.Margin = new Thickness(14, 6, 14, 14);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        _err.Margin = new Thickness(14, 10, 14, 0);
        _err.FontWeight = FontWeight.SemiBold;
        DockPanel.SetDock(_err, Dock.Top);
        dock.Children.Add(_err);
        dock.Children.Add(new ScrollViewer { Content = stack });
        Content = dock;
    }

    void UpdatePrimaryUi()
    {
        bool ping = _primary is null;
        _primaryEdit.IsVisible = !ping;
        _prof.IsEnabled = ping;
        _profHeader.Opacity = ping ? 1 : 0.5;
        _primaryInfo.Text = ping
            ? L.T("State and color are defined by ping. For devices that do not answer ping choose TCP, HTTP(S) or DNS — then no ping is sent at all.")
            : L.F("State and color are defined by: {0} (every {1} s). No ping is sent, the ping parameters below are not used. History, reports and availability are calculated from this check.", _primary!.Title(), _primary.IntervalSec);
    }

    async Task OnPrimaryKindChanged()
    {
        if (_primaryGuard) return;
        var idx = _primaryKind.SelectedIndex;
        if (idx == _primaryIndex) return;
        if (idx <= 0)
        {
            _primary = null;
            _primaryIndex = 0;
            UpdatePrimaryUi();
            return;
        }
        var c = _primary is null ? new ExtraCheck { IntervalSec = 10, TimeoutMs = 2000 } : ConfigStore.Copy(_primary);
        c.Kind = (CheckKind)(idx - 1);
        if (await new CheckDialog(c, (_addr.Text ?? "").Trim(), primary: true).ShowDialog<bool>(this))
        {
            _primary = c;
            _primaryIndex = (int)c.Kind + 1;
        }
        _primaryGuard = true;
        _primaryKind.SelectedIndex = _primaryIndex;
        _primaryGuard = false;
        UpdatePrimaryUi();
    }

    async Task EditPrimary()
    {
        if (_primary is null) return;
        var c = ConfigStore.Copy(_primary);
        if (!await new CheckDialog(c, (_addr.Text ?? "").Trim(), primary: true).ShowDialog<bool>(this)) return;
        _primary = c;
        _primaryIndex = (int)c.Kind + 1;
        _primaryGuard = true;
        _primaryKind.SelectedIndex = _primaryIndex;
        _primaryGuard = false;
        UpdatePrimaryUi();
    }

    void RefreshLinks()
    {
        var addr = (_addr.Text ?? "").Trim();
        _linkList.ItemsSource = _links.Count == 0
            ? new List<string> { L.T("(none — the device context menu will have no connections)") }
            : _links.Select(l => l.Title(addr.Length > 0 ? addr : L.T("address"))).ToList();
    }

    void RefreshChecks()
    {
        _checkList.ItemsSource = _checks.Count == 0
            ? new List<string> { L.T("(none — only ping is checked)") }
            : _checks.Select(c => L.F("{0}{1} · every {2} s", (c.Enabled ? "" : L.T("[off] ")), c.Title(), c.IntervalSec)).ToList();
    }

    static void AddRow(Grid g, int row, string label, Control c)
    {
        var l = Ui.Label(label);
        Grid.SetRow(l, row);
        Grid.SetColumn(l, 0);
        Grid.SetRow(c, row);
        Grid.SetColumn(c, 1);
        c.Margin = new Thickness(0, 2);
        g.Children.Add(l);
        g.Children.Add(c);
    }

    void UpdateInherited()
    {
        var typeName = _type.SelectedIndex > 0 ? _typeNames[_type.SelectedIndex] : "";
        var tp = ProfileResolver.TypeProfile(_cfg, typeName);
        _prof.SetInherited(f => ProfileResolver.Value(f, _cfg.Global, tp, null));
    }

    void Save()
    {
        var name = (_name.Text ?? "").Trim();
        var addr = (_addr.Text ?? "").Trim();
        if (name.Length == 0) { _err.Text = L.T("Enter a name"); return; }
        if (!AddressUtil.IsValid(addr)) { _err.Text = L.T("Invalid IPv4 address or host name"); return; }
        if (_cfg.Devices.Any(d => d != _dev && string.Equals(d.Address.Trim(), addr, StringComparison.OrdinalIgnoreCase)))
        {
            _err.Text = L.T("A device with this address already exists");
            return;
        }
        var prof = new PingProfile();
        if (!_prof.TryRead(prof, out var perr)) { _err.Text = perr; return; }

        var dev = _dev ?? new Device();
        dev.Name = name;
        dev.Address = addr;
        dev.Type = _type.SelectedIndex > 0 ? _typeNames[_type.SelectedIndex] : "";
        dev.ParentId = _parent.SelectedIndex > 0 ? _parentIds[_parent.SelectedIndex] : null;
        dev.Group = (_group.Text ?? "").Trim();
        dev.Description = (_desc.Text ?? "").Trim();
        dev.Enabled = _enabled.IsChecked == true;
        dev.SendAlerts = _sendAlerts.IsChecked == true;
        dev.PlaySounds = _playSounds.IsChecked == true;
        prof.Extra = _dev?.Profile.Extra;
        dev.Profile = prof;
        dev.Checks = _checks;
        dev.Links = _links;
        dev.Primary = _primary;
        if (_dev is null) _cfg.Devices.Add(dev);
        Close(true);
    }

    public static HashSet<string> Descendants(AppConfig cfg, string id)
    {
        var res = new HashSet<string>();
        var q = new Queue<string>();
        q.Enqueue(id);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var d in cfg.Devices)
                if (d.ParentId == cur && res.Add(d.Id)) q.Enqueue(d.Id);
        }
        return res;
    }
}

public sealed class SettingsDialog : Window
{
    readonly AppConfig _cfg;
    readonly AppCore _core;
    readonly ProfileEditor _global = new(true);
    readonly TextBox _retention = new() { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
    readonly CheckBox _suppress = new() { Content = L.T("When the parent is down, show children as \"Unreachable via parent\"") };
    readonly ListBox _types = new() { Width = 210 };
    readonly TextBox _typeName = new() { Width = 260 };
    readonly TextBlock _typeHint = new() { Opacity = 0.7 };
    readonly ProfileEditor _typeProf = new(false);
    readonly TextBox _curPw = Ui.PasswordBox();
    readonly TextBox _newPw1 = Ui.PasswordBox();
    readonly TextBox _newPw2 = Ui.PasswordBox();
    readonly RadioButton _aOpen = new() { GroupName = "settings-access", Content = L.T("No restrictions — anyone can change the list and settings") };
    readonly RadioButton _aView = new() { GroupName = "settings-access", Content = L.T("View mode; administrator mode — by button, no password") };
    readonly RadioButton _aPw = new() { GroupName = "settings-access", Content = L.T("View mode; administrator mode — with a password") };
    readonly CheckBox _allowReports = new() { Content = L.T("\"History and reports\" allowed in view mode (history, PDF and raw log)") };
    readonly CheckBox _allowLinks = new() { Content = L.T("Device connections allowed in view mode (SSH / web in the right-click menu)") };
    readonly TextBox _autoLock = new() { Width = 100 };
    readonly CheckBox _autostart = new() { Content = L.T("Start FreeMon at login (minimized to tray)") };
    readonly ComboBox _closeAction = new() { Width = 420 };
    readonly ComboBox _theme = new() { Width = 420 };
    readonly ComboBox _repLang = new() { Width = 280 };
    readonly ComboBox _mapOrigin = new() { Width = 420 };
    readonly CheckBox _lastEvent = new() { Content = L.T("Show the last event in the status bar when the \"Events\" panel is collapsed") };
    readonly Button _typeIcon = new() { Content = L.T("Choose icon…") };
    readonly TextBlock _typeIconName = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    string _typeIconKey = "lan";
    readonly StatusBadge _typeIconPreview = new(28) { VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _nDown = new() { Content = L.T("Notify about \"Down\"") };
    readonly CheckBox _nDeg = new() { Content = L.T("Notify about \"Unstable\"") };
    readonly CheckBox _nRec = new() { Content = L.T("Notify about restored connection") };
    readonly CheckBox _nSkip = new() { Content = L.T("Do not notify about child devices if their parent is unreachable") };
    readonly CheckBox _nSound = new() { Content = L.T("Sound on \"Down\" / \"Unstable\"") };
    readonly CheckBox _nSoundRec = new() { Content = L.T("Quiet sound when the connection is restored") };
    readonly TextBox _nSeconds = new() { Width = 100 };
    readonly CheckBox _nChannels = new()
    {
        Content = new TextBlock
        {
            Text = L.T("Send events to channels (e-mail, webhooks)"),
            FontWeight = FontWeight.SemiBold,
        },
    };
    readonly TextBox _repOrg = new() { Width = 420 };
    readonly TextBox _repAuthor = new() { Width = 420 };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0) };
    readonly bool _readOnly;
    readonly bool _autostartInitial;
    int _cur = -1;
    bool _guard;
    bool _saving;

    public string? NewPassword { get; private set; }
    public bool RemovePassword { get; private set; }
    public bool AutostartChanged { get; private set; }
    public bool AutostartValue { get; private set; }
    /// <summary>Secrets of all channels after "Зберегти" (null = settings opened read-only / not saved).</summary>
    public Dictionary<string, ChannelSecret>? ChannelSecrets { get; private set; }
    readonly List<AlertChannel> _channels = new();
    readonly Dictionary<string, ChannelSecret> _secrets = new();
    readonly ListBox _chList = new() { Height = 220 };

    public SettingsDialog(AppConfig cfg, AppCore core, bool readOnly)
    {
        _cfg = cfg;
        _core = core;
        _readOnly = readOnly;
        Title = L.T("Settings");
        Width = 900;
        Height = 760;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _retention.Text = cfg.RetentionDays.ToString(CultureInfo.InvariantCulture);
        _suppress.IsChecked = cfg.SuppressChildrenWhenParentDown;
        _autoLock.Text = cfg.AutoLockMinutes.ToString(CultureInfo.InvariantCulture);
        _global.Load(cfg.Global);
        _autostartInitial = Autostart.IsEnabled();
        _autostart.IsChecked = _autostartInitial;
        _closeAction.ItemsSource = new[]
        {
            CloseBehavior.IsGnome ? L.T("Automatic (minimize: GNOME has no tray)") : L.T("Automatic (hide to tray)"),
            L.T("Hide to tray"),
            L.T("Minimize to taskbar"),
        };
        _theme.ItemsSource = new[] { L.T("As in the system"), L.T("Light"), L.T("Dark") };
        _theme.SelectedIndex = (int)cfg.Theme;
        _mapOrigin.ItemsSource = new[] { L.T("Top left corner"), L.T("Top center"), L.T("Window center") };
        _mapOrigin.SelectedIndex = (int)cfg.MapOrigin;
        _lastEvent.IsChecked = cfg.ShowLastEventInStatusBar;
        _closeAction.SelectedIndex = (int)cfg.CloseAction;
        _nDown.IsChecked = cfg.Notify.OnDown;
        _nDeg.IsChecked = cfg.Notify.OnDegraded;
        _nRec.IsChecked = cfg.Notify.OnRecovery;
        _nSkip.IsChecked = cfg.Notify.SkipUnreachableChildren;
        _nSound.IsChecked = cfg.Notify.Sound;
        _nSoundRec.IsChecked = cfg.Notify.SoundRecovery;
        _nSeconds.Text = cfg.Notify.PopupSeconds.ToString(CultureInfo.InvariantCulture);
        _nChannels.IsChecked = cfg.Notify.ChannelsEnabled;
        _repOrg.Text = cfg.Report.Organization;
        _repAuthor.Text = cfg.Report.PreparedBy;
        _typeIcon.Click += async (_, _) =>
        {
            var picked = await new IconPickerDialog(_typeIconKey).ShowDialog<string?>(this);
            if (picked is null) return;
            _typeIconKey = picked;
            _typeIconPreview.Set(DevStatus.Up, picked);
            _typeIconName.Text = MdiIcons.Title(picked);
        };

        // --- general
        var general = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        general.Children.Add(Ui.Row(L.T("Keep the check log, days"), _retention));
        general.Children.Add(_suppress);
        general.Children.Add(Ui.Row(L.T("Interface theme"), _theme, 200));
        general.Children.Add(Ui.Row(L.T("Map layout origin"), _mapOrigin, 200));
        general.Children.Add(Ui.Row(L.T("Window \"Close\" button"), _closeAction, 200));
        general.Children.Add(_lastEvent);
        general.Children.Add(Ui.Hint(L.T("Monitoring keeps running. Starting FreeMon again opens the window of the running instance.")));
        general.Children.Add(_autostart);
        general.Children.Add(Ui.Hint(L.T("Autostart is configured for the current user: ") + Autostart.Where));
        general.Children.Add(Ui.Header(L.T("Global ping parameters (apply unless overridden in the type or device)")));
        general.Children.Add(_global);
        general.Children.Add(Ui.Hint(L.T("0 in the \"Unstable\" thresholds = threshold disabled. The state changes only after N consecutive checks with the new result.")));

        // --- types
        var tbtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tbtns.Children.Add(Ui.Btn(L.T("Add"), (_, _) => AddType()));
        tbtns.Children.Add(Ui.Btn(L.T("Delete"), (_, _) => _ = DeleteType()));
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(tbtns, Dock.Bottom);
        left.Children.Add(tbtns);
        left.Children.Add(_types);

        var right = new StackPanel { Spacing = 6 };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(Ui.Label(L.T("Type name")));
        nameRow.Children.Add(_typeName);
        right.Children.Add(nameRow);
        right.Children.Add(_typeHint);
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        iconRow.Children.Add(Ui.Label(L.T("Icon")));
        iconRow.Children.Add(_typeIconPreview);
        iconRow.Children.Add(_typeIconName);
        iconRow.Children.Add(_typeIcon);
        right.Children.Add(iconRow);
        right.Children.Add(Ui.Header(L.T("Type parameters (empty = inherit global)")));
        right.Children.Add(_typeProf);

        var typesPanel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(left, Dock.Left);
        typesPanel.Children.Add(left);
        typesPanel.Children.Add(new ScrollViewer { Content = right });

        // --- security
        bool prot = cfg.Security is not null;
        _aOpen.IsChecked = !prot && !cfg.ViewMode;
        _aView.IsChecked = !prot && cfg.ViewMode;
        _aPw.IsChecked = prot;
        var security = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        security.Children.Add(EncryptionNoticeWindow.Link(() => this));
        security.Children.Add(Ui.Hint(
            L.T("In view mode the state, log and reports are visible, while changing the list and settings or exiting the program is possible only in administrator mode. Without a password this protects against accidental changes, with a password — against outsiders.")));
        security.Children.Add(_aOpen);
        security.Children.Add(_aView);
        security.Children.Add(_aPw);
        _allowReports.IsChecked = cfg.AllowReportsInViewMode;
        security.Children.Add(_allowReports);
        _allowLinks.IsChecked = cfg.AllowLinksInViewMode;
        security.Children.Add(_allowLinks);
        if (prot) security.Children.Add(Ui.Row(L.T("Current password (to change or remove the password)"), _curPw));
        security.Children.Add(Ui.Row(prot ? L.T("New password (empty — do not change)") : L.T("Password (at least 6 characters)"), _newPw1));
        security.Children.Add(Ui.Row(L.T("Repeat password"), _newPw2));
        security.Children.Add(Ui.Row(L.T("Auto-lock after inactivity, min (0 = off)"), _autoLock));
        security.Children.Add(Ui.Hint(
            L.T("The password protects against changes through the program interface. If the configuration file is changed outside the program, this is detected at the next administrator login — with a list of changes and an option to roll back.")));

        // --- notifications
        var notify = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        notify.Children.Add(Ui.Hint(L.T("A popup in the bottom right corner of the screen. Changes that happen almost at the same time are shown in one popup. The state at program start is not notified.")));
        notify.Children.Add(_nDown);
        notify.Children.Add(_nDeg);
        notify.Children.Add(_nRec);
        notify.Children.Add(_nSkip);
        var soundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        soundRow.Children.Add(_nSound);
        foreach (var (label, kind) in new[] { (L.T("Down"), SoundKind.Down), (L.T("Unstable"), SoundKind.Degraded), (L.T("Restored"), SoundKind.Recovery) })
        {
            var k = kind;
            var b = new Button { Content = "▶ " + label, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(b, L.T("Listen"));
            b.Click += (_, _) => Sound.Play(k);
            soundRow.Children.Add(b);
        }
        notify.Children.Add(soundRow);
        notify.Children.Add(_nSoundRec);
        notify.Children.Add(Ui.Row(L.T("Close notifications after, s (0 = manually only)"), _nSeconds));

        notify.Children.Add(Ui.Header(L.T("Channels: e-mail and webhooks")));
        notify.Children.Add(_nChannels);
        notify.Children.Add(new TextBlock
        {
            Text = L.T("Master switch for all channels at once — the channels and their settings are kept. A single channel can be turned off on the \"Channels\" tab, and sending events of a specific device — with the \"Send this device's events to channels\" box in its settings."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(28, 0, 0, 0),
        });
        notify.Children.Add(new Border
        {
            BorderBrush = Icons.Brush(DevStatus.Degraded),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(28, 6, 0, 0),
            Child = new TextBlock
            {
                Text = L.T("Data leaves the network. If a channel points to a public service (Telegram, public ntfy, external e-mail, etc.), device names, IP addresses and the state of your network are sent to and stored on third-party servers. For internal information prefer your own servers: corporate e-mail, Virola, Mattermost, ntfy or Gotify in your network."),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        // --- reports
        var reports = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        reports.Children.Add(Ui.Hint(L.T("Default values for PDF reports. In the \"History and reports\" window each user can change them — the last entered value is remembered for that user.")));
        reports.Children.Add(Ui.Row(L.T("Organization / department (report header)"), _repOrg, 280));
        reports.Children.Add(Ui.Row(L.T("Prepared by"), _repAuthor, 280));
        _repLang.ItemsSource = new[] { L.T("Same as the user's interface language") }.Concat(L.Languages.Select(x => x.Name)).ToList();
        _repLang.SelectedIndex = string.IsNullOrEmpty(cfg.Report.Language) ? 0 : Array.FindIndex(L.Languages, x => x.Code == cfg.Report.Language) + 1;
        reports.Children.Add(Ui.Row(L.T("Report language (PDF, raw log)"), _repLang, 280));
        reports.Children.Add(Ui.Hint(L.T("Additional checks (TCP, HTTP(S), DNS) are not included in the PDF report: they are in the log and in the raw TXT log. The report calculates availability from the device's main check.")));

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = L.T("General"), Content = new ScrollViewer { Content = general } });
        tabs.Items.Add(new TabItem { Header = L.T("Device types"), Content = typesPanel });
        tabs.Items.Add(new TabItem { Header = L.T("Notifications"), Content = new ScrollViewer { Content = notify } });
        tabs.Items.Add(new TabItem { Header = L.T("Reports"), Content = new ScrollViewer { Content = reports } });
        if (!readOnly)
        {
            _channels.AddRange(ConfigStore.Copy(cfg.Channels));
            foreach (var (k, v) in SecretStore.Load()) _secrets[k] = v;
            var chPanel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
            chPanel.Children.Add(Ui.Hint(L.T("Where to send state changes: e-mail and webhooks (Telegram, Virola, Node-RED, ntfy, Gotify, Mattermost / Slack…). Webhook addresses, headers with tokens and SMTP passwords are stored separately, encrypted.")));
            chPanel.Children.Add(_chList);
            var chBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var addCh = new Button { Content = L.T("Add channel") };
            var editCh = new Button { Content = L.T("Edit") };
            var delCh = new Button { Content = L.T("Delete") };
            addCh.Click += async (_, _) =>
            {
                var c = new AlertChannel { Name = "" };
                var s = new ChannelSecret();
                if (await new ChannelDialog(_cfg, c, s).ShowDialog<bool>(this))
                {
                    _channels.Add(c);
                    _secrets[c.Id] = s;
                    RefreshChannels();
                }
            };
            editCh.Click += async (_, _) =>
            {
                var i = _chList.SelectedIndex;
                if (i < 0 || i >= _channels.Count) return;
                var c = ConfigStore.Copy(_channels[i]);
                var s = _secrets.TryGetValue(c.Id, out var old) ? ConfigStore.Copy(old) : new ChannelSecret();
                if (await new ChannelDialog(_cfg, c, s).ShowDialog<bool>(this))
                {
                    _channels[i] = c;
                    _secrets[c.Id] = s;
                    RefreshChannels();
                }
            };
            delCh.Click += async (_, _) =>
            {
                var i = _chList.SelectedIndex;
                if (i < 0 || i >= _channels.Count) return;
                if (!await Dialogs.Confirm(this, L.T("Delete channel"), L.F("Delete the channel \"{0}\" together with its address and tokens?", _channels[i].Name))) return;
                _secrets.Remove(_channels[i].Id);
                _channels.RemoveAt(i);
                RefreshChannels();
            };
            _chList.DoubleTapped += (_, _) => editCh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            chBtns.Children.Add(addCh);
            chBtns.Children.Add(editCh);
            chBtns.Children.Add(delCh);
            chPanel.Children.Add(chBtns);
            RefreshChannels();
            tabs.Items.Add(new TabItem { Header = L.T("Channels"), Content = new ScrollViewer { Content = chPanel } });
            tabs.Items.Add(new TabItem { Header = L.T("Backups"), Content = new ScrollViewer { Content = BuildBackupTab(cfg) } });
        }
        if (!readOnly)
            tabs.Items.Add(new TabItem { Header = L.T("Security"), Content = new ScrollViewer { Content = security } });

        // personal settings: the only tab in view mode, the last one for the administrator
        var personalTab = new TabItem { Header = L.T("Personal"), Content = new ScrollViewer { Content = BuildPersonalTab(cfg) } };
        if (readOnly) tabs.Items.Clear();
        tabs.Items.Add(personalTab);
        if (readOnly) tabs.SelectedIndex = 0;

        var buttons = Ui.ButtonRow(Ui.Btn(L.T("Save"), async (_, _) => await SaveAsync(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false)));
        buttons.Margin = new Thickness(14, 6, 14, 14);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_err, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_err);
        root.Children.Add(tabs);
        Content = root;

        _typeProf.SetInherited(f => ProfileResolver.Value(f, _cfg.Global, null, null));
        _types.SelectionChanged += (_, _) => OnTypeSelected();
        var first = _cfg.Types.Count > 0 ? 0 : -1;
        RefreshTypeList(first);
        LoadType(first);
    }

    readonly CheckBox _bAuto = new() { Content = L.T("Make automatic backups") };
    readonly ComboBox _bInterval = new() { Width = 200 };
    readonly TextBox _bFolder = new() { Width = 420, PlaceholderText = L.T("folder for backups (better on another disk or a network share)") };
    readonly TextBox _bKeep = new() { Width = 80 };
    readonly CheckBox _bLog = new() { Content = L.T("Include the check log") };
    readonly TextBlock _bKeyState = new() { TextWrapping = TextWrapping.Wrap };
    string _bPub = "", _bPriv = "";

    Control BuildBackupTab(AppConfig cfg)
    {
        var b = cfg.Backup;
        _bAuto.IsChecked = b.AutoEnabled;
        _bInterval.ItemsSource = new[] { L.T("daily"), L.T("weekly") };
        _bInterval.SelectedIndex = b.IntervalHours == 168 ? 1 : 0;
        _bFolder.Text = b.Folder;
        _bKeep.Text = b.Keep.ToString(CultureInfo.InvariantCulture);
        _bLog.IsChecked = b.IncludeLog;
        _bPub = b.PublicKey;
        _bPriv = b.EncryptedPrivateKey;
        UpdateKeyState();

        var p = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        p.Children.Add(EncryptionNoticeWindow.Link(() => this));
        p.Children.Add(new Border
        {
            BorderBrush = Icons.Brush(DevStatus.Degraded),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 6),
            Child = new TextBlock
            {
                Text = L.T("Remember to make backups. The data is encrypted with this computer's key: after reinstalling the OS, replacing the computer or a disk failure it cannot be recovered without a backup."),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        p.Children.Add(Ui.Header(L.T("Manual backup")));
        p.Children.Add(Ui.Hint(L.T("Contains the saved settings, lists, channel secrets and, optionally, the log. Encrypted with the backup password.")));
        var mk = new Button { Content = L.T("Create backup…") };
        mk.Click += async (_, _) => await CreateManualBackup();
        var rs = new Button { Content = L.T("Restore from backup…") };
        rs.Click += async (_, _) => await RestoreBackup();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(mk);
        row.Children.Add(rs);
        p.Children.Add(row);

        p.Children.Add(Ui.Header(L.T("Automatic backups")));
        p.Children.Add(Ui.Hint(L.T("Made on schedule without entering a password; they can be opened only with the automatic backup password. Backups beyond the given number are deleted.")));
        p.Children.Add(_bAuto);
        p.Children.Add(Ui.Row(L.T("How often"), _bInterval, 200));
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        folderRow.Children.Add(_bFolder);
        var pick = new Button { Content = L.T("Browse…") };
        pick.Click += async (_, _) =>
        {
            var f = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Folder for backups"), AllowMultiple = false });
            if (f.Count > 0 && f[0].TryGetLocalPath() is { } lp) _bFolder.Text = lp;
        };
        folderRow.Children.Add(pick);
        p.Children.Add(Ui.Row(L.T("Folder"), folderRow, 200));
        p.Children.Add(Ui.Row(L.T("Keep last backups"), _bKeep, 200));
        p.Children.Add(_bLog);
        var keyBtn = new Button { Content = L.T("Set the automatic backup password…") };
        keyBtn.Click += async (_, _) =>
        {
            var d = new BackupPasswordDialog(L.T("Automatic backup password"), true, false);
            if (!await d.ShowDialog<bool>(this)) return;
            var pw = d.Password;
            (_bPub, _bPriv) = await Task.Run(() => BackupService.CreateKeyPair(pw));
            UpdateKeyState();
        };
        p.Children.Add(keyBtn);
        p.Children.Add(_bKeyState);
        if (BackupService.LastAutoBackup() is { } last)
            p.Children.Add(Ui.Hint(L.F("Last automatic backup: {0:dd.MM.yyyy HH:mm}", last)));
        return p;
    }

    void UpdateKeyState() => _bKeyState.Text = string.IsNullOrEmpty(_bPub)
        ? L.T("The automatic backup password is not set — automatic backups are not made.")
        : L.T("The automatic backup password is set. A new password applies to the next backups; older ones open with the old password.");

    bool ReadBackup(out string error)
    {
        error = "";
        if (!int.TryParse((_bKeep.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep) || keep < 1 || keep > 365)
        { error = L.T("Backups: number of backups 1..365"); return false; }
        bool auto = _bAuto.IsChecked == true;
        var folder = (_bFolder.Text ?? "").Trim();
        if (auto && folder.Length == 0) { error = L.T("Backups: specify the folder for automatic backups"); return false; }
        if (auto && string.IsNullOrEmpty(_bPub)) { error = L.T("Backups: set the automatic backup password"); return false; }
        _cfg.Backup = new BackupSettings
        {
            Extra = _cfg.Backup.Extra,
            AutoEnabled = auto,
            IntervalHours = _bInterval.SelectedIndex == 1 ? 168 : 24,
            Folder = folder,
            Keep = keep,
            IncludeLog = _bLog.IsChecked == true,
            PublicKey = _bPub,
            EncryptedPrivateKey = _bPriv,
        };
        return true;
    }

    Task CreateManualBackup() => BackupUi.CreateManualAsync(this, _core, m => _err.Text = m);

    Task RestoreBackup() => BackupUi.RestoreAsync(this, m => _err.Text = m);

    readonly ComboBox _pLang = new() { Width = 420 };
    readonly ComboBox _pWebMode = new() { Width = 420 };
    readonly ComboBox _pFound = new() { Width = 420 };
    readonly TextBox _pWebCmd = new() { Width = 420, PlaceholderText = L.T("for example: firefox -P Work --new-window {url}") };
    readonly ComboBox _pSshMode = new() { Width = 420 };
    readonly TextBox _pSshCmd = new() { Width = 420, PlaceholderText = L.T("for example: konsole -e ssh -p {port} {target}") };
    List<(string Name, string Command)> _browsers = new();

    /// <summary>Settings of the current user on this computer (ui.json): language, browser, SSH client.</summary>
    Control BuildPersonalTab(AppConfig cfg)
    {
        var ui = UiStateStore.Current;
        _browsers = Connector.FindBrowsers();
        _pLang.ItemsSource = new[] { L.T("As in the system") }.Concat(L.Languages.Select(x => x.Name)).ToList();
        _pLang.SelectedIndex = string.IsNullOrEmpty(ui.Language) ? 0 : Array.FindIndex(L.Languages, x => x.Code == ui.Language) + 1;
        _pWebMode.ItemsSource = new[] { L.T("Default browser"), L.T("Selected browser or custom command") };
        _pWebMode.SelectedIndex = ui.WebMode == 1 ? 1 : 0;
        _pWebCmd.Text = ui.WebCommand;
        _pFound.ItemsSource = _browsers.Count == 0
            ? new List<string> { L.T("(no browsers found — enter the command manually)") }
            : new[] { L.T("— insert a found browser —") }.Concat(_browsers.Select(b => b.Name)).ToList();
        _pFound.SelectedIndex = 0;
        _pFound.SelectionChanged += (_, _) =>
        {
            var i = _pFound.SelectedIndex - 1;
            if (i < 0 || i >= _browsers.Count) return;
            _pWebCmd.Text = _browsers[i].Command;
            _pWebMode.SelectedIndex = 1;
        };
        _pSshMode.ItemsSource = new[]
        {
            OperatingSystem.IsWindows() ? L.T("New console window with ssh (Windows OpenSSH client)") : L.T("System terminal (detect automatically)"),
            L.T("System ssh:// link handler"),
            L.T("Custom command"),
        };
        _pSshMode.SelectedIndex = (int)(ui.SshMode ?? cfg.SshMode);
        _pSshCmd.Text = ui.SshCommand ?? cfg.SshCommand;

        var p = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        p.Children.Add(Ui.Hint(Paths.Portable
            ? L.F("These settings are stored in {0} — in portable mode they are shared by everyone who runs this copy of FreeMon.", UiStateStore.FilePath)
            : L.F("These settings belong to you only (your account on this computer) and are stored in {0}. Other users have their own.", UiStateStore.FilePath)));
        p.Children.Add(Ui.Header(L.T("Interface language")));
        p.Children.Add(Ui.Row(L.T("Language"), _pLang, 200));
        p.Children.Add(Ui.Header(L.T("Web (HTTP / HTTPS)")));
        p.Children.Add(Ui.Row(L.T("Open with"), _pWebMode, 200));
        p.Children.Add(Ui.Row(L.T("Browsers found"), _pFound, 200));
        p.Children.Add(Ui.Row(L.T("Command"), _pWebCmd, 200));
        p.Children.Add(Ui.Hint(L.T("{url} — page address. A separate browser profile for network work: Firefox — -P \"Profile name\", Chrome / Edge / Brave — --profile-directory=\"Profile 1\".")));
        p.Children.Add(Ui.Header("SSH"));
        p.Children.Add(Ui.Row(L.T("Open with"), _pSshMode, 200));
        p.Children.Add(Ui.Row(L.T("Custom command"), _pSshCmd, 200));
        p.Children.Add(Ui.Hint(L.T("Placeholders: {host}, {port}, {user}, {target} (= user@address). Example for PuTTY: putty.exe -ssh {target} -P {port}")));
        return p;
    }

    /// <summary>Saves the personal tab; returns true if the interface language changed.</summary>
    bool SavePersonal()
    {
        var ui = UiStateStore.Current;
        var newLang = _pLang.SelectedIndex > 0 ? L.Languages[_pLang.SelectedIndex - 1].Code : "";
        bool changed = newLang != ui.Language;
        ui.Language = newLang;
        ui.WebMode = _pWebMode.SelectedIndex == 1 ? 1 : 0;
        ui.WebCommand = (_pWebCmd.Text ?? "").Trim();
        ui.SshMode = _pSshMode.SelectedIndex is >= 0 and <= 2 ? (SshMode)_pSshMode.SelectedIndex : SshMode.Terminal;
        ui.SshCommand = (_pSshCmd.Text ?? "").Trim();
        UiStateStore.Save();
        return changed;
    }

    void RefreshChannels()
    {
        _chList.ItemsSource = _channels.Count == 0
            ? new List<string> { L.T("(no channels — events are shown only in the program)") }
            : _channels.Select(c => $"{(c.Enabled ? "✓" : "✗")}  {c.Name}  —  {(c.Kind == ChannelKind.Email ? "Email: " + c.MailTo : L.T("Webhook: ") + (string.IsNullOrEmpty(c.Preset) ? L.T("custom") : c.Preset))}").ToList();
    }

    void RefreshTypeList(int select)
    {
        _guard = true;
        _types.ItemsSource = _cfg.Types.Select(t => AppConfig.TypeLabel(t.Name)).ToList();
        _types.SelectedIndex = select;
        _guard = false;
    }

    void LoadType(int i)
    {
        _cur = i;
        bool has = i >= 0 && i < _cfg.Types.Count;
        bool builtIn = has && AppConfig.IsBuiltInType(_cfg.Types[i].Name);
        _typeName.IsEnabled = has && !builtIn;
        _typeProf.IsEnabled = has;
        _typeName.Text = has ? AppConfig.TypeLabel(_cfg.Types[i].Name) : "";
        _typeHint.Text = builtIn ? L.T("Built-in type: the name cannot be changed and the type cannot be removed; parameters can be changed.") : "";
        _typeProf.Load(has ? _cfg.Types[i].Profile : new PingProfile());
        _typeIcon.IsEnabled = has;
        var key = has
            ? (string.IsNullOrWhiteSpace(_cfg.Types[i].Icon) ? MdiIcons.DefaultKeyForType(_cfg.Types[i].Name) : _cfg.Types[i].Icon)
            : "lan";
        _typeIconKey = MdiIcons.Exists(key) ? key : "lan";
        _typeIconPreview.Set(DevStatus.Up, _typeIconKey);
        _typeIconName.Text = MdiIcons.Title(_typeIconKey);
    }

    bool CommitType()
    {
        if (_readOnly || _cur < 0 || _cur >= _cfg.Types.Count) return true;
        var t = _cfg.Types[_cur];
        var name = AppConfig.IsBuiltInType(t.Name) ? t.Name : (_typeName.Text ?? "").Trim();
        if (name.Length == 0) { _err.Text = L.T("The type name cannot be empty"); return false; }
        if (_cfg.Types.Where((_, i) => i != _cur).Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _err.Text = L.F("Type \"{0}\" already exists", name);
            return false;
        }
        var p = new PingProfile();
        if (!_typeProf.TryRead(p, out var e)) { _err.Text = L.F("Type \"{0}\": {1}", t.Name, e); return false; }
        if (!string.Equals(t.Name, name, StringComparison.Ordinal))
            foreach (var d in _cfg.Devices.Where(d => string.Equals(d.Type, t.Name, StringComparison.OrdinalIgnoreCase)))
                d.Type = name;
        p.Extra = t.Profile.Extra;
        t.Name = name;
        t.Profile = p;
        t.Icon = _typeIconKey == MdiIcons.DefaultKeyForType(t.Name) ? "" : _typeIconKey;
        _err.Text = "";
        return true;
    }

    void OnTypeSelected()
    {
        if (_guard) return;
        var next = _types.SelectedIndex;
        if (next == _cur) return;
        if (!CommitType())
        {
            var back = _cur;
            Dispatcher.UIThread.Post(() => RefreshTypeList(back));
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            RefreshTypeList(next);
            LoadType(next);
        });
    }

    void AddType()
    {
        if (!CommitType()) return;
        int n = 1;
        string name;
        do name = L.F("New type {0}", n++);
        while (_cfg.Types.Any(t => t.Name == name));
        var other = _cfg.Types.FindIndex(t => string.Equals(t.Name, AppConfig.OtherType, StringComparison.OrdinalIgnoreCase));
        var i = other < 0 ? _cfg.Types.Count : other;
        _cfg.Types.Insert(i, new DeviceType { Name = name });
        RefreshTypeList(i);
        LoadType(i);
    }

    async Task DeleteType()
    {
        if (_cur < 0 || _cur >= _cfg.Types.Count) return;
        var t = _cfg.Types[_cur];
        if (AppConfig.IsBuiltInType(t.Name))
        {
            _err.Text = L.F("\"{0}\" is a built-in type and cannot be removed", AppConfig.TypeLabel(t.Name));
            return;
        }
        var used = _cfg.Devices.Count(d => string.Equals(d.Type, t.Name, StringComparison.OrdinalIgnoreCase));
        if (used > 0)
        {
            _err.Text = L.F("Type \"{0}\" is used by devices ({1}) — change their type first", t.Name, used);
            return;
        }
        if (!await Dialogs.Confirm(this, L.T("Delete type"), L.F("Remove the type \"{0}\" from the list of types?", t.Name))) return;
        _cfg.Types.RemoveAt(_cur);
        var i = Math.Min(_cur, _cfg.Types.Count - 1);
        _cur = -1;
        RefreshTypeList(i);
        LoadType(i);
    }

    async Task SaveAsync()
    {
        if (_saving) return;
        bool langChanged = SavePersonal();
        if (langChanged)
            await Dialogs.Info(this, L.T("Interface language"), L.T("The interface language will change after FreeMon is restarted."));
        if (_readOnly)
        {
            Close(false);
            return;
        }
        if (!CommitType()) return;
        if (!int.TryParse((_retention.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 3650)
        {
            _err.Text = L.T("Log retention: 1..3650 days");
            return;
        }
        if (!int.TryParse((_autoLock.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lockMin) || lockMin < 0 || lockMin > 1440)
        {
            _err.Text = L.T("Auto-lock: 0..1440 min");
            return;
        }
        var g = new PingProfile();
        if (!_global.TryRead(g, out var e)) { _err.Text = L.T("Global: ") + e; return; }
        if (!int.TryParse((_nSeconds.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var popupSec) || popupSec < 0 || popupSec > 3600)
        {
            _err.Text = L.T("Notification display time: 0..3600 s");
            return;
        }

        var n1 = _newPw1.Text ?? "";
        var n2 = _newPw2.Text ?? "";
        bool hadPw = _cfg.Security is not null;
        bool usePw = _aPw.IsChecked == true;
        bool wantRemove = hadPw && !usePw;
        bool wantNew = usePw && (!hadPw || n1.Length > 0 || n2.Length > 0);
        if (wantNew)
        {
            if (n1.Length < PasswordCrypto.MinLength) { _err.Text = L.F("The password must be at least {0} characters long", PasswordCrypto.MinLength); return; }
            if (n1 != n2) { _err.Text = L.T("Passwords do not match"); return; }
        }
        if ((wantNew || wantRemove) && _cfg.Security is not null)
        {
            _saving = true;
            _err.Text = L.T("Checking the current password…");
            var ok = await _core.VerifyPasswordAsync(_curPw.Text ?? "");
            _saving = false;
            if (!ok) { _err.Text = L.T("The current password is wrong"); return; }
        }

        _cfg.ViewMode = _aView.IsChecked == true || usePw;
        _cfg.AllowReportsInViewMode = _allowReports.IsChecked == true;
        _cfg.AllowLinksInViewMode = _allowLinks.IsChecked == true;
        g.Extra = _cfg.Global.Extra;
        _cfg.Global = g;
        _cfg.RetentionDays = days;
        _cfg.AutoLockMinutes = lockMin;
        _cfg.SuppressChildrenWhenParentDown = _suppress.IsChecked == true;
        _cfg.Notify = new NotifySettings
        {
            Extra = _cfg.Notify.Extra,
            OnDown = _nDown.IsChecked == true,
            OnDegraded = _nDeg.IsChecked == true,
            OnRecovery = _nRec.IsChecked == true,
            SkipUnreachableChildren = _nSkip.IsChecked == true,
            Sound = _nSound.IsChecked == true,
            SoundRecovery = _nSoundRec.IsChecked == true,
            PopupSeconds = popupSec,
            ChannelsEnabled = _nChannels.IsChecked == true,
        };
        if (!_readOnly)
        {
            if (!ReadBackup(out var berr)) { _err.Text = berr; return; }
            _cfg.Channels = _channels;
            ChannelSecrets = _channels.ToDictionary(c => c.Id, c => _secrets.TryGetValue(c.Id, out var s) ? s : new ChannelSecret());
        }
        _cfg.Report = new ReportSettings
        {
            Language = _repLang.SelectedIndex > 0 ? L.Languages[_repLang.SelectedIndex - 1].Code : "",
            Extra = _cfg.Report.Extra,
            Organization = (_repOrg.Text ?? "").Trim(),
            PreparedBy = (_repAuthor.Text ?? "").Trim(),
        };
        _cfg.CloseAction = _closeAction.SelectedIndex is >= 0 and <= 2 ? (CloseAction)_closeAction.SelectedIndex : CloseAction.Auto;
        _cfg.Theme = _theme.SelectedIndex is >= 0 and <= 2 ? (UiTheme)_theme.SelectedIndex : UiTheme.Auto;
        _cfg.MapOrigin = _mapOrigin.SelectedIndex is >= 0 and <= 2 ? (MapOrigin)_mapOrigin.SelectedIndex : MapOrigin.TopLeft;
        _cfg.ShowLastEventInStatusBar = _lastEvent.IsChecked == true;
        AutostartValue = _autostart.IsChecked == true;
        AutostartChanged = AutostartValue != _autostartInitial;
        NewPassword = wantNew ? n1 : null;
        RemovePassword = wantRemove;
        Close(true);
    }
}
