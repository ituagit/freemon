using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

/// <summary>Editor for one alert channel. Secrets are returned separately (they go to secrets.json).</summary>
public sealed class ChannelDialog : Window
{
    readonly AppConfig _cfg;
    readonly AlertChannel _c;
    readonly ChannelSecret _s;

    readonly ComboBox _kind = new() { Width = 200 };
    readonly TextBox _name = new() { Width = 360, PlaceholderText = L.T("for example, \"Duty — Telegram\"") };
    readonly CheckBox _enabled = new() { Content = L.T("Channel enabled") };
    readonly CheckBox _onDown = new() { Content = L.T("\"Down\"") };
    readonly CheckBox _onDeg = new() { Content = L.T("\"Unstable\"") };
    readonly CheckBox _onRec = new() { Content = L.T("Connection restored") };
    readonly CheckBox _onChecks = new() { Content = L.T("Events of additional checks (TCP / HTTP / DNS)") };
    readonly CheckBox _skipChildren = new() { Content = L.T("Do not send about children if the parent is unreachable") };
    readonly CheckBox _ignoreCert = new() { Content = L.T("Ignore certificate errors (self-signed server certificate)") };

    // email
    readonly TextBox _smtpHost = new() { Width = 300 };
    readonly TextBox _smtpPort = new() { Width = 90 };
    readonly ComboBox _smtpSec = new() { Width = 200 };
    readonly TextBox _smtpUser = new() { Width = 300, PlaceholderText = L.T("empty — no authentication") };
    readonly TextBox _smtpPass = Ui.PasswordBox();
    readonly TextBox _from = new() { Width = 300 };
    readonly TextBox _to = new() { Width = 420, PlaceholderText = L.T("several addresses — separated by commas") };

    // webhook
    readonly ComboBox _preset = new() { Width = 300 };
    readonly TextBlock _presetHint = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    readonly TextBox _url = new() { Width = 520 };
    readonly ComboBox _method = new() { Width = 100 };
    readonly TextBox _ctype = new() { Width = 300 };
    readonly TextBox _headers = new() { Width = 520, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = L.T("Name: value (one per line)") };
    readonly TextBox _body = new() { Width = 520, Height = 140, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Liberation Mono, monospace") };

    readonly StackPanel _emailPanel = new() { Spacing = 6 };
    readonly StackPanel _hookPanel = new() { Spacing = 6 };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    readonly TextBlock _test = new() { TextWrapping = TextWrapping.Wrap };

    public ChannelSecret Secret => _s;

    public ChannelDialog(AppConfig cfg, AlertChannel channel, ChannelSecret secret)
    {
        _cfg = cfg;
        _c = channel;
        _s = secret;
        Title = L.T("Notification channel");
        Width = 760;
        Height = 780;
        MinWidth = 600;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        _kind.ItemsSource = new[] { "Email (SMTP)", L.T("Webhook (HTTP)") };
        _kind.SelectedIndex = (int)channel.Kind;
        _name.Text = channel.Name;
        _enabled.IsChecked = channel.Enabled;
        _onDown.IsChecked = channel.OnDown;
        _onDeg.IsChecked = channel.OnDegraded;
        _onRec.IsChecked = channel.OnRecovery;
        _onChecks.IsChecked = channel.OnChecks;
        _skipChildren.IsChecked = channel.SkipUnreachableChildren;
        _ignoreCert.IsChecked = channel.IgnoreCertErrors;

        _smtpHost.Text = channel.SmtpHost;
        _smtpPort.Text = channel.SmtpPort.ToString(CultureInfo.InvariantCulture);
        _smtpSec.ItemsSource = new[] { L.T("STARTTLS (usually port 587)"), L.T("TLS from the start (usually port 465)"), L.T("No encryption (not recommended)") };
        _smtpSec.SelectedIndex = (int)channel.SmtpSecurity;
        _smtpUser.Text = channel.SmtpUser;
        _smtpPass.Text = secret.SmtpPassword;
        _from.Text = channel.MailFrom;
        _to.Text = channel.MailTo;

        _preset.ItemsSource = WebhookPresets.All.Select(p => p.Name).ToList();
        var pi = Array.FindIndex(WebhookPresets.All, p => p.Name == channel.Preset);
        _preset.SelectedIndex = pi < 0 ? WebhookPresets.All.Length - 1 : pi;
        _presetHint.Text = WebhookPresets.All[_preset.SelectedIndex].Hint;
        _method.ItemsSource = new[] { "POST", "GET" };
        _method.SelectedIndex = channel.Method == "GET" ? 1 : 0;
        _ctype.Text = channel.ContentType;
        _url.Text = secret.Url;
        _headers.Text = secret.Headers;
        _body.Text = channel.BodyTemplate;

        _emailPanel.Children.Add(Ui.Row(L.T("SMTP server"), _smtpHost, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("Port"), _smtpPort, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("Encryption"), _smtpSec, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("User"), _smtpUser, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("Password"), _smtpPass, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("From"), _from, 200));
        _emailPanel.Children.Add(Ui.Row(L.T("To"), _to, 200));

        _hookPanel.Children.Add(Ui.Row(L.T("Template"), _preset, 200));
        _hookPanel.Children.Add(_presetHint);
        _hookPanel.Children.Add(Ui.Label(L.T("Address (URL) — stored separately from the configuration, as a secret")));
        _hookPanel.Children.Add(_url);
        var mrow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        mrow.Children.Add(Ui.Label(L.T("Method")));
        mrow.Children.Add(_method);
        mrow.Children.Add(Ui.Label("Content-Type"));
        mrow.Children.Add(_ctype);
        _hookPanel.Children.Add(mrow);
        _hookPanel.Children.Add(Ui.Label(L.T("Extra headers — also a secret (tokens, etc.)")));
        _hookPanel.Children.Add(_headers);
        _hookPanel.Children.Add(Ui.Label(L.T("Request body (for POST)")));
        _hookPanel.Children.Add(_body);
        _hookPanel.Children.Add(Ui.Hint(L.T("Placeholders: ") + AlertText.PlaceholdersHelp +
                                        L.T(" Values are escaped for the content type (JSON, form-urlencoded).")));

        var filters = new StackPanel { Spacing = 4 };
        filters.Children.Add(Ui.Header(L.T("What to send")));
        filters.Children.Add(_onDown);
        filters.Children.Add(_onDeg);
        filters.Children.Add(_onRec);
        filters.Children.Add(_onChecks);
        filters.Children.Add(_skipChildren);
        filters.Children.Add(Ui.Hint(L.T("State at program start is not sent. Changes that happen almost at the same time go in one message. Sending for a single device is turned off in its settings.")));

        var testBtn = new Button { Content = L.T("Send test message") };
        testBtn.Click += async (_, _) => await TestAsync(testBtn);
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        testRow.Children.Add(testBtn);
        testRow.Children.Add(_test);

        var body = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        body.Children.Add(Ui.Row(L.T("Channel type"), _kind, 200));
        body.Children.Add(Ui.Row(L.T("Name"), _name, 200));
        body.Children.Add(_enabled);
        body.Children.Add(_emailPanel);
        body.Children.Add(_hookPanel);
        body.Children.Add(_ignoreCert);
        body.Children.Add(filters);
        body.Children.Add(testRow);
        body.Children.Add(Ui.Hint(L.T("The program connects only to the server specified here: no proxy, no redirects, no downloading of certificates from the Internet.")));

        var buttons = Ui.ButtonRow(Ui.Btn(L.T("Save"), (_, _) => Save(), true), Ui.Btn(L.T("Cancel"), (_, _) => Close(false)));
        buttons.Margin = new Thickness(14, 6, 14, 14);
        _err.Margin = new Thickness(14, 10, 14, 0);
        var root = new DockPanel();
        DockPanel.SetDock(_err, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_err);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;

        _kind.SelectionChanged += (_, _) => UpdateKind();
        _preset.SelectionChanged += (_, _) => ApplyPreset();
        UpdateKind();
    }

    void UpdateKind()
    {
        bool email = _kind.SelectedIndex == 0;
        _emailPanel.IsVisible = email;
        _hookPanel.IsVisible = !email;
    }

    void ApplyPreset()
    {
        var i = _preset.SelectedIndex;
        if (i < 0) return;
        var p = WebhookPresets.All[i];
        _presetHint.Text = p.Hint;
        if (p.Name == L.T("Custom")) return;
        _url.Text = p.Url;
        _method.SelectedIndex = p.Method == "GET" ? 1 : 0;
        _ctype.Text = p.ContentType;
        _headers.Text = p.Headers;
        _body.Text = p.Body;
        _ignoreCert.IsChecked = p.IgnoreCert;
    }

    bool TryRead(AlertChannel t, ChannelSecret s, out string error)
    {
        error = "";
        bool email = _kind.SelectedIndex == 0;
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) { error = L.T("Enter the channel name"); return false; }
        int port = t.SmtpPort;
        if (email)
        {
            if ((_smtpHost.Text ?? "").Trim().Length == 0) { error = L.T("Enter the SMTP server"); return false; }
            if (!int.TryParse((_smtpPort.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            { error = L.T("Port: 1..65535"); return false; }
            if ((_from.Text ?? "").Trim().Length == 0) { error = L.T("Enter the sender address"); return false; }
            if ((_to.Text ?? "").Trim().Length == 0) { error = L.T("Enter the recipients"); return false; }
        }
        else
        {
            var url = (_url.Text ?? "").Trim();
            if (!Uri.TryCreate(url.Replace("{", "").Replace("}", ""), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            { error = L.T("The webhook address must be a full http:// or https:// URL"); return false; }
            if (url.Contains('<')) { error = L.T("Replace placeholders like <TOKEN> in the address with real values"); return false; }
            if ((_headers.Text ?? "").Contains('<')) { error = L.T("Replace placeholders like <TOKEN> in the headers with real values"); return false; }
        }

        t.Kind = email ? ChannelKind.Email : ChannelKind.Webhook;
        t.Name = name;
        t.Enabled = _enabled.IsChecked == true;
        t.OnDown = _onDown.IsChecked == true;
        t.OnDegraded = _onDeg.IsChecked == true;
        t.OnRecovery = _onRec.IsChecked == true;
        t.OnChecks = _onChecks.IsChecked == true;
        t.SkipUnreachableChildren = _skipChildren.IsChecked == true;
        t.IgnoreCertErrors = _ignoreCert.IsChecked == true;
        t.SmtpHost = (_smtpHost.Text ?? "").Trim();
        t.SmtpPort = port;
        t.SmtpSecurity = _smtpSec.SelectedIndex is >= 0 and <= 2 ? (SmtpSecurity)_smtpSec.SelectedIndex : SmtpSecurity.StartTls;
        t.SmtpUser = (_smtpUser.Text ?? "").Trim();
        t.MailFrom = (_from.Text ?? "").Trim();
        t.MailTo = (_to.Text ?? "").Trim();
        t.Preset = _preset.SelectedIndex >= 0 ? WebhookPresets.All[_preset.SelectedIndex].Name : "";
        t.Method = _method.SelectedIndex == 1 ? "GET" : "POST";
        t.ContentType = (_ctype.Text ?? "").Trim();
        t.BodyTemplate = _body.Text ?? "";
        s.SmtpPassword = _smtpPass.Text ?? "";
        s.Url = (_url.Text ?? "").Trim();
        s.Headers = _headers.Text ?? "";
        return true;
    }

    void Save()
    {
        if (!TryRead(_c, _s, out var e)) { _err.Text = e; return; }
        Close(true);
    }

    async Task TestAsync(Button b)
    {
        _err.Text = "";
        var tc = ConfigStore.Copy(_c);
        var ts = new ChannelSecret();
        if (!TryRead(tc, ts, out var e)) { _err.Text = e; return; }
        tc.Enabled = true;
        b.IsEnabled = false;
        _test.Text = L.T("Sending…");
        _test.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            await Task.Run(() => AlertDispatcher.TestAsync(_cfg, tc, ts));
            _test.Text = L.T("Sent successfully");
            _test.Foreground = Icons.Brush(DevStatus.Up);
        }
        catch (Exception ex)
        {
            _test.Text = L.T("Error: ") + (ex.InnerException?.Message ?? ex.Message);
            _test.Foreground = Icons.Brush(DevStatus.Down);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}
