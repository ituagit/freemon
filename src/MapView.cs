using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeMon;

public enum MapMode { AllExpanded, ProblemsOnly, AllCollapsed }

public sealed record NodeState(Device Device, DevStatus Status, string IconKey, string Tip, DevStatus? WorstCheck = null);

/// <summary>
/// Family map: every device that has children is a "family" card (parent in the center, children around).
/// Devices without parent and children go to the "Окремі пристрої" card.
/// Dashed lines connect a child that has its own family with that family's card.
/// </summary>
public sealed class MapView : DockPanel
{
    const double BadgeSize = 34;
    const double CenterSize = 46;
    const double NodeW = 104;
    const string LooseKey = "__loose__";

    sealed class Node
    {
        public string Id = "";
        public StatusBadge Badge = null!;
        public TextBlock Label = null!;
        public StackPanel Root = null!;
        public string Tip = "";
    }

    sealed class Card
    {
        public string Key = "";
        public Device? Hub;
        public Border Frame = null!;
        public Control Body = null!;
        public Control Header = null!;
        public TextBlock Arrow = null!;
        public CountersView Counts = null!;
        public Node? Center;
        public readonly List<Node> Members = new();
        public readonly List<(Line Line, string ChildId)> Lines = new();
        public readonly Dictionary<string, Node> NodeById = new();
        public DevStatus Worst = DevStatus.Unknown;
        public bool Collapsed;
        public bool SuppressTap;
        /// <summary>Point that saved offsets (MapPos / MapCenterPos) are measured from, in canvas coordinates.</summary>
        public Point Origin;
    }

    const double CardGap = 12;
    readonly Canvas _cards = new() { Margin = new Thickness(6) };
    readonly Button _autoLayout = new() { Content = L.T("↺ Arrange families automatically"), Margin = new Thickness(12, 0, 0, 0), IsVisible = false };
    ScrollViewer _scroll = null!;
    readonly Grid _layer = new();
    StackPanel _bar = null!;

    /// <summary>Compact top: the map toolbar is hidden (navigation "← Back / All families" stays).</summary>
    public void SetCompact(bool compact) => _bar.IsVisible = !compact;
    readonly CheckBox _frames = new() { Content = L.T("family frames and labels"), Margin = new Thickness(12, 0, 0, 0) };
    bool _showFrames = true;
    /// <summary>Positions just dragged but not yet confirmed by a config reload.</summary>
    readonly Dictionary<string, MapPoint> _pending = new();
    Dictionary<string, Device> _devById = new();
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    /// <summary>Navigation row shown when a single family is open: "‹ Back" and breadcrumbs "All families › A › B".</summary>
    readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0, 8, 6), IsVisible = false };
    /// <summary>Families opened before the current one (for "Back").</summary>
    readonly List<string> _drillStack = new();
    readonly List<Card> _cardList = new();
    readonly Dictionary<string, Card> _cardByKey = new();
    readonly Dictionary<string, bool> _manual = new();
    MapMode _mode = MapMode.AllExpanded;
    string? _drill;
    AppConfig? _cfg;
    Func<string, NodeState?>? _state;

    readonly List<Control> _resetButtons = new();
    bool _allowDrag;

    /// <summary>Right-click menu for a device node (built by the main window).</summary>
    public Func<string, ContextMenu>? ContextMenuFactory { get; set; }

    public event Action<string>? DeviceClicked;
    public event Action<string>? DeviceActivated;
    /// <summary>Device id, new offset from the card center (x, y), true = moved as the family center.</summary>
    public event Action<string, double, double, bool>? NodeMoved;
    public event Action<MapMode>? ModeChanged;
    /// <summary>All card positions after a card was dragged: key = hub device id or "__loose__".</summary>
    public event Action<Dictionary<string, MapPoint>>? CardsMoved;
    public event Action? CardsReset;
    /// <summary>Family (hub) id whose children positions should be reset.</summary>
    public event Action<string>? LayoutReset;

    /// <summary>Administrator mode: nodes can be dragged inside their family card.</summary>
    public bool AllowDrag
    {
        get => _allowDrag;
        set
        {
            _allowDrag = value;
            foreach (var b in _resetButtons) b.IsVisible = value;
            _autoLayout.IsVisible = value && _drill is null;
        }
    }

    /// <summary>Config changed without structural changes (e.g. saved positions): keep the current layout.</summary>
    public void SetConfig(AppConfig cfg)
    {
        _cfg = cfg;
        _devById = cfg.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        _pending.Clear();
        (_layer.HorizontalAlignment, _layer.VerticalAlignment) = cfg.MapOrigin switch
        {
            MapOrigin.TopCenter => (HorizontalAlignment.Center, VerticalAlignment.Top),
            MapOrigin.Center => (HorizontalAlignment.Center, VerticalAlignment.Center),
            _ => (HorizontalAlignment.Left, VerticalAlignment.Top),
        };
    }

    public MapView()
    {
        var bar = _bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 6) };
        bar.Children.Add(new TextBlock { Text = L.T("Families:"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        _mode = Enum.IsDefined((MapMode)UiStateStore.Current.MapMode) ? (MapMode)UiStateStore.Current.MapMode : MapMode.AllExpanded;
        bar.Children.Add(ModeButton(L.T("all expanded"), MapMode.AllExpanded, _mode == MapMode.AllExpanded));
        bar.Children.Add(ModeButton(L.T("problems only"), MapMode.ProblemsOnly, _mode == MapMode.ProblemsOnly));
        bar.Children.Add(ModeButton(L.T("all collapsed"), MapMode.AllCollapsed, _mode == MapMode.AllCollapsed));
        _showFrames = UiStateStore.Current.MapFrames;
        _frames.IsChecked = _showFrames;
        _frames.IsCheckedChanged += (_, _) =>
        {
            _showFrames = _frames.IsChecked == true;
            UiStateStore.Current.MapFrames = _showFrames;
            UiStateStore.Save();
            ApplyFrames();
            ApplyCollapse();
            Relayout();
            PostLinks();
        };
        ToolTip.SetTip(_frames, L.T("Turn off to see only devices and links: no frames, names, counters or buttons in family cards"));
        bar.Children.Add(_frames);
        bar.Children.Add(_autoLayout);
        ToolTip.SetTip(_autoLayout, L.T("Remove saved positions of family cards and arrange them automatically"));
        _autoLayout.Click += (_, _) => CardsReset?.Invoke();
        bar.Children.Add(new TextBlock
        {
            Text = L.T("▾ / ▸ — collapse or expand a family; ⤢ — open only this family, ← — back; in administrator mode headers and markers can be dragged, ↺ restores the markers of a family"),
            Opacity = 0.5,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        });

        var layer = _layer;
        _cards.HorizontalAlignment = HorizontalAlignment.Left;
        _cards.VerticalAlignment = VerticalAlignment.Top;
        layer.HorizontalAlignment = HorizontalAlignment.Left;
        layer.VerticalAlignment = VerticalAlignment.Top;
        layer.Children.Add(_cards);
        layer.Children.Add(_overlay);
        _scroll = new ScrollViewer
        {
            Content = layer,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_crumbs, Dock.Top);
        Children.Add(bar);
        Children.Add(_crumbs);
        Children.Add(_scroll);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => { Relayout(); UpdateLinks(); }, DispatcherPriority.Background);
    }

    RadioButton ModeButton(string text, MapMode mode, bool isChecked)
    {
        var r = new RadioButton { GroupName = "freemon-map-mode", Content = text, IsChecked = isChecked };
        r.IsCheckedChanged += (_, _) =>
        {
            if (r.IsChecked != true) return;
            if (_mode == mode) return;
            _mode = mode;
            _manual.Clear();
            ModeChanged?.Invoke(mode);
            ApplyCollapse();
            PostLinks();
        };
        return r;
    }

    public void Rebuild(AppConfig cfg, Func<string, NodeState?> state)
    {
        SetConfig(cfg);
        _state = state;
        Rebuild();
    }

    void Rebuild()
    {
        if (_cfg is null) return;
        _cards.Children.Clear();
        _cardList.Clear();
        _cardByKey.Clear();
        _resetButtons.Clear();

        var devs = _cfg.Devices;
        var ids = devs.Select(d => d.Id).ToHashSet();
        bool HasParent(Device d) => d.ParentId is not null && d.ParentId != d.Id && ids.Contains(d.ParentId);
        var children = devs.Where(HasParent)
            .GroupBy(d => d.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var byId = devs.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());

        if (_drill is not null && !children.ContainsKey(_drill))
        {
            _drill = null;
            _drillStack.Clear();
        }

        var order = new List<string>();
        void Walk(string id, int depth)
        {
            if (depth > 32 || !children.ContainsKey(id) || order.Contains(id)) return;
            order.Add(id);
            foreach (var c in children[id]) Walk(c.Id, depth + 1);
        }

        if (_drill is not null)
        {
            Walk(_drill, 0);
        }
        else
        {
            foreach (var r in devs.Where(d => !HasParent(d)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                Walk(r.Id, 0);
            foreach (var h in children.Keys)
                if (!order.Contains(h)) order.Add(h); // broken chains
        }

        foreach (var hubId in order)
            if (byId.TryGetValue(hubId, out var hub))
                AddCard(BuildFamily(hub, children[hubId], children));

        if (_drill is null)
        {
            var loose = devs.Where(d => !HasParent(d) && !children.ContainsKey(d.Id))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (loose.Count > 0) AddCard(BuildLoose(loose));
        }

        _autoLayout.IsVisible = _allowDrag && _drill is null;
        BuildCrumbs(byId);
        ApplyFrames();
        Refresh();
        Relayout();
        PostLinks();
    }

    void AddCard(Card c)
    {
        _cardList.Add(c);
        _cardByKey[c.Key] = c;
        _cards.Children.Add(c.Frame);
    }

    Node MakeNode(Device d, double size, bool ownFamily)
    {
        var node = new Node { Id = d.Id };
        node.Badge = new StatusBadge(size) { HorizontalAlignment = HorizontalAlignment.Center };
        if (ownFamily)
        {
            node.Badge.BorderBrush = Brushes.Gray;
            node.Badge.BorderThickness = new Thickness(2);
        }
        node.Label = new TextBlock
        {
            Text = d.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NodeW,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        node.Root = new StackPanel
        {
            Width = NodeW,
            Spacing = 2,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        node.Root.Children.Add(node.Badge);
        node.Root.Children.Add(node.Label);
        if (ContextMenuFactory is { } cmf) node.Root.ContextMenu = cmf(d.Id);
        var id = d.Id;
        node.Root.Tapped += (_, e) =>
        {
            DeviceClicked?.Invoke(id);
            e.Handled = true;
        };
        node.Root.DoubleTapped += (_, e) =>
        {
            DeviceActivated?.Invoke(id);
            e.Handled = true;
        };
        return node;
    }

    Card MakeCard(string key, Device? hub, string title, Control body)
    {
        var card = new Card { Key = key, Hub = hub, Body = body };
        card.Arrow = new TextBlock { Text = "▾", Width = 14, VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // left: title and counters; right corner: all buttons (← ⤢ ↺ ▾)
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0),
        };
        if (hub is not null && _drill == hub.Id)
            buttons.Children.Add(HeaderButton("←", L.T("Back (one level up)"), card, GoBack));
        if (hub is not null)
        {
            if (_drill != hub.Id)
            {
                var focusId = hub.Id;
                buttons.Children.Add(HeaderButton("⤢", L.T("Open only this family"), card, () => EnterDrill(focusId)));
            }
            var resetId = hub.Id;
            var reset = HeaderButton("↺", L.T("Restore the default positions of the markers (and the center) in this family"), card,
                () => LayoutReset?.Invoke(resetId));
            reset.IsVisible = _allowDrag;
            buttons.Children.Add(reset);
            _resetButtons.Add(reset);
        }
        buttons.Children.Add(HeaderButton(null, L.T("Collapse / expand"), card, () =>
        {
            _manual[card.Key] = !card.Collapsed;
            ApplyCollapse();
            PostLinks();
        }, card.Arrow));

        card.Counts = new CountersView(11, hideZero: true) { Margin = new Thickness(0, 2, 0, 4) };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(t);
        left.Children.Add(card.Counts);
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(left);

        card.Header = head;
        EnableCardDrag(card, head);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        card.Frame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6),
            BorderBrush = Icons.Brush(DevStatus.Unknown),
            Child = stack,
        };
        return card;
    }

    const double FitPad = 6;
    const double LabelH = 18;

    Card BuildFamily(Device hub, List<Device> kids, Dictionary<string, List<Device>> children)
    {
        int n = kids.Count;
        // compact star: the card is sized to its content afterwards (Refit)
        double r = n <= 2 ? 72 : Math.Max(80, n * (NodeW + 6) / (2 * Math.PI));
        double cx = 0, cy = 0;
        var canvas = new Canvas();
        var card = MakeCard(hub.Id, hub, hub.Name, canvas);
        card.Origin = new Point(cx, cy);

        var points = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            if (kids[i].MapPos is { } mp)
            {
                points.Add(new Point(cx + mp.X, cy + mp.Y));
                continue;
            }
            double a = -Math.PI / 2 + 2 * Math.PI * i / n;
            points.Add(new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }

        var centerPt = hub.MapCenterPos is { } cp ? new Point(cx + cp.X, cy + cp.Y) : new Point(cx, cy);

        for (int i = 0; i < n; i++)
        {
            var line = new Line
            {
                StartPoint = centerPt,
                EndPoint = points[i],
                StrokeThickness = 2,
                Stroke = Icons.Brush(DevStatus.Unknown),
            };
            canvas.Children.Add(line);
            card.Lines.Add((line, kids[i].Id));
        }

        var center = MakeNode(hub, CenterSize, false);
        center.Label.FontWeight = FontWeight.SemiBold;
        Canvas.SetLeft(center.Root, centerPt.X - NodeW / 2);
        Canvas.SetTop(center.Root, centerPt.Y - CenterSize / 2);
        canvas.Children.Add(center.Root);
        card.Center = center;
        card.NodeById[hub.Id] = center;
        var cardLines = card.Lines;
        EnableDrag(card, center, CenterSize, true, p =>
        {
            foreach (var (l, _) in cardLines) l.StartPoint = p;
        });

        for (int i = 0; i < n; i++)
        {
            var kid = kids[i];
            var node = MakeNode(kid, BadgeSize, children.ContainsKey(kid.Id));
            Canvas.SetLeft(node.Root, points[i].X - NodeW / 2);
            Canvas.SetTop(node.Root, points[i].Y - BadgeSize / 2);
            canvas.Children.Add(node.Root);
            card.Members.Add(node);
            card.NodeById[kid.Id] = node;
            var myLine = card.Lines[i].Line;
            EnableDrag(card, node, BadgeSize, false, p => myLine.EndPoint = p);
        }
        Refit(card);
        return card;
    }

    /// <summary>Sizes the family canvas to its nodes (plus a small margin) and moves everything so it starts at the margin.</summary>
    static void Refit(Card card)
    {
        if (card.Body is not Canvas canvas || card.NodeById.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var node in card.NodeById.Values)
        {
            double x = Canvas.GetLeft(node.Root), y = Canvas.GetTop(node.Root);
            double h = node.Badge.Height + LabelH;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + NodeW);
            maxY = Math.Max(maxY, y + h);
        }
        double dx = FitPad - minX, dy = FitPad - minY;
        if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
        {
            foreach (var node in card.NodeById.Values)
            {
                Canvas.SetLeft(node.Root, Canvas.GetLeft(node.Root) + dx);
                Canvas.SetTop(node.Root, Canvas.GetTop(node.Root) + dy);
            }
            foreach (var (l, _) in card.Lines)
            {
                l.StartPoint = new Point(l.StartPoint.X + dx, l.StartPoint.Y + dy);
                l.EndPoint = new Point(l.EndPoint.X + dx, l.EndPoint.Y + dy);
            }
            card.Origin = new Point(card.Origin.X + dx, card.Origin.Y + dy);
        }
        canvas.Width = maxX - minX + 2 * FitPad;
        canvas.Height = maxY - minY + 2 * FitPad;
    }

    void EnableDrag(Card card, Node node, double badge, bool isCenter, Action<Point> onMove)
    {
        var canvas = (Canvas)card.Body;
        Point? start = null;
        double ox = 0, oy = 0;
        bool moved = false;
        const double slack = 240; // how far a node may go beyond the current family area; the card grows after release

        node.Root.PointerPressed += (_, e) =>
        {
            if (!_allowDrag || !e.GetCurrentPoint(node.Root).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(canvas);
            ox = Canvas.GetLeft(node.Root);
            oy = Canvas.GetTop(node.Root);
            moved = false;
            e.Pointer.Capture(node.Root);
        };
        node.Root.PointerMoved += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(canvas);
            double dx = p.X - s.X, dy = p.Y - s.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            moved = true;
            double nx = Math.Clamp(ox + dx, -slack, canvas.Width - NodeW + slack);
            double ny = Math.Clamp(oy + dy, -slack, canvas.Height - badge - LabelH + slack);
            Canvas.SetLeft(node.Root, nx);
            Canvas.SetTop(node.Root, ny);
            onMove(new Point(nx + NodeW / 2, ny + badge / 2));
            UpdateLinks();
        };
        node.Root.PointerReleased += (_, e) =>
        {
            if (start is null) return;
            start = null;
            e.Pointer.Capture(null);
            if (!moved) return;
            e.Handled = true;
            Refit(card);
            double x = Canvas.GetLeft(node.Root) + NodeW / 2 - card.Origin.X;
            double y = Canvas.GetTop(node.Root) + badge / 2 - card.Origin.Y;
            Relayout();
            PostLinks();
            NodeMoved?.Invoke(node.Id, Math.Round(x), Math.Round(y), isCenter);
        };
        node.Root.PointerCaptureLost += (_, _) => start = null;
    }

    Card BuildLoose(List<Device> loose)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 6 * (NodeW + 8) };
        var card = MakeCard(LooseKey, null, L.T("Standalone devices"), wrap);
        foreach (var d in loose)
        {
            var node = MakeNode(d, BadgeSize, false);
            node.Root.Margin = new Thickness(4, 6);
            wrap.Children.Add(node.Root);
            card.Members.Add(node);
            card.NodeById[d.Id] = node;
        }
        return card;
    }

    public void Refresh()
    {
        if (_state is null) return;
        foreach (var card in _cardList)
        {
            var counts = new StatusCounts();
            var statusById = new Dictionary<string, DevStatus>();
            foreach (var node in card.NodeById.Values)
            {
                var st = _state(node.Id);
                if (st is null) continue;
                node.Badge.Set(st.Status, st.IconKey);
                node.Badge.SetCheck(st.WorstCheck);
                node.Root.Opacity = st.Device.Enabled ? 1 : 0.5;
                node.Label.Text = st.Device.Name;
                if (st.Tip != node.Tip)
                {
                    node.Tip = st.Tip;
                    ToolTip.SetTip(node.Root, st.Tip);
                }
                counts.Add(st.Status);
                statusById[node.Id] = st.Status;
            }
            foreach (var (line, childId) in card.Lines)
            {
                var s = statusById.TryGetValue(childId, out var cs) ? cs : DevStatus.Unknown;
                line.Stroke = Icons.Brush(s);
                line.StrokeDashArray = s is DevStatus.Unreachable or DevStatus.Disabled or DevStatus.Down
                    ? new AvaloniaList<double> { 3, 3 }
                    : null;
            }
            card.Counts.Set(counts);
            card.Worst = counts.Worst;
            card.Frame.BorderBrush = _showFrames ? Icons.Brush(counts.Worst) : Brushes.Transparent;
        }
        ApplyCollapse();
        UpdateLinks();
    }

    void ApplyCollapse()
    {
        bool changed = false;
        foreach (var card in _cardList)
        {
            bool collapsed = _manual.TryGetValue(card.Key, out var m)
                ? m
                : _mode switch
                {
                    MapMode.AllCollapsed => true,
                    MapMode.ProblemsOnly => card.Worst is not (DevStatus.Down or DevStatus.Degraded),
                    _ => false,
                };
            if (_drill is not null && card.Key == _drill && !_manual.ContainsKey(card.Key)) collapsed = false;
            if (!_showFrames) collapsed = false;
            if (card.Collapsed != collapsed || card.Body.IsVisible == collapsed) changed = true;
            card.Collapsed = collapsed;
            card.Body.IsVisible = !collapsed;
            card.Arrow.Text = collapsed ? "▸" : "▾";
        }
        if (changed)
        {
            Relayout();
            // second pass after the layout system has settled (text measuring etc.)
            Dispatcher.UIThread.Post(() => { Relayout(); UpdateLinks(); }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Small clickable icon in a card header. Handles the press itself, so the header neither starts dragging
    /// nor reacts; the hit area is padded so it is easy to hit.
    /// </summary>
    Control HeaderButton(string? text, string tip, Card card, Action action, TextBlock? content = null)
    {
        var tb = content ?? new TextBlock { Text = text ?? "", FontSize = 14 };
        tb.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(5, 1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
        ToolTip.SetTip(b, tip);
        b.PointerEntered += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            card.SuppressTap = true;
            if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) Dispatcher.UIThread.Post(action);
        };
        return b;
    }

    void EnterDrill(string id)
    {
        if (_drill is not null) _drillStack.Add(_drill);
        _drill = id;
        _manual.Clear();
        Rebuild();
    }

    void GoBack()
    {
        if (_drillStack.Count > 0)
        {
            _drill = _drillStack[^1];
            _drillStack.RemoveAt(_drillStack.Count - 1);
        }
        else
        {
            _drill = null;
        }
        _manual.Clear();
        Rebuild();
    }

    void GoTo(int level)
    {
        // level -1 = all families, otherwise index in the chain (stack + current)
        var chain = _drillStack.Concat(_drill is null ? Array.Empty<string>() : new[] { _drill }).ToList();
        if (level < 0)
        {
            _drillStack.Clear();
            _drill = null;
        }
        else if (level < chain.Count)
        {
            _drill = chain[level];
            _drillStack.Clear();
            _drillStack.AddRange(chain.Take(level));
        }
        _manual.Clear();
        Rebuild();
    }

    void BuildCrumbs(Dictionary<string, Device> byId)
    {
        _crumbs.Children.Clear();
        _drillStack.RemoveAll(id => !byId.ContainsKey(id));
        _crumbs.IsVisible = _drill is not null;
        if (_drill is null) return;
        var back = new Button { Content = L.T("‹ Back"), Padding = new Thickness(10, 2) };
        ToolTip.SetTip(back, L.T("Back (one level up)"));
        back.Click += (_, _) => GoBack();
        _crumbs.Children.Add(back);
        TextBlock Crumb(string text, int level, bool current)
        {
            var t = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
            };
            if (!current)
            {
                t.TextDecorations = TextDecorations.Underline;
                t.Cursor = new Cursor(StandardCursorType.Hand);
                t.PointerPressed += (_, _) => GoTo(level);
            }
            return t;
        }
        _crumbs.Children.Add(Crumb(L.T("All families"), -1, false));
        var chain = _drillStack.Concat(new[] { _drill }).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            _crumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            var name = byId.TryGetValue(chain[i], out var dv) ? dv.Name : "?";
            _crumbs.Children.Add(Crumb(name, i, i == chain.Count - 1));
        }
    }

    void ApplyFrames()
    {
        foreach (var c in _cardList)
        {
            c.Header.IsVisible = _showFrames;
            if (!_showFrames) c.Frame.BorderBrush = Brushes.Transparent;
        }
    }

    MapPoint? SavedPos(Card c)
    {
        if (_drill is not null || _cfg is null) return null;
        if (_pending.TryGetValue(c.Key, out var pp)) return pp;
        if (c.Key == LooseKey) return _cfg.MapLoosePos;
        return _devById.TryGetValue(c.Key, out var d) ? d.MapCardPos : null;
    }

    /// <summary>Cards with a saved position stay there; the others flow in rows below them.</summary>
    void Relayout()
    {
        double avail = _scroll.Viewport.Width > 50 ? _scroll.Viewport.Width - 16 : Math.Max(400, Bounds.Width - 16);
        double maxRight = 0, maxBottom = 0;
        var auto = new List<Card>();
        foreach (var c in _cardList)
        {
            // a card that was just collapsed/expanded still has a cached size: force a fresh measure
            c.Frame.InvalidateMeasure();
            if (c.Frame.Child is Layoutable inner) inner.InvalidateMeasure();
            c.Header.InvalidateMeasure();
            c.Body.InvalidateMeasure();
            c.Frame.Measure(Size.Infinity);
            var sz = c.Frame.DesiredSize;
            if (SavedPos(c) is { } p)
            {
                double x = Math.Max(0, p.X), y = Math.Max(0, p.Y);
                Canvas.SetLeft(c.Frame, x);
                Canvas.SetTop(c.Frame, y);
                maxRight = Math.Max(maxRight, x + sz.Width);
                maxBottom = Math.Max(maxBottom, y + sz.Height);
            }
            else
            {
                auto.Add(c);
            }
        }
        double ax = 0, ay = maxBottom > 0 ? maxBottom + CardGap : 0, rowH = 0;
        foreach (var c in auto)
        {
            var sz = c.Frame.DesiredSize;
            if (ax > 0 && ax + sz.Width > avail)
            {
                ax = 0;
                ay += rowH + CardGap;
                rowH = 0;
            }
            Canvas.SetLeft(c.Frame, ax);
            Canvas.SetTop(c.Frame, ay);
            ax += sz.Width + CardGap;
            rowH = Math.Max(rowH, sz.Height);
            maxRight = Math.Max(maxRight, Canvas.GetLeft(c.Frame) + sz.Width);
            maxBottom = Math.Max(maxBottom, ay + sz.Height);
        }
        _cards.Width = maxRight + CardGap;
        _cards.Height = maxBottom + CardGap;
    }

    void EnableCardDrag(Card card, Control head)
    {
        Point? start = null;
        double ox = 0, oy = 0;
        bool moved = false;
        head.PointerPressed += (_, e) =>
        {
            card.SuppressTap = false;
            if (!_allowDrag || _drill is not null || !e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(_cards);
            ox = Canvas.GetLeft(card.Frame);
            oy = Canvas.GetTop(card.Frame);
            moved = false;
            e.Pointer.Capture(head);
        };
        head.PointerMoved += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(_cards);
            double dx = p.X - s.X, dy = p.Y - s.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 5) return;
            moved = true;
            double nx = Math.Max(0, ox + dx), ny = Math.Max(0, oy + dy);
            // the board size is NOT changed while dragging (a centered board would shift all cards)
            Canvas.SetLeft(card.Frame, nx);
            Canvas.SetTop(card.Frame, ny);
            UpdateLinks();
        };
        head.PointerReleased += (_, e) =>
        {
            if (start is null) return;
            start = null;
            e.Pointer.Capture(null);
            if (!moved) return;
            card.SuppressTap = true;
            e.Handled = true;
            // freeze the whole layout: every card keeps its current place
            var all = _cardList.ToDictionary(c => c.Key, c => new MapPoint
            {
                X = Math.Round(Canvas.GetLeft(c.Frame)),
                Y = Math.Round(Canvas.GetTop(c.Frame)),
            });
            foreach (var kv in all) _pending[kv.Key] = kv.Value;
            Relayout();
            PostLinks();
            CardsMoved?.Invoke(all);
        };
        head.PointerCaptureLost += (_, _) => start = null;
    }

    void PostLinks() => Dispatcher.UIThread.Post(UpdateLinks, DispatcherPriority.Background);

    void UpdateLinks()
    {
        _overlay.Children.Clear();
        foreach (var card in _cardList)
        {
            var parentId = card.Hub?.ParentId;
            if (parentId is null || !_cardByKey.TryGetValue(parentId, out var pc)) continue;

            Control from = !pc.Collapsed && pc.NodeById.TryGetValue(card.Key, out var n) ? n.Badge : pc.Header;
            Control to = !card.Collapsed && card.Center is not null ? card.Center.Badge : card.Header;
            var a = from.TranslatePoint(new Point(from.Bounds.Width / 2, from.Bounds.Height / 2), _overlay);
            var b = to.TranslatePoint(new Point(to.Bounds.Width / 2, to.Bounds.Height / 2), _overlay);
            if (a is not { } pa || b is not { } pb) continue;
            _overlay.Children.Add(new Line
            {
                StartPoint = pa,
                EndPoint = pb,
                Stroke = Brushes.Gray,
                StrokeThickness = 1.5,
                StrokeDashArray = new AvaloniaList<double> { 5, 4 },
                Opacity = 0.7,
            });
        }
    }
}
