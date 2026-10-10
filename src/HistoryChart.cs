using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FreeMon;

/// <summary>
/// Average latency line, packet-loss bars and a status strip. Same drawing for screen and PDF.
/// On screen the chart can be zoomed with the mouse wheel and dragged left / right like a DVR timeline;
/// this only changes what is shown — reports always use the whole selected period.
/// </summary>
public sealed class HistoryChart : Control
{
    static readonly IBrush RttBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB5));
    static readonly IBrush LossBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xD9, 0x3A, 0x3A));
    static readonly IBrush ScreenText = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    static readonly IBrush ScreenGrid = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
    static readonly IBrush PrintGrid = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    static readonly IBrush NoDataBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x88, 0x88, 0x88));
    static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
    static readonly IBrush ThumbBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x2F, 0x6F, 0xB5));

    const long MinViewMs = 2 * 60_000;

    HistoryResult? _data;
    long _vf, _vt;                 // visible window, unix ms (UTC)
    Point? _dragStart;
    long _dragVf;
    List<Bucket>? _cache;
    (long F, long T, int N) _cacheKey;

    public bool ForPrint { get; init; }

    /// <summary>Visible window changed: (zoomed in?, from UTC, to UTC).</summary>
    public event Action<bool, DateTime, DateTime>? ViewChanged;

    public HistoryResult? Data
    {
        get => _data;
        set
        {
            _data = value;
            _cache = null;
            ResetView();
        }
    }

    public bool Zoomed => _data is { } r && (_vf > HistoryCalc.Ms(r.FromUtc) || _vt < HistoryCalc.Ms(r.ToUtc));

    public void ResetView()
    {
        if (_data is { } r)
        {
            _vf = HistoryCalc.Ms(r.FromUtc);
            _vt = HistoryCalc.Ms(r.ToUtc);
        }
        InvalidateVisual();
        RaiseView();
    }

    void RaiseView()
    {
        if (_data is null) return;
        ViewChanged?.Invoke(Zoomed, HistoryCalc.Utc(_vf), HistoryCalc.Utc(_vt));
    }

    double FontSize => ForPrint ? 13 : 11;
    double LeftM => ForPrint ? 64 : 52;
    double RightM => ForPrint ? 54 : 44;

    Rect PlotRect(double w, double h)
    {
        double top = ForPrint ? 30 : 34, bottom = ForPrint ? 46 : 40;
        return new Rect(LeftM, top, Math.Max(10, w - LeftM - RightM), Math.Max(10, h - top - bottom));
    }

    void Text(DrawingContext ctx, string s, double x, double y, IBrush brush, int align = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), FontSize, brush);
        double px = align switch { 1 => x - ft.Width, 2 => x - ft.Width / 2, _ => x };
        ctx.DrawText(ft, new Point(px, y));
    }

    static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (m * p >= v) return m * p;
        return 10 * p;
    }

    // ---------------------------------------------------------------- interaction (screen only)

    void SetView(long vf, long vt)
    {
        if (_data is not { } r) return;
        long full0 = HistoryCalc.Ms(r.FromUtc), full1 = HistoryCalc.Ms(r.ToUtc);
        long span = Math.Clamp(vt - vf, Math.Min(MinViewMs, full1 - full0), full1 - full0);
        vf = Math.Clamp(vf, full0, full1 - span);
        _vf = vf;
        _vt = vf + span;
        InvalidateVisual();
        RaiseView();
    }

    long TimeAt(double x)
    {
        var plot = PlotRect(Bounds.Width, Bounds.Height);
        double frac = Math.Clamp((x - plot.Left) / plot.Width, 0, 1);
        return _vf + (long)((_vt - _vf) * frac);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ForPrint || _data is null) return;
        e.Handled = true; // the wheel over the chart zooms, it never scrolls the window
        double dx = e.Delta.X, dy = e.Delta.Y;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && dx == 0) { dx = dy; dy = 0; }
        if (dx != 0)
        {
            long shift = (long)((_vt - _vf) * 0.15 * -dx);
            SetView(_vf + shift, _vt + shift);
            return;
        }
        if (dy == 0) return;
        long anchor = TimeAt(e.GetPosition(this).X);
        double factor = Math.Pow(0.8, dy); // up = zoom in
        long nf = anchor - (long)((anchor - _vf) * factor);
        long nt = anchor + (long)((_vt - anchor) * factor);
        if (nt - nf < MinViewMs)
        {
            double frac = (anchor - _vf) / (double)Math.Max(1, _vt - _vf);
            nf = anchor - (long)(MinViewMs * frac);
            nt = nf + MinViewMs;
        }
        SetView(nf, nt);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ForPrint || _data is null) return;
        if (e.ClickCount >= 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !Zoomed) return;
        _dragStart = e.GetPosition(this);
        _dragVf = _vf;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } s) return;
        var plot = PlotRect(Bounds.Width, Bounds.Height);
        double dx = e.GetPosition(this).X - s.X;
        long span = _vt - _vf;
        long shift = (long)(-dx / plot.Width * span);
        SetView(_dragVf + shift, _dragVf + shift + span);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
        Cursor = null;
    }

    // ---------------------------------------------------------------- drawing

    List<Bucket> VisibleBuckets(HistoryResult r, int n)
    {
        long full0 = HistoryCalc.Ms(r.FromUtc), full1 = HistoryCalc.Ms(r.ToUtc);
        if (_vf == full0 && _vt == full1 && (ForPrint || n == r.Buckets.Count)) return r.Buckets;
        var key = (_vf, _vt, n);
        if (_cache is not null && _cacheKey == key) return _cache;
        _cache = HistoryCalc.Bucketize(r.Rows, _vf, _vt, n);
        _cacheKey = key;
        return _cache;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        double w = Bounds.Width, h = Bounds.Height;
        var textBrush = ForPrint ? Brushes.Black : ScreenText;
        var gridPen = new Pen(ForPrint ? PrintGrid : ScreenGrid, 1);
        // background: white for print; transparent on screen, but it makes the whole area react to the mouse
        ctx.FillRectangle(ForPrint ? Brushes.White : Brushes.Transparent, new Rect(0, 0, w, h));

        var plot = PlotRect(w, h);
        var r = _data;
        if (r is null || r.Rows.Count == 0)
        {
            Text(ctx, L.R("No data for the selected period"), w / 2, h / 2 - FontSize, textBrush, 2);
            return;
        }
        if (_vt <= _vf) { _vf = HistoryCalc.Ms(r.FromUtc); _vt = HistoryCalc.Ms(r.ToUtc); }

        int n = ForPrint ? r.Buckets.Count : (int)Math.Clamp(plot.Width / 3, 20, 400);
        var buckets = VisibleBuckets(r, n);
        n = buckets.Count;
        double bw = plot.Width / n;
        double maxRtt = NiceMax(buckets.Where(b => b.AvgRtt.HasValue).Select(b => b.AvgRtt!.Value).DefaultIfEmpty(1).Max() * 1.1);

        // grid + left axis (latency), right axis (loss %)
        for (int k = 0; k <= 4; k++)
        {
            double y = plot.Bottom - plot.Height * k / 4;
            ctx.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Text(ctx, HistoryText.Num(maxRtt * k / 4, "0.#"), plot.Left - 6, y - FontSize * 0.7, RttBrush, 1);
            Text(ctx, $"{25 * k}%", plot.Right + 6, y - FontSize * 0.7, textBrush);
        }
        Text(ctx, L.R("ms"), plot.Left - 6, plot.Top - FontSize * 2, RttBrush, 1);

        double sy = plot.Bottom + 4, sh = ForPrint ? 10 : 8;
        double viewMs = _vt - _vf;
        var hatchPen = new Pen(ForPrint ? new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80)) : new SolidColorBrush(Color.FromArgb(0x55, 0x99, 0x99, 0x99)), 1);

        using (ctx.PushClip(new Rect(plot.Left, plot.Top, plot.Width, plot.Height + sh + 6)))
        {
            // monitoring gaps: hatched grey bands over the plot and the state strip
            foreach (var (gs, ge) in r.Gaps)
            {
                double x1 = plot.Left + plot.Width * (HistoryCalc.Ms(gs) - _vf) / viewMs;
                double x2 = plot.Left + plot.Width * (HistoryCalc.Ms(ge) - _vf) / viewMs;
                if (x2 < plot.Left || x1 > plot.Right) continue;
                if (x2 - x1 < 2) x2 = x1 + 2;
                var band = new Rect(x1, plot.Top, x2 - x1, plot.Height + sh + 4);
                ctx.FillRectangle(NoDataBrush, band);
                using (ctx.PushClip(band))
                {
                    for (double hx = x1 - band.Height; hx < x2; hx += 7)
                        ctx.DrawLine(hatchPen, new Point(hx, band.Bottom), new Point(hx + band.Height, band.Top));
                }
            }

            // loss bars
            for (int i = 0; i < n; i++)
            {
                var b = buckets[i];
                if (b.LossPct is double loss && loss > 0)
                {
                    double bh = plot.Height * Math.Min(100, loss) / 100;
                    ctx.FillRectangle(LossBrush, new Rect(plot.Left + i * bw, plot.Bottom - bh, Math.Max(1, bw), bh));
                }
            }

            // latency line (breaks where there is no data)
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < n; i++)
                {
                    var b = buckets[i];
                    if (b.AvgRtt is not double v)
                    {
                        if (open) { g.EndFigure(false); open = false; }
                        continue;
                    }
                    var p = new Point(plot.Left + (i + 0.5) * bw, plot.Bottom - plot.Height * Math.Min(v, maxRtt) / maxRtt);
                    if (!open) { g.BeginFigure(p, false); open = true; }
                    else g.LineTo(p);
                }
                if (open) g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(RttBrush, ForPrint ? 2.2 : 1.6), geo);

            // status strip
            for (int i = 0; i < n; i++)
            {
                var b = buckets[i];
                var brush = b.NoData ? NoDataBrush : Icons.Brush(b.Worst);
                ctx.FillRectangle(brush, new Rect(plot.Left + i * bw, sy, Math.Max(1, bw + 0.5), sh));
            }
        }

        // time axis of the visible window
        var from = HistoryCalc.Utc(_vf).ToLocalTime();
        var to = HistoryCalc.Utc(_vt).ToLocalTime();
        var span = to - from;
        string fmt = span.TotalHours <= 2 ? "HH:mm:ss" : span.TotalHours <= 36 ? "HH:mm" : span.TotalDays <= 10 ? "dd.MM HH:mm" : "dd.MM";
        for (int k = 0; k <= 5; k++)
        {
            var tt = from + TimeSpan.FromTicks(span.Ticks * k / 5);
            double x = plot.Left + plot.Width * k / 5;
            Text(ctx, tt.ToString(fmt), x, sy + sh + 4, textBrush, k == 0 ? 0 : k == 5 ? 1 : 2);
        }

        // legend
        double lx = plot.Left;
        ctx.DrawLine(new Pen(RttBrush, 2), new Point(lx, 10), new Point(lx + 18, 10));
        Text(ctx, L.R("average latency"), lx + 22, 10 - FontSize * 0.7, textBrush);
        lx += 22 + 150;
        ctx.FillRectangle(LossBrush, new Rect(lx, 4, 12, 12));
        Text(ctx, L.R("packet loss"), lx + 16, 10 - FontSize * 0.7, textBrush);
        lx += 16 + 130;
        ctx.FillRectangle(Icons.Brush(DevStatus.Up), new Rect(lx, 6, 8, 8));
        ctx.FillRectangle(Icons.Brush(DevStatus.Degraded), new Rect(lx + 9, 6, 8, 8));
        ctx.FillRectangle(Icons.Brush(DevStatus.Down), new Rect(lx + 18, 6, 8, 8));
        Text(ctx, L.R("state (bar at the bottom)"), lx + 30, 10 - FontSize * 0.7, textBrush);
        if (r.Gaps.Count > 0)
        {
            lx += 30 + 190;
            var keyRect = new Rect(lx, 4, 14, 12);
            ctx.FillRectangle(NoDataBrush, keyRect);
            using (ctx.PushClip(keyRect))
                for (double hx = lx - 12; hx < lx + 14; hx += 5)
                    ctx.DrawLine(hatchPen, new Point(hx, keyRect.Bottom), new Point(hx + 12, keyRect.Top));
            Text(ctx, L.R("no monitoring"), lx + 18, 10 - FontSize * 0.7, textBrush);
        }

        // overview of the whole period (screen, when zoomed): where the visible window is
        if (!ForPrint && Zoomed)
        {
            long full0 = HistoryCalc.Ms(r.FromUtc), full1 = HistoryCalc.Ms(r.ToUtc);
            double fullMs = Math.Max(1, full1 - full0);
            var track = new Rect(plot.Left, 22, plot.Width, 5);
            ctx.FillRectangle(TrackBrush, track);
            double tx1 = plot.Left + plot.Width * (_vf - full0) / fullMs;
            double tx2 = plot.Left + plot.Width * (_vt - full0) / fullMs;
            ctx.FillRectangle(ThumbBrush, new Rect(tx1, 22, Math.Max(3, tx2 - tx1), 5));
        }
    }

    /// <summary>Renders the chart of the whole period off-screen to PNG (must be called on the UI thread).</summary>
    public static byte[] RenderPng(HistoryResult r, int width, int height)
    {
        var chart = new HistoryChart { ForPrint = true, Width = width, Height = height };
        chart.Data = r;
        chart.Measure(new Size(width, height));
        chart.Arrange(new Rect(0, 0, width, height));
        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(chart);
        using var ms = new MemoryStream();
        rtb.Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }
}
