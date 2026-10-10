using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace FreeMon;

public sealed class MainWindow : Window
{
    public const string Cols = "26,230,150,90,70,190,*";

    readonly AppCore _core;
    readonly TreeView _tree = new();
    readonly Dictionary<string, RowView> _rows = new();
    readonly List<Button> _adminButtons = new();
    readonly List<StackPanel> _groups = new();
    readonly List<Border> _groupSeps = new();
    Button? _historyBtn;
    readonly Button _lockBtn = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly CountersView _counters = new() { Margin = new Thickness(12, 0, 0, 0) };
    readonly MapView _map = new();
    readonly Dictionary<string, Device> _devById = new();
    readonly TextBlock _warnText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    readonly Border _warn;
    readonly ListBox _events = new() { Height = 150 };
    readonly DockPanel _statusBar = new() { Margin = new Thickness(8, 4) };
    readonly TextBlock _statusLeft = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _statusRight = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
    TextBlock? _folderLink;
    TextBlock? _folderSep;
    bool _eventsExpanded = true;
    PathIcon? _eventsChevron;
    readonly DispatcherTimer _timer;
    int _eventsVersion = -1;
    int _busy;

    public MainWindow(AppCore core)
    {
        _core = core;
        Title = L.T("FreeMon — availability monitoring");
        Width = 1180;
        Height = 760;
        MinWidth = 760;
        MinHeight = 440;
        Icon = AppInfo.Icon;
        _warn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x2A)),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(8, 6, 8, 0),
            Child = _warnText,
        };
        Content = BuildLayout();
        _tree.DoubleTapped += async (_, _) => await Guard(_core.Unlocked ? EditSelected : OpenHistory);
        _core.LockChanged += () => Dispatcher.UIThread.Post(UpdateMode);
        _map.ContextMenuFactory = MakeDeviceMenu;
        _map.DeviceClicked += SelectDevice;
        _map.NodeMoved += async (id, x, y, isCenter) => await Guard(() => SaveMapPos(id, new MapPoint { X = x, Y = y }, isCenter));
        _map.CardsMoved += async all => await Guard(() => SaveCardPositions(all));
        _map.CardsReset += async () => await Guard(ResetCardPositions);
        _map.ModeChanged += m =>
        {
            UiStateStore.Current.MapMode = (int)m;
            UiStateStore.Save();
        };
        _map.LayoutReset += async hubId => await Guard(() => ResetMapLayout(hubId));
        _map.DeviceActivated += async id =>
        {
            SelectDevice(id);
            await Guard(_core.Unlocked ? EditSelected : OpenHistory);
        };
        RebuildTree();
        UpdateMode();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            Refresh();
            AutoLockCheck();
        };
        _timer.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            if (CloseBehavior.Minimize(_core.Config)) WindowState = WindowState.Minimized;
            else Hide();
            _core.Lock();
        }
        base.OnClosing(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _core.Touch();
        base.OnPointerMoved(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _core.Touch();
        base.OnKeyDown(e);
    }

    async Task Guard(Func<Task> act)
    {
        _busy++;
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            CrashLog.Write("UI", ex);
            await Dialogs.Info(this, L.T("Error"), ex.Message);
        }
        finally
        {
            _busy--;
            _core.Touch();
        }
    }

    Control BuildLayout()
    {
        // toolbar: device | data (menu) | reports | program
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        Control Label(string text, string icon)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new PathIcon { Data = MdiIcons.Named(icon), Width = 14, Height = 14 });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        StackPanel Group()
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            _groups.Add(g);
            return g;
        }
        Button B(StackPanel g, string text, string icon, string tip, Func<Task> action, bool admin)
        {
            var b = new Button { Content = Label(text, icon), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += async (_, _) => await Guard(action);
            g.Children.Add(b);
            if (admin) _adminButtons.Add(b);
            return b;
        }

        var gDevice = Group();
        B(gDevice, L.T("Add"), "plus", L.T("Add a device (the selected one becomes its parent)"), AddDevice, true);
        B(gDevice, L.T("Edit"), "pencil", L.T("Edit the selected device"), EditSelected, true);
        B(gDevice, L.T("On/off"), "power", L.T("Turn monitoring of the selected device on or off"), ToggleSelected, true);
        B(gDevice, L.T("Remove"), "delete-outline", L.T("Remove the selected device from the list (its history stays in the log)"), RemoveSelected, true);

        var gData = Group();
        var dataBtn = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new PathIcon { Data = MdiIcons.Named("database"), Width = 14, Height = 14 },
                    new TextBlock { Text = L.T("Data"), VerticalAlignment = VerticalAlignment.Center },
                    new PathIcon { Data = MdiIcons.Named("chevron-down"), Width = 12, Height = 12 },
                },
            },
        };
        ToolTip.SetTip(dataBtn, L.T("Device list import and export, backups"));
        MenuItem Mi(string text, string icon, Func<Task> action)
        {
            var mi = new MenuItem { Header = text, Icon = new PathIcon { Data = MdiIcons.Named(icon), Width = 14, Height = 14 } };
            mi.Click += async (_, _) => await Guard(action);
            return mi;
        }
        var dataMenu = new MenuFlyout();
        dataMenu.Items.Add(Mi(L.T("Import device list (CSV)…"), "file-import", ImportCsv));
        dataMenu.Items.Add(Mi(L.T("Export device list (CSV)…"), "file-export", ExportCsv));
        dataMenu.Items.Add(new Separator());
        dataMenu.Items.Add(Mi(L.T("Create backup…"), "content-save", () => BackupUi.CreateManualAsync(this, _core, m => { if (m.StartsWith(L.T("Failed"))) _ = Dialogs.Info(this, L.T("Backup"), m); })));
        dataMenu.Items.Add(Mi(L.T("Restore from backup…"), "backup-restore", () => BackupUi.RestoreAsync(this, m => _ = Dialogs.Info(this, L.T("Backup"), m))));
        dataBtn.Flyout = dataMenu;
        gData.Children.Add(dataBtn);
        _adminButtons.Add(dataBtn);

        var gReports = Group();
        _historyBtn = B(gReports, L.T("History and reports"), "chart-line", L.T("Device history, PDF report, raw log, report verification"), OpenHistory, false);

        var gApp = Group();
        B(gApp, L.T("Settings"), "cog", L.T("Program settings; in view mode — only your personal settings"), OpenSettings, false);
        _lockBtn.Click += async (_, _) => await Guard(LoginOrLock);
        gApp.Children.Add(_lockBtn);

        for (int i = 0; i < _groups.Count; i++)
        {
            if (i > 0)
            {
                var sep = new Border
                {
                    Width = 1,
                    Height = 24,
                    Background = Brushes.Gray,
                    Opacity = 0.5,
                    Margin = new Thickness(10, 0, 10, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _groupSeps.Add(sep);
                bar.Children.Add(sep);
            }
            bar.Children.Add(_groups[i]);
        }

        var top = new DockPanel { Margin = new Thickness(8, 8, 8, 2) };
        _topBar = bar;
        _topChevron = new PathIcon { Width = 14, Height = 14 };
        var topToggle = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 6),
            Margin = new Thickness(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = _topChevron,
        };
        topToggle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(topToggle).Properties.IsLeftButtonPressed) return;
            SetTopCollapsed(!UiStateStore.Current.TopCollapsed);
            UiStateStore.Save();
        };
        _topToggle = topToggle;
        DockPanel.SetDock(topToggle, Dock.Left);
        DockPanel.SetDock(_counters, Dock.Right);
        top.Children.Add(topToggle);
        top.Children.Add(_counters);
        top.Children.Add(bar);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(36, 4, 8, 2) };
        string[] titles = { "", L.T("Name"), L.T("Address"), L.T("Latency"), L.T("Loss"), L.T("State"), L.T("Since") };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(4, 0) };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        // bottom panel: collapsed = chevron up ("розгорнути вгору"), expanded = chevron down ("згорнути вниз")
        _eventsChevron = new PathIcon { Width = 14, Height = 14 };
        var evHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 2, 0, 4),
        };
        evHeader.Children.Add(_eventsChevron);
        evHeader.Children.Add(new TextBlock { Text = L.T("Events"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var evPanel = new DockPanel { Margin = new Thickness(8, 4, 8, 0) };
        DockPanel.SetDock(evHeader, Dock.Top);
        evPanel.Children.Add(evHeader);
        evPanel.Children.Add(_events);
        evHeader.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(evHeader).Properties.IsLeftButtonPressed) return;
            SetEventsExpanded(!_eventsExpanded);
            UiStateStore.Current.EventsExpanded = _eventsExpanded;
            UiStateStore.Save();
        };
        SetEventsExpanded(UiStateStore.Current.EventsExpanded);

        BuildStatusBar();

        var treePanel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        treePanel.Children.Add(header);
        treePanel.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 1),
            Margin = new Thickness(8, 0),
            Child = _tree,
        });

        var tabs = new TabControl { Margin = new Thickness(4, 0) };
        tabs.TemplateApplied += (_, e) =>
        {
            _tabStrip = e.NameScope.Find<Control>("PART_ItemsPresenter");
            SetTopCollapsed(UiStateStore.Current.TopCollapsed);
        };
        tabs.Items.Add(new TabItem { Header = L.T("Map"), Content = _map });
        tabs.Items.Add(new TabItem { Header = L.T("Tree"), Content = treePanel });
        tabs.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() => _map.Refresh(), DispatcherPriority.Background);

        var root = new DockPanel();
        DockPanel.SetDock(_warn, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(evPanel, Dock.Bottom);
        root.Children.Add(_warn);
        root.Children.Add(top);
        root.Children.Add(_statusBar);
        root.Children.Add(evPanel);
        root.Children.Add(tabs);
        return root;
    }

    WrapPanel? _topBar;
    Control? _tabStrip;
    PathIcon? _topChevron;
    Control? _topToggle;

    /// <summary>Wall display: collapses everything above the map / tree except the state counters.</summary>
    void SetTopCollapsed(bool collapsed)
    {
        UiStateStore.Current.TopCollapsed = collapsed;
        if (_topBar is not null) _topBar.IsVisible = !collapsed;
        if (_tabStrip is not null) _tabStrip.IsVisible = !collapsed;
        _map.SetCompact(collapsed);
        if (_topChevron is not null) _topChevron.Data = MdiIcons.Named(collapsed ? "chevron-down" : "chevron-up");
        if (_topToggle is not null)
            ToolTip.SetTip(_topToggle, collapsed
                ? L.T("Expand the toolbar")
                : L.T("Collapse everything above the map, leaving only the state counters (wall display)"));
    }

    static TextBlock Link(string text, Action onClick)
    {
        var t = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Underline,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xE0)),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        t.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(t).Properties.IsLeftButtonPressed) onClick();
        };
        return t;
    }

    void SetEventsExpanded(bool expanded)
    {
        _eventsExpanded = expanded;
        _events.IsVisible = expanded;
        if (_eventsChevron is not null)
        {
            _eventsChevron.Data = MdiIcons.Named(expanded ? "chevron-down" : "chevron-up");
            ToolTip.SetTip(_eventsChevron.Parent as Control ?? _eventsChevron, expanded ? L.T("Collapse") : L.T("Expand"));
        }
        UpdateStatusLeft();
    }

    void BuildStatusBar()
    {
        _folderLink = Link(L.T("Data folder"), () => { if (_core.Unlocked) _ = Guard(OpenDataFolder); });
        ToolTip.SetTip(_folderLink, L.T("Open the data folder: ") + Paths.DataDir);
        _folderSep = new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        _statusRight.Children.Add(_folderLink);
        _statusRight.Children.Add(_folderSep);
        _statusRight.Children.Add(Link(L.T("About"), () => _ = Guard(OpenAbout)));
        DockPanel.SetDock(_statusRight, Dock.Right);
        _statusBar.Children.Add(_statusRight);
        _statusBar.Children.Add(_statusLeft);
    }

    /// <summary>
    /// Left part of the status bar: last event when the "Події" panel is collapsed (if enabled in settings),
    /// otherwise data folder and ping mode for the administrator; empty in view mode.
    /// </summary>
    void UpdateStatusLeft()
    {
        bool collapsed = !_eventsExpanded;
        if (collapsed && _core.Config.ShowLastEventInStatusBar)
        {
            var last = _core.Monitor.Recent().FirstOrDefault() ?? "";
            _statusLeft.Text = last;
            _statusLeft.Opacity = 0.95;
            ToolTip.SetTip(_statusLeft, last.Length > 0 ? last : null);
        }
        else if (_core.Unlocked)
        {
            _statusLeft.Text = L.F("Data: {0} ({1})   |   Ping: {2}", Paths.DataDir, Paths.KindText(Paths.Kind), ProbeFactory.Mode);
            _statusLeft.Opacity = 0.7;
            ToolTip.SetTip(_statusLeft, null);
        }
        else
        {
            _statusLeft.Text = "";
            ToolTip.SetTip(_statusLeft, null);
        }
    }

    void UpdateMode()
    {
        bool un = _core.Unlocked;
        if (_folderLink is not null) _folderLink.IsVisible = un;
        if (_folderSep is not null) _folderSep.IsVisible = un;
        UpdateStatusLeft();
        _map.AllowDrag = un;
        foreach (var b in _adminButtons) b.IsVisible = un;
        if (_historyBtn is not null) _historyBtn.IsVisible = un || _core.Config.AllowReportsInViewMode;
        _lockBtn.IsVisible = _core.Protected;
        // a group is shown if any of its buttons is; a separator only between two shown groups
        bool anyBefore = false;
        for (int i = 0; i < _groups.Count; i++)
        {
            bool vis = _groups[i].Children.Any(c => c.IsVisible);
            _groups[i].IsVisible = vis;
            if (i > 0) _groupSeps[i - 1].IsVisible = vis && anyBefore;
            anyBefore |= vis;
        }
        var modeSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        modeSp.Children.Add(new PathIcon { Data = MdiIcons.Named(un ? "lock-open-variant" : "lock"), Width = 14, Height = 14 });
        modeSp.Children.Add(new TextBlock { Text = un ? L.T("Administrator mode") : L.T("View mode"), VerticalAlignment = VerticalAlignment.Center });
        _lockBtn.Content = modeSp;
        ToolTip.SetTip(_lockBtn, un ? L.T("Click to switch to view mode") : L.T("Click to switch to administrator mode"));
        _warn.IsVisible = _core.Warning is not null;
        _warnText.Text = _core.Warning ?? "";
    }

    void AutoLockCheck()
    {
        var min = _core.Config.AutoLockMinutes;
        if (_busy > 0 || !_core.Protected || !_core.Unlocked || min <= 0) return;
        if (DateTime.UtcNow - _core.LastActivityUtc > TimeSpan.FromMinutes(min))
        {
            _core.Lock();
            _core.Monitor.SystemEvent(L.T("Automatic lock after inactivity"));
        }
    }

    void SelectDevice(string id)
    {
        if (_rows.TryGetValue(id, out var row)) _tree.SelectedItem = row.Item;
    }

    string? SelectedId() => (_tree.SelectedItem as TreeViewItem)?.Tag as string;

    Device? SelectedDevice()
    {
        var id = SelectedId();
        return id is null ? null : _core.Config.Devices.FirstOrDefault(d => d.Id == id);
    }

    void RebuildTree()
    {
        var selected = SelectedId();
        _tree.Items.Clear();
        _rows.Clear();

        var devs = _core.Config.Devices;
        var ids = devs.Select(d => d.Id).ToHashSet();
        bool HasParent(Device d) => d.ParentId is not null && d.ParentId != d.Id && ids.Contains(d.ParentId);
        var children = devs.Where(HasParent)
            .GroupBy(d => d.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var roots = devs.Where(d => !HasParent(d)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        void Add(ItemsControl parent, Device d, int depth)
        {
            if (_rows.ContainsKey(d.Id) || depth > 32) return;
            var row = new RowView(d);
            row.Item.ContextMenu = MakeDeviceMenu(d.Id);
            _rows[d.Id] = row;
            parent.Items.Add(row.Item);
            if (children.TryGetValue(d.Id, out var list))
                foreach (var c in list) Add(row.Item, c, depth + 1);
        }

        foreach (var r in roots) Add(_tree, r, 0);
        foreach (var d in devs)
            if (!_rows.ContainsKey(d.Id)) Add(_tree, d, 0); // broken parent chains

        if (selected is not null && _rows.TryGetValue(selected, out var sel)) _tree.SelectedItem = sel.Item;
        _devById.Clear();
        foreach (var d in devs) _devById.TryAdd(d.Id, d);
        _map.Rebuild(_core.Config, StateOf);
        Refresh(force: true);
    }

    void Refresh(bool force = false)
    {
        if (!force && !IsVisible) return;
        var counts = new StatusCounts();
        var cfg = _core.Config;
        foreach (var d in cfg.Devices)
        {
            var st = StateOf(d.Id);
            if (st is null) continue;
            counts.Add(st.Status);
            if (_rows.TryGetValue(d.Id, out var row))
                row.Update(d, _core.Monitor.Get(d.Id)?.Snap, st.Status, st.IconKey, st.Tip, st.WorstCheck);
        }
        _counters.Set(counts);
        _map.Refresh();

        var v = _core.Monitor.RecentVersion;
        if (v != _eventsVersion)
        {
            _eventsVersion = v;
            UpdateStatusLeft();
            _events.ItemsSource = _core.Monitor.Recent();
        }
    }

    NodeState? StateOf(string id)
    {
        if (!_devById.TryGetValue(id, out var d)) return null;
        var cfg = _core.Config;
        var rt = _core.Monitor.Get(d.Id);
        var eff = !d.Enabled ? DevStatus.Disabled : rt is null ? DevStatus.Unknown : _core.Monitor.Effective(rt);
        var checks = d.Enabled ? _core.Monitor.ChecksOf(d.Id) : Array.Empty<CheckRuntime>();
        DevStatus? worst = null;
        foreach (var c in checks)
            if (worst is null || StatusText.Severity(c.Snap.Status) > StatusText.Severity(worst.Value)) worst = c.Snap.Status;
        return new NodeState(d, eff, MdiIcons.KeyForDevice(cfg, d), Tip(cfg, d, rt?.Snap, eff, checks), worst);
    }

    static string Tip(AppConfig cfg, Device d, DeviceSnapshot? s, DevStatus eff, IReadOnlyList<CheckRuntime> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine(d.Name);
        sb.AppendLine(L.F("{0}   type: {1}", d.Address, (d.Type.Length > 0 ? AppConfig.TypeLabel(d.Type) : "—")));
        if (s is not null && eff != DevStatus.Disabled)
            sb.AppendLine(L.F("State: {0} since {1:dd.MM.yyyy HH:mm:ss} ({2})", StatusText.Ua(eff), s.SinceUtc.ToLocalTime(), Dur(DateTime.UtcNow - s.SinceUtc)));
        var l = s?.Last;
        if (l is not null && d.Primary is { } pcTip)
        {
            sb.AppendLine(L.F("Last check {0:HH:mm:ss} ({1}): {2}", l.TimeUtc.ToLocalTime(), pcTip.Title(), l.Note ?? l.Error ?? "—"));
        }
        else if (l is not null)
        {
            sb.Append(L.F("Last check {0:HH:mm:ss}: {1} of {2} replies, loss {3:0}%", l.TimeUtc.ToLocalTime(), l.Recv, l.Sent, l.LossPct));
            if (l.Avg is double a) sb.Append(L.F(", latency min/avg/max {0:0.#}/{1:0.#}/{2:0.#} ms", l.Min, a, l.Max));
            if (l.Jitter is double j) sb.Append(L.F(", jitter {0:0.#} ms", j));
            sb.AppendLine();
            if (l.Error is not null) sb.AppendLine(L.T("Error: ") + l.Error);
        }
        if (d.Primary is { } pc)
        {
            sb.AppendLine(L.F("State is defined by: {0} (no ping), every {1} s", pc.Title(), pc.IntervalSec));
        }
        else
        {
            var p = ProfileResolver.Effective(cfg, d);
            sb.AppendLine(L.F("Ping: every {0} s, {1} packets of {2} bytes, pause {3} ms, timeout {4} ms", p.IntervalSec, p.PacketCount, p.PacketSize, p.PacketIntervalMs, p.TimeoutMs));
        }
        if (checks.Count > 0)
        {
            sb.AppendLine(L.T("Additional checks:"));
            foreach (var c in checks)
            {
                var cs = c.Snap;
                sb.Append($"  • {c.Check.Title()}: {StatusText.Ua(cs.Status)}");
                if (cs.Last is { } lr) sb.Append($" — {lr.Detail}");
                sb.AppendLine();
            }
        }
        if (d.Description.Length > 0) sb.Append(L.T("Description: ") + d.Description);
        return sb.ToString().TrimEnd();
    }

    static string Dur(TimeSpan t) =>
        t.TotalDays >= 1 ? L.F("{0} d {1} h", (int)t.TotalDays, t.Hours)
        : t.TotalHours >= 1 ? L.F("{0} h {1} min", (int)t.TotalHours, t.Minutes)
        : L.F("{0} min", (int)t.TotalMinutes);

    async Task ApplyAsync(AppConfig cfg, byte[]? newKey = null)
    {
        ConfigStore.Normalize(cfg);
        await _core.ApplyAsync(cfg, newKey);
        RebuildTree();
        UpdateMode();
    }

    async Task LoginOrLock()
    {
        if (!_core.Protected) return;
        if (_core.Unlocked)
        {
            _core.Lock();
            return;
        }
        if (!_core.PasswordRequired)
        {
            await _core.UnlockAsync("");
            UpdateMode();
            return;
        }
        UnlockOutcome? outcome = null;
        var ok = await PasswordDialog.Ask(this, L.T("Administrator login"), L.T("Administrator password:"), async p =>
        {
            outcome = await _core.UnlockAsync(p);
            return outcome.Status != UnlockStatus.Wrong;
        });
        if (!ok || outcome is null) return;
        if (outcome.Status == UnlockStatus.Tampered && outcome.Integrity is { } r)
            await HandleTamper(r);
        UpdateMode();
    }

    async Task HandleTamper(IntegrityResult r)
    {
        var choice = await new TamperDialog(r).ShowDialog<TamperChoice>(this);
        switch (choice)
        {
            case TamperChoice.Revert:
            {
                var signed = Integrity.LoadSigned();
                if (signed is null)
                {
                    await Dialogs.Info(this, L.T("Rollback"), L.T("Could not read the signed copy"));
                    return;
                }
                await ApplyAsync(signed);
                _core.Monitor.SystemEvent(L.T("Configuration restored from the signed copy"));
                break;
            }
            case TamperChoice.Accept:
            {
                var cur = Integrity.LoadCurrent();
                if (cur is null)
                {
                    await Dialogs.Info(this, L.T("Accept changes"), L.T("The current configuration file cannot be read"));
                    return;
                }
                await ApplyAsync(cur);
                _core.Monitor.SystemEvent(L.T("External configuration changes accepted by the administrator"));
                break;
            }
            default:
                if (!r.CanAccept) _core.Lock();
                break;
        }
    }

    async Task AddDevice()
    {
        var cfg = ConfigStore.Clone(_core.Config);
        if (await new DeviceDialog(cfg, null, SelectedId()).ShowDialog<bool>(this))
            await ApplyAsync(cfg);
    }

    async Task EditSelected()
    {
        var d = SelectedDevice();
        if (d is null) { await Dialogs.Info(this, L.T("Device"), L.T("Select a device in the tree first")); return; }
        var cfg = ConfigStore.Clone(_core.Config);
        if (await new DeviceDialog(cfg, d.Id, null).ShowDialog<bool>(this))
            await ApplyAsync(cfg);
    }

    async Task ToggleSelected()
    {
        var d = SelectedDevice();
        if (d is null) { await Dialogs.Info(this, L.T("Device"), L.T("Select a device in the tree first")); return; }
        var cfg = ConfigStore.Clone(_core.Config);
        var x = cfg.Devices.First(v => v.Id == d.Id);
        x.Enabled = !x.Enabled;
        await ApplyAsync(cfg);
    }

    async Task RemoveSelected()
    {
        var d = SelectedDevice();
        if (d is null) { await Dialogs.Info(this, L.T("Device"), L.T("Select a device in the tree first")); return; }
        var kids = _core.Config.Devices.Count(x => x.ParentId == d.Id);
        var msg = L.F("Remove \"{0}\" ({1}) from monitoring?", d.Name, d.Address)
                  + (kids > 0 ? L.F("\nChild devices ({0}) will become root devices.", kids) : "")
                  + L.T("\nThe check history in the log will be kept.");
        if (!await Dialogs.Confirm(this, L.T("Remove device"), msg)) return;
        var cfg = ConfigStore.Clone(_core.Config);
        cfg.Devices.RemoveAll(x => x.Id == d.Id);
        foreach (var x in cfg.Devices.Where(x => x.ParentId == d.Id)) x.ParentId = null;
        await ApplyAsync(cfg);
    }

    async Task OpenSettings()
    {
        var cfg = ConfigStore.Clone(_core.Config);
        bool readOnly = !_core.Unlocked;
        var dlg = new SettingsDialog(cfg, _core, readOnly);
        if (!await dlg.ShowDialog<bool>(this) || readOnly) return;

        if (dlg.AutostartChanged)
        {
            try
            {
                Autostart.Set(dlg.AutostartValue);
                _core.Monitor.SystemEvent(dlg.AutostartValue ? L.T("Autostart enabled") : L.T("Autostart disabled"));
            }
            catch (Exception ex)
            {
                CrashLog.Write("Autostart", ex);
                await Dialogs.Info(this, L.T("Autostart"), L.T("Could not change autostart: ") + ex.Message);
            }
        }

        byte[]? key = null;
        string? ev = null;
        if (dlg.RemovePassword)
        {
            cfg.Security = null;
            ev = L.T("Administrator password removed");
        }
        else if (dlg.NewPassword is { } np)
        {
            var (info, k) = await Task.Run(() => PasswordCrypto.Create(np));
            ev = cfg.Security is null ? L.T("Administrator password set") : L.T("Administrator password changed");
            cfg.Security = info;
            key = k;
        }
        await ApplyAsync(cfg, key);
        App.ApplyTheme(cfg.Theme);
        UpdateStatusLeft();
        if (ev is not null) _core.Monitor.SystemEvent(ev);
        if (dlg.ChannelSecrets is { } secrets)
        {
            try { SecretStore.Save(secrets); }
            catch (Exception ex)
            {
                CrashLog.Write("Secrets save", ex);
                await Dialogs.Info(this, L.T("Channels"), L.T("Could not save the channel secrets: ") + ex.Message);
            }
        }
    }

    async Task SaveMapPos(string id, MapPoint? pos, bool isCenter)
    {
        if (!_core.Unlocked) return;
        var cfg = ConfigStore.Clone(_core.Config);
        var d = cfg.Devices.FirstOrDefault(x => x.Id == id);
        if (d is null) return;
        if (isCenter) d.MapCenterPos = pos;
        else d.MapPos = pos;
        await _core.ApplyAsync(cfg, reloadMonitor: false);
        _devById.Clear();
        foreach (var x in _core.Config.Devices) _devById.TryAdd(x.Id, x);
        _map.SetConfig(_core.Config);
    }

    async Task SaveCardPositions(Dictionary<string, MapPoint> all)
    {
        if (!_core.Unlocked) return;
        var cfg = ConfigStore.Clone(_core.Config);
        foreach (var d in cfg.Devices)
            if (all.TryGetValue(d.Id, out var p)) d.MapCardPos = p;
        if (all.TryGetValue("__loose__", out var lp)) cfg.MapLoosePos = lp;
        await _core.ApplyAsync(cfg, reloadMonitor: false);
        _devById.Clear();
        foreach (var x in _core.Config.Devices) _devById.TryAdd(x.Id, x);
        _map.SetConfig(_core.Config);
    }

    async Task ResetCardPositions()
    {
        if (!_core.Unlocked) return;
        var cfg = ConfigStore.Clone(_core.Config);
        foreach (var d in cfg.Devices) d.MapCardPos = null;
        cfg.MapLoosePos = null;
        await _core.ApplyAsync(cfg, reloadMonitor: false);
        RebuildTree();
    }

    async Task ResetMapLayout(string hubId)
    {
        if (!_core.Unlocked) return;
        var cfg = ConfigStore.Clone(_core.Config);
        foreach (var d in cfg.Devices.Where(x => x.ParentId == hubId)) d.MapPos = null;
        var hub = cfg.Devices.FirstOrDefault(x => x.Id == hubId);
        if (hub is not null) hub.MapCenterPos = null;
        await _core.ApplyAsync(cfg, reloadMonitor: false);
        RebuildTree();
    }

    /// <summary>Right-click menu of a device: connections, history, edit — built each time it opens.</summary>
    ContextMenu MakeDeviceMenu(string id)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            if (!_devById.TryGetValue(id, out var d)) { e.Cancel = true; return; }
            SelectDevice(id);
            bool admin = _core.Unlocked;
            if (admin || _core.Config.AllowLinksInViewMode)
            {
                foreach (var l in d.Links)
                {
                    var link = l;
                    var mi = new MenuItem { Header = link.Title(d.Address) };
                    mi.Click += async (_, _) => await Guard(() => Connector.OpenAsync(this, _core.Config, d, link));
                    menu.Items.Add(mi);
                }
                if (d.Links.Count == 0 && admin)
                    menu.Items.Add(new MenuItem { Header = L.T("(no connections configured — \"Edit…\" → \"Connections\")"), IsEnabled = false });
            }
            if (admin || _core.Config.AllowLinksInViewMode)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var mtr = new MenuItem { Header = L.T("Route trace (MTR)…") };
                mtr.Click += (_, _) => new MtrWindow(d).Show(this);
                menu.Items.Add(mtr);
            }
            if (admin || _core.Config.AllowReportsInViewMode)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var h = new MenuItem { Header = L.T("History and reports") };
                h.Click += async (_, _) => await Guard(OpenHistory);
                menu.Items.Add(h);
            }
            if (admin)
            {
                var ed = new MenuItem { Header = L.T("Edit…") };
                ed.Click += async (_, _) => await Guard(EditSelected);
                menu.Items.Add(ed);
            }
            if (menu.Items.Count == 0) e.Cancel = true;
        };
        return menu;
    }

    async Task OpenAbout() => await new AboutWindow().ShowDialog(this);

    async Task OpenHistory()
    {
        if (!_core.Unlocked && !_core.Config.AllowReportsInViewMode) return;
        if (_core.Config.Devices.Count == 0)
        {
            await Dialogs.Info(this, L.T("History and reports"), L.T("The device list is empty"));
            return;
        }
        await new HistoryWindow(_core, SelectedId()).ShowDialog(this);
    }

    async Task OpenDataFolder()
    {
        bool ok = false;
        try { ok = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Paths.DataDir)); }
        catch (Exception ex) { CrashLog.Write("Open data dir", ex); }
        if (!ok) await Dialogs.Info(this, L.T("Data folder"), L.T("Could not open the file manager. Data folder:\n") + Paths.DataDir);
    }

    async Task ImportCsv()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Import devices from CSV"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv", "*.txt" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;

        byte[] bytes;
        await using (var s = await files[0].OpenReadAsync())
        {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var cfg = ConfigStore.Clone(_core.Config);
        var rep = DeviceCsv.Import(cfg, DecodeText(bytes));
        var sb = new StringBuilder(L.F("New: {0}, updated: {1}, errors: {2}", rep.Added, rep.Updated, rep.Errors.Count));
        foreach (var e in rep.Errors.Take(40)) sb.Append('\n').Append(e);
        if (rep.Errors.Count > 40) sb.Append(L.T("\n… more ")).Append(rep.Errors.Count - 40);
        if (rep.Added + rep.Updated == 0)
        {
            await Dialogs.Info(this, L.T("Import CSV"), sb.ToString());
            return;
        }
        sb.Append(L.T("\n\nApply the changes?"));
        if (await Dialogs.Confirm(this, L.T("Import CSV"), sb.ToString()))
            await ApplyAsync(cfg);
    }

    static string DecodeText(byte[] b)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(b).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            // Excel "CSV" in Ukrainian Windows is usually cp1251
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(b);
        }
    }

    async Task ExportCsv()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Export devices to CSV"),
            SuggestedFileName = $"freemon-devices-{DateTime.Now:yyyyMMdd}.csv",
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file is null) return;

        var data = new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(DeviceCsv.Export(_core.Config)))
            .ToArray();
        await using (var s = await file.OpenWriteAsync())
        {
            await s.WriteAsync(data);
            if (s.CanSeek) s.SetLength(data.Length);
        }
        await Dialogs.Info(this, L.T("Export CSV"), L.F("Devices saved: {0}", _core.Config.Devices.Count));
    }
}

sealed class RowView
{
    readonly Grid _g;
    readonly StatusBadge _dot = new(20)
    {
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    readonly TextBlock _name, _addr, _rtt, _loss, _status, _since;
    string _tip = "";

    public TreeViewItem Item { get; }

    public RowView(Device d)
    {
        _g = new Grid { ColumnDefinitions = new ColumnDefinitions(MainWindow.Cols) };
        _g.Children.Add(_dot);
        _name = Cell(1);
        _addr = Cell(2);
        _rtt = Cell(3);
        _loss = Cell(4);
        _status = Cell(5);
        _since = Cell(6);
        Item = new TreeViewItem { Header = _g, IsExpanded = true, Tag = d.Id };
    }

    TextBlock Cell(int col)
    {
        var t = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0),
        };
        Grid.SetColumn(t, col);
        _g.Children.Add(t);
        return t;
    }

    public void Update(Device d, DeviceSnapshot? s, DevStatus eff, string iconKey, string tip, DevStatus? worstCheck)
    {
        _dot.Set(eff, iconKey);
        _dot.SetCheck(worstCheck);
        _name.Text = d.Name;
        _name.Opacity = d.Enabled ? 1 : 0.5;
        _addr.Text = d.Address;
        var l = s?.Last;
        _rtt.Text = l?.Avg is double a ? L.F("{0:0.#} ms", a) : "—";
        _loss.Text = l is null ? "—" : $"{l.LossPct:0}%";
        _status.Text = StatusText.Ua(eff);
        _status.Foreground = Icons.Brush(eff);
        _since.Text = s is null || eff is DevStatus.Disabled or DevStatus.Unknown ? "" : s.SinceUtc.ToLocalTime().ToString("dd.MM HH:mm:ss");
        if (tip != _tip)
        {
            _tip = tip;
            ToolTip.SetTip(_g, tip);
        }
    }
}
