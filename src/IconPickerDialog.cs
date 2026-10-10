using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

/// <summary>Searchable picker over the full Material Design Icons set.</summary>
public sealed class IconPickerDialog : Window
{
    const int MaxShown = 400;
    readonly TextBox _search = new() { PlaceholderText = L.T("Search (icon names in English): router, server, camera, phone, printer, wifi…") };
    readonly ListBox _list = new();
    readonly TextBlock _info = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    readonly StatusBadge _preview = new(40);
    readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    string _selected;

    public IconPickerDialog(string currentKey)
    {
        _selected = MdiIcons.Exists(currentKey) ? currentKey : "lan";
        Title = L.T("Choose an icon");
        Width = 820;
        Height = 640;
        MinWidth = 520;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Control { Tag: string k }) Select(k);
        };
        _list.DoubleTapped += (_, _) => Close(_selected);
        _search.TextChanged += (_, _) => Fill();

        var sel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sel.Children.Add(_preview);
        sel.Children.Add(_name);

        var buttons = Ui.ButtonRow(Ui.Btn(L.T("Select"), (_, _) => Close(_selected), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(null)));
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(sel);

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(_search);
        top.Children.Add(_info);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Select(_selected);
        Fill();
        Opened += (_, _) => _search.Focus();
    }

    void Select(string key)
    {
        _selected = key;
        _preview.Set(DevStatus.Up, key);
        _name.Text = MdiIcons.Title(key);
    }

    void Fill()
    {
        var q = (_search.Text ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
        List<string> keys;
        int total;
        if (q.Length == 0)
        {
            keys = MdiIcons.All.Select(i => i.Key).ToList();
            total = keys.Count;
            _info.Text = L.F("Basic icons. The full set has {0} — use the search.", MdiIcons.Full.Count);
        }
        else
        {
            var curated = MdiIcons.All.Where(i => i.Key.Contains(q) || AppConfig.TypeLabel(i.Title).ToLowerInvariant().Contains(q)).Select(i => i.Key);
            var full = MdiIcons.Full.Keys.Where(k => k.Contains(q)).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)
                .Select(k => MdiIcons.FullPrefix + k);
            var all = curated.Concat(full).ToList();
            total = all.Count;
            keys = all.Take(MaxShown).ToList();
            _info.Text = total == 0 ? L.T("Nothing found")
                : total > MaxShown ? L.F("Found {0}, showing the first {1} — refine the search", total, MaxShown)
                : L.F("Found {0}", total);
        }
        _list.ItemsSource = keys.Select(Item).ToList();
    }

    static Control Item(string key)
    {
        var sp = new StackPanel { Width = 92, Spacing = 3, Margin = new Thickness(2), Tag = key };
        sp.Children.Add(new PathIcon { Data = MdiIcons.Get(key), Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock
        {
            Text = MdiIcons.Title(key),
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        ToolTip.SetTip(sp, MdiIcons.Title(key));
        return sp;
    }
}
