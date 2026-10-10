using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

public struct StatusCounts
{
    public int Up, Degraded, Down, Disabled, Unknown;

    public void Add(DevStatus s)
    {
        switch (s)
        {
            case DevStatus.Up: Up++; break;
            case DevStatus.Degraded: Degraded++; break;
            case DevStatus.Down:
            case DevStatus.Unreachable: Down++; break;
            case DevStatus.Disabled: Disabled++; break;
            default: Unknown++; break;
        }
    }

    /// <summary>Worst status for frames/icons: Down > Degraded > Unknown > Up; all disabled -> Disabled.</summary>
    public DevStatus Worst =>
        Down > 0 ? DevStatus.Down
        : Degraded > 0 ? DevStatus.Degraded
        : Unknown > 0 ? DevStatus.Unknown
        : Up > 0 ? DevStatus.Up
        : Disabled > 0 ? DevStatus.Disabled
        : DevStatus.Unknown;
}

/// <summary>"Є зв'язок 5 · Нестабільно 0 ...": numbers colored by status when > 0, zero items dimmed.</summary>
public sealed class CountersView : StackPanel
{
    static readonly (DevStatus St, string Label)[] Items =
    {
        (DevStatus.Up, L.T("Up")),
        (DevStatus.Degraded, L.T("Unstable")),
        (DevStatus.Down, L.T("Down")),
        (DevStatus.Disabled, L.T("Disabled")),
        (DevStatus.Unknown, L.T("Unknown")),
    };

    readonly TextBlock[] _labels = new TextBlock[Items.Length];
    readonly TextBlock[] _nums = new TextBlock[Items.Length];
    readonly StackPanel[] _groups = new StackPanel[Items.Length];
    readonly bool _hideZero;

    public CountersView(double fontSize = 13, bool hideZero = false)
    {
        _hideZero = hideZero;
        Orientation = Orientation.Horizontal;
        Spacing = 14;
        VerticalAlignment = VerticalAlignment.Center;
        for (int i = 0; i < Items.Length; i++)
        {
            _labels[i] = new TextBlock { Text = Items[i].Label, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center };
            _nums[i] = new TextBlock { Text = "0", FontSize = fontSize, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            _groups[i] = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            _groups[i].Children.Add(_labels[i]);
            _groups[i].Children.Add(_nums[i]);
            Children.Add(_groups[i]);
        }
    }

    public void Set(StatusCounts c)
    {
        int[] v = { c.Up, c.Degraded, c.Down, c.Disabled, c.Unknown };
        for (int i = 0; i < Items.Length; i++)
        {
            _nums[i].Text = v[i].ToString();
            if (v[i] > 0)
            {
                _nums[i].Foreground = Icons.Brush(Items[i].St);
                _nums[i].Opacity = 1;
                _labels[i].Opacity = 1;
                _groups[i].IsVisible = true;
            }
            else
            {
                _nums[i].ClearValue(TextBlock.ForegroundProperty);
                _nums[i].Opacity = 0.35;
                _labels[i].Opacity = 0.35;
                _groups[i].IsVisible = !_hideZero;
            }
        }
    }
}

/// <summary>Colored circle with a white device-type icon.</summary>
public sealed class StatusBadge : Border
{
    readonly PathIcon _icon;
    readonly Avalonia.Controls.Shapes.Ellipse _dot;
    string _key = "";

    public StatusBadge(double size)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        _icon = new PathIcon
        {
            Width = size * 0.6,
            Height = size * 0.6,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        double ds = Math.Max(8, size * 0.36);
        _dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = ds,
            Height = ds,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -ds * 0.25, -ds * 0.25, 0),
            IsVisible = false,
        };
        var grid = new Grid();
        grid.Children.Add(_icon);
        grid.Children.Add(_dot);
        Child = grid;
        Background = Icons.Brush(DevStatus.Unknown);
    }

    /// <summary>Corner dot = worst additional check that is not OK (null = hide).</summary>
    public void SetCheck(DevStatus? worstCheck)
    {
        bool show = worstCheck is DevStatus.Down or DevStatus.Degraded;
        _dot.IsVisible = show;
        if (show) _dot.Fill = Icons.Brush(worstCheck!.Value);
    }

    public void Set(DevStatus s, string iconKey)
    {
        Background = Icons.Brush(s);
        if (_key != iconKey)
        {
            _key = iconKey;
            _icon.Data = MdiIcons.Get(iconKey);
        }
    }
}
