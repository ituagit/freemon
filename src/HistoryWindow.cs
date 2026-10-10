using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace FreeMon;

/// <summary>Device history: period, plain-language summary, chart, incidents; exports PDF report and raw TXT log.</summary>
public sealed class HistoryWindow : Window
{
    static readonly string[] Presets =
    {
        L.T("Last 24 hours"), L.T("Today"), L.T("Yesterday"), L.T("Last 7 days"), L.T("Last 30 days"),
        L.T("Current month"), L.T("Previous month"), L.T("Custom period"),
    };

    readonly AppCore _core;
    readonly List<Device> _devs;
    readonly ComboBox _dev = new() { MinWidth = 320 };
    readonly ComboBox _preset = new() { Width = 200 };
    readonly CalendarDatePicker _from = new() { Width = 140 };
    readonly CalendarDatePicker _to = new() { Width = 140 };
    readonly TextBox _summary = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        MinHeight = 60,
    };
    readonly HistoryChart _chart = new() { Height = 260, Margin = new Thickness(0, 6) };
    readonly TextBlock _zoomInfo = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    readonly Button _zoomReset = new() { IsVisible = false };

    readonly ListBox _incidents = new() { Height = 140 };
    readonly TextBox _org = new() { Width = 360 };
    readonly TextBox _author = new() { Width = 360 };
    readonly TextBox _comment = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
    readonly TextBlock _status = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _pdf;
    readonly Button _txt;
    HistoryResult? _res;
    int _loadId;

    public HistoryWindow(AppCore core, string? deviceId)
    {
        _core = core;
        Title = L.T("History and reports");
        Width = 1000;
        Height = 860;
        MinWidth = 760;
        MinHeight = 560;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _devs = core.Config.Devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _dev.ItemsSource = _devs.Select(d => $"{d.Name} ({d.Address})").ToList();
        var di = deviceId is null ? -1 : _devs.FindIndex(d => d.Id == deviceId);
        _dev.SelectedIndex = di >= 0 ? di : (_devs.Count > 0 ? 0 : -1);
        _preset.ItemsSource = Presets;
        _preset.SelectedIndex = 3;
        _from.SelectedDate = DateTime.Today.AddDays(-7);
        _to.SelectedDate = DateTime.Today;
        var ui = UiStateStore.Current;
        _org.Text = ui.ReportOrganization ?? core.Config.Report.Organization;
        _author.Text = ui.ReportPreparedBy ?? core.Config.Report.PreparedBy;
        UpdatePickers();

        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control c) { c.Margin = new Thickness(0, 0, 8, 6); top.Children.Add(c); }
        Add(Ui.Label(L.T("Device")));
        Add(_dev);
        Add(Ui.Label(L.T("Period")));
        Add(_preset);
        Add(Ui.Label(L.T("from")));
        Add(_from);
        Add(Ui.Label(L.T("to")));
        Add(_to);

        _pdf = new Button { Content = L.T("Save PDF report…"), IsEnabled = false };
        _txt = new Button { Content = L.T("Raw TXT log…"), IsEnabled = false };
        var close = new Button { Content = L.T("Close") };
        var verify = new Button { Content = L.T("Verify report…") };
        verify.Click += async (_, _) => await new VerifyReportWindow(_core).ShowDialog(this);
        _pdf.Click += async (_, _) => await Guard(SavePdf);
        _txt.Click += async (_, _) => await Guard(SaveTxt);
        close.Click += (_, _) => Close();

        var form = new StackPanel { Spacing = 4 };
        form.Children.Add(Ui.Header(L.T("For the PDF report")));
        form.Children.Add(Ui.Row(L.T("Organization / department (header)"), _org, 240));
        form.Children.Add(Ui.Row(L.T("Prepared by"), _author, 240));
        form.Children.Add(Ui.Label(L.T("Report comment (free text, goes into the PDF)")));
        form.Children.Add(_comment);

        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        right.Children.Add(_pdf);
        right.Children.Add(_txt);
        right.Children.Add(verify);
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_status);

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(Ui.Header(L.T("Summary")));
        body.Children.Add(_summary);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _zoomReset.Content = L.T("Reset zoom");
        _zoomReset.Click += (_, _) => _chart.ResetView();
        zoomRow.Children.Add(_zoomReset);
        zoomRow.Children.Add(_zoomInfo);
        body.Children.Add(zoomRow);
        body.Children.Add(_chart);
        _chart.ViewChanged += (zoomed, vf, vt) =>
        {
            _zoomReset.IsVisible = zoomed;
            _zoomInfo.Text = zoomed
                ? L.F("Chart zoom: {0:dd.MM.yyyy HH:mm} – {1:dd.MM.yyyy HH:mm}. View only — PDF and TXT always cover the whole selected period.", vf.ToLocalTime(), vt.ToLocalTime())
                : L.T("Chart: mouse wheel — zoom, drag — move along the timeline, double-click — whole period.");
        };
        _zoomInfo.Text = L.T("Chart: mouse wheel — zoom, drag — move along the timeline, double-click — whole period.");
        body.Children.Add(Ui.Header(L.T("Incidents")));
        body.Children.Add(_incidents);
        body.Children.Add(form);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;

        _dev.SelectionChanged += async (_, _) => await LoadAsync();
        _preset.SelectionChanged += async (_, _) =>
        {
            UpdatePickers();
            await LoadAsync();
        };
        _from.SelectedDateChanged += async (_, _) => { if (_preset.SelectedIndex == Presets.Length - 1) await LoadAsync(); };
        _to.SelectedDateChanged += async (_, _) => { if (_preset.SelectedIndex == Presets.Length - 1) await LoadAsync(); };
        Opened += async (_, _) => await LoadAsync();
    }

    Device? SelectedDevice => _dev.SelectedIndex >= 0 && _dev.SelectedIndex < _devs.Count ? _devs[_dev.SelectedIndex] : null;

    void UpdatePickers()
    {
        bool custom = _preset.SelectedIndex == Presets.Length - 1;
        _from.IsEnabled = custom;
        _to.IsEnabled = custom;
    }

    (DateTime From, DateTime To) Range()
    {
        var now = DateTime.Now;
        var today = now.Date;
        var month = new DateTime(today.Year, today.Month, 1);
        return _preset.SelectedIndex switch
        {
            0 => (now.AddHours(-24), now),
            1 => (today, now),
            2 => (today.AddDays(-1), today),
            3 => (now.AddDays(-7), now),
            4 => (now.AddDays(-30), now),
            5 => (month, now),
            6 => (month.AddMonths(-1), month),
            _ => ((_from.SelectedDate ?? today).Date, (_to.SelectedDate ?? today).Date.AddDays(1)),
        };
    }

    async Task Guard(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            CrashLog.Write("History", ex);
            _status.Text = L.T("Error: ") + ex.Message;
        }
    }

    async Task LoadAsync()
    {
        var id = ++_loadId;
        var d = SelectedDevice;
        if (d is null)
        {
            _status.Text = L.T("No devices");
            return;
        }
        var (f, t) = Range();
        if (t <= f)
        {
            _status.Text = L.T("The end of the period must be later than the start");
            return;
        }
        _status.Text = L.T("Loading the log…");
        _pdf.IsEnabled = false;
        _txt.IsEnabled = false;

        HistoryResult r;
        try
        {
            var adminKey = _core.AdminSealKey;
            r = await Task.Run(() => HistoryCalc.Compute(d, f.ToUniversalTime(), t.ToUniversalTime(), adminKey));
        }
        catch (Exception ex)
        {
            CrashLog.Write("History load", ex);
            if (id == _loadId) _status.Text = L.T("Error reading the log: ") + ex.Message;
            return;
        }
        if (id != _loadId) return;

        _res = r;
        _summary.Text = HistoryText.Summary(r) + "\n\n" + HistoryText.IntegrityText(r);
        _chart.Data = r;
        _incidents.ItemsSource = r.Incidents.Count == 0
            ? new List<string> { L.T("No incidents recorded") }
            : r.Incidents.AsEnumerable().Reverse().Select(HistoryText.IncidentLine).ToList();
        _status.Text = L.F("Checks in the log for the period: {0}", r.CycleCount);
        _pdf.IsEnabled = true;
        _txt.IsEnabled = true;
    }

    static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in s.Trim()) sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
        return sb.Length == 0 ? "device" : sb.ToString();
    }

    async Task<IStorageFile?> AskSave(string title, string name, string ext, string typeName)
    {
        return await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + ext } } },
        });
    }

    static async Task WriteAll(IStorageFile file, byte[] data)
    {
        await using var s = await file.OpenWriteAsync();
        await s.WriteAsync(data);
        if (s.CanSeek) s.SetLength(data.Length);
    }

    string BaseName(HistoryResult r) =>
        $"FreeMon-{SafeName(r.Device.Name)}-{r.FromUtc.ToLocalTime():yyyyMMdd}-{r.ToUtc.ToLocalTime():yyyyMMdd}";

    async Task SavePdf()
    {
        var r = _res;
        if (r is null) return;
        var file = await AskSave(L.T("Save PDF report"), BaseName(r) + ".pdf", "pdf", "PDF");
        if (file is null) return;

        _status.Text = L.T("Generating the PDF…");
        UiStateStore.Current.ReportOrganization = (_org.Text ?? "").Trim();
        UiStateStore.Current.ReportPreparedBy = (_author.Text ?? "").Trim();
        UiStateStore.Save();
        var cfg = _core.Config;
        var d = r.Device;
        var parent = d.ParentId is null ? null : cfg.Devices.FirstOrDefault(x => x.Id == d.ParentId);
        var meta = new ReportMeta(
            (_org.Text ?? "").Trim(),
            (_author.Text ?? "").Trim(),
            _comment.Text ?? "",
            parent is null ? "" : $"{parent.Name} ({parent.Address})",
            AppConfig.TypeLabelReport(d.Type),
            HistoryText.CheckingText(cfg, d));
        var png = HistoryChart.RenderPng(r, 1200, 420);
        var bytes = await Task.Run(() => ReportPdf.Build(r, meta, png));
        await WriteAll(file, bytes);
        _status.Text = L.T("PDF report saved");
        _core.Monitor.SystemEvent(L.F("PDF report created: {0}, {1}", d.Name, HistoryText.Period(r)));
        await OfferOpen(file, L.T("PDF report saved"));
    }

    async Task SaveTxt()
    {
        var r = _res;
        if (r is null) return;
        var file = await AskSave(L.T("Save raw log"), BaseName(r) + ".txt", "txt", L.T("Text"));
        if (file is null) return;
        _status.Text = L.T("Generating the log…");
        var cfg = _core.Config;
        var bytes = await Task.Run(() =>
            new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(HistoryText.RawLog(r, cfg))).ToArray());
        await WriteAll(file, bytes);
        _status.Text = L.F("Log saved ({0} checks)", r.CycleCount);
        await OfferOpen(file, L.T("Raw log saved"));
    }

    /// <summary>Asks whether to open the saved file in the default application (PDF viewer, text editor).</summary>
    async Task OfferOpen(Avalonia.Platform.Storage.IStorageFile file, string title)
    {
        if (!await Dialogs.Confirm(this, title, L.F("{0}\n\nOpen it now?", file.TryGetLocalPath() ?? file.Name))) return;
        if (!await Launcher.LaunchFileAsync(file))
            await Dialogs.Info(this, title, L.T("The system could not open the file: no application is associated with this file type."));
    }
}
