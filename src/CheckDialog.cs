using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

/// <summary>Editor for one additional check (TCP port, HTTP(S) request, DNS query).</summary>
public sealed class CheckDialog : Window
{
    readonly ExtraCheck _c;
    readonly string _address;
    readonly ComboBox _kind = new() { Width = 240 };
    readonly TextBox _name = new() { Width = 300, PlaceholderText = L.T("optional") };
    readonly CheckBox _enabled = new() { Content = L.T("Check enabled") };
    readonly TextBox _interval = new() { Width = 100 };
    readonly TextBox _timeout = new() { Width = 100 };
    readonly TextBox _slow = new() { Width = 100 };
    readonly TextBox _confirm = new() { Width = 100 };
    readonly TextBox _port = new() { Width = 100 };
    readonly TextBox _url = new() { Width = 360, PlaceholderText = L.T("empty = http://address/") };
    readonly TextBox _codes = new() { Width = 160 };
    readonly TextBox _text = new() { Width = 300, PlaceholderText = L.T("optional") };
    readonly CheckBox _ignoreCert = new() { Content = L.T("Ignore HTTPS certificate errors (self-signed certificates of routers, cameras)") };
    readonly TextBox _dnsName = new() { Width = 300, PlaceholderText = L.T("for example, google.com") };
    readonly ComboBox _dnsType = new() { Width = 100 };
    readonly TextBox _dnsExpect = new() { Width = 200, PlaceholderText = L.T("optional") };
    readonly StackPanel _tcp = new() { Spacing = 6 };
    readonly StackPanel _http = new() { Spacing = 6 };
    readonly StackPanel _dns = new() { Spacing = 6 };
    readonly StackPanel _json = new() { Spacing = 6 };
    readonly TextBox _jUrl = new() { Width = 420, PlaceholderText = L.T("for example: http://ha.local:8123/api/states/sensor.server_room_temperature") };
    readonly TextBox _jHeaders = new() { Width = 420, Height = 54, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, PlaceholderText = L.T("Name: value (one per line), e.g. Authorization: Bearer <token>") };
    readonly TextBox _jPath = new() { Width = 300, PlaceholderText = L.T("for example: state or attributes.temperature or data[0].value") };
    readonly ComboBox _okOp = new() { Width = 170 };
    readonly TextBox _okA = new() { Width = 110 };
    readonly TextBox _okB = new() { Width = 110 };
    readonly ComboBox _warnOp = new() { Width = 170 };
    readonly TextBox _warnA = new() { Width = 110 };
    readonly TextBox _warnB = new() { Width = 110 };
    readonly CheckBox _jIgnoreCert = new() { Content = L.T("Ignore HTTPS certificate errors (self-signed certificates of routers, cameras)") };
    static readonly JsonOp[] OkOps = { JsonOp.Exists, JsonOp.Equals, JsonOp.NotEquals, JsonOp.Contains, JsonOp.Less, JsonOp.LessOrEqual, JsonOp.Greater, JsonOp.GreaterOrEqual, JsonOp.Between, JsonOp.Regex };
    static readonly JsonOp[] WarnOps = { JsonOp.None, JsonOp.Equals, JsonOp.NotEquals, JsonOp.Contains, JsonOp.Less, JsonOp.LessOrEqual, JsonOp.Greater, JsonOp.GreaterOrEqual, JsonOp.Between, JsonOp.Regex };
    readonly TextBlock _hint = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _err = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    readonly TextBlock _test = new() { TextWrapping = TextWrapping.Wrap };

    public CheckDialog(ExtraCheck c, string deviceAddress, bool primary = false)
    {
        _c = c;
        _address = deviceAddress;
        Title = primary ? L.T("Main check (defines the device state)") : L.T("Additional check");
        Width = 720;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        _kind.ItemsSource = new[] { L.T("TCP port"), L.T("HTTP(S) request"), L.T("DNS query"), L.T("HTTP JSON value") };
        _kind.SelectedIndex = (int)c.Kind;
        _dnsType.ItemsSource = new[] { "A", "AAAA" };
        _dnsType.SelectedIndex = c.DnsType == "AAAA" ? 1 : 0;
        _name.Text = c.Name;
        _enabled.IsChecked = c.Enabled;
        _interval.Text = I(c.IntervalSec);
        _timeout.Text = I(c.TimeoutMs);
        _slow.Text = I(c.SlowMs);
        _confirm.Text = I(c.ConfirmCycles);
        _port.Text = I(c.Port);
        _url.Text = c.Url;
        _codes.Text = c.ExpectCodes;
        _text.Text = c.ExpectText;
        _ignoreCert.IsChecked = c.IgnoreCertErrors;
        _dnsName.Text = c.DnsName;
        _jUrl.Text = c.Kind == CheckKind.Json ? c.Url : "";
        _jHeaders.Text = c.Headers;
        _jPath.Text = c.JsonPath;
        _okOp.ItemsSource = OkOps.Select(JsonValue.Symbol).ToList();
        _okOp.SelectedIndex = Math.Max(0, Array.IndexOf(OkOps, c.OkOp));
        _okA.Text = c.OkValue;
        _okB.Text = c.OkValue2;
        _warnOp.ItemsSource = WarnOps.Select(JsonValue.Symbol).ToList();
        _warnOp.SelectedIndex = Math.Max(0, Array.IndexOf(WarnOps, c.WarnOp));
        _warnA.Text = c.WarnValue;
        _warnB.Text = c.WarnValue2;
        _jIgnoreCert.IsChecked = c.IgnoreCertErrors;
        if (c.Kind == CheckKind.Json) _url.Text = "";
        _dnsExpect.Text = c.DnsExpect;

        _tcp.Children.Add(Ui.Row(L.T("Port"), _port, 240));

        _http.Children.Add(Ui.Row(L.T("Address (URL)"), _url, 240));
        _http.Children.Add(Ui.Row(L.T("Response codes treated as success"), _codes, 240));
        _http.Children.Add(Ui.Row(L.T("Text that must be in the response"), _text, 240));
        _http.Children.Add(_ignoreCert);

        _json.Children.Add(Ui.Row(L.T("Address (URL)"), _jUrl, 240));
        _json.Children.Add(Ui.Row(L.T("Request headers"), _jHeaders, 240));
        _json.Children.Add(Ui.Row(L.T("Path to the value"), _jPath, 240));
        var okRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        okRow.Children.Add(_okOp);
        okRow.Children.Add(_okA);
        okRow.Children.Add(_okB);
        _json.Children.Add(Ui.Row(L.T("\"Up\" when the value"), okRow, 240));
        var warnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        warnRow.Children.Add(_warnOp);
        warnRow.Children.Add(_warnA);
        warnRow.Children.Add(_warnB);
        _json.Children.Add(Ui.Row(L.T("otherwise \"Unstable\" when the value"), warnRow, 240));
        _json.Children.Add(Ui.Hint(L.T("Otherwise — \"Down\". Numbers are compared as numbers, text — without regard to case. \"between\" takes two values (inclusive).")));
        _json.Children.Add(_jIgnoreCert);
        _okOp.SelectionChanged += (_, _) => UpdateJsonFields();
        _warnOp.SelectionChanged += (_, _) => UpdateJsonFields();
        UpdateJsonFields();

        _dns.Children.Add(Ui.Row(L.T("Name to query"), _dnsName, 240));
        _dns.Children.Add(Ui.Row(L.T("Record type"), _dnsType, 240));
        _dns.Children.Add(Ui.Row(L.T("Expected address in the answer"), _dnsExpect, 240));

        var common = new StackPanel { Spacing = 6 };
        common.Children.Add(Ui.Row(L.T("Check type"), _kind, 240));
        common.Children.Add(Ui.Row(L.T("Name"), _name, 240));
        if (!primary) common.Children.Add(_enabled);
        else _enabled.IsChecked = true;
        common.Children.Add(Ui.Row(L.T("Interval, s"), _interval, 240));
        common.Children.Add(Ui.Row(L.T("Timeout, ms"), _timeout, 240));
        common.Children.Add(Ui.Row(L.T("\"Unstable\" when the response is slower than, ms (0 = off)"), _slow, 240));
        common.Children.Add(Ui.Row(L.T("Consecutive checks to change the state"), _confirm, 240));

        var testBtn = new Button { Content = L.T("Check now") };
        testBtn.Click += async (_, _) => await TestAsync(testBtn);
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        testRow.Children.Add(testBtn);
        testRow.Children.Add(_test);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
        body.Children.Add(_hint);
        body.Children.Add(common);
        body.Children.Add(_tcp);
        body.Children.Add(_http);
        body.Children.Add(_dns);
        body.Children.Add(_json);
        body.Children.Add(testRow);

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
        UpdateKind();
    }

    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    CheckKind Kind => _kind.SelectedIndex is >= 0 and <= 3 ? (CheckKind)_kind.SelectedIndex : CheckKind.Tcp;

    void UpdateJsonFields()
    {
        var ok = OkOps[Math.Max(0, _okOp.SelectedIndex)];
        _okA.IsVisible = ok != JsonOp.Exists;
        _okB.IsVisible = ok == JsonOp.Between;
        var warn = WarnOps[Math.Max(0, _warnOp.SelectedIndex)];
        _warnA.IsVisible = warn != JsonOp.None;
        _warnB.IsVisible = warn == JsonOp.Between;
    }

    void UpdateKind()
    {
        var k = Kind;
        _tcp.IsVisible = k == CheckKind.Tcp;
        _http.IsVisible = k == CheckKind.Http;
        _dns.IsVisible = k == CheckKind.Dns;
        _json.IsVisible = k == CheckKind.Json;
        _hint.Text = k switch
        {
            CheckKind.Tcp =>
                L.T("Checks whether the device accepts a TCP connection on the given port (like tcp-conn in MikroTik Netwatch): connection established — success; refused or no answer — \"Down\". No data is sent."),
            CheckKind.Http =>
                L.T("A GET request is sent. Success — if the response code is in the given range and (if set) the page contains the given text. Address: empty = http://<device address>/, \"/path\" = http://<device address>/path, or a full URL (http:// or https://). Redirects are not followed — 3xx codes are included in the default range 200-399."),
            CheckKind.Json =>
                L.T("A GET request is sent and a value is taken from the JSON response by a path — for example the state of a Home Assistant sensor (URL http://<HA>:8123/api/states/sensor.<name>, header \"Authorization: Bearer <long-lived token>\", path \"state\"). The value is compared with the conditions: \"Up\", otherwise \"Unstable\" (optional), otherwise \"Down\". Address: empty = http://<device address>/, \"/path\" or a full URL."),
            _ =>
                L.T("The device is queried as a DNS server (UDP port 53): a query for the given name is sent to it. Success — the server answered without an error and returned an A (IPv4) or AAAA (IPv6) record, and if an expected address is set — exactly that one. This checks that the DNS service on a router or server works and resolves names; the device's own name is not checked."),
        };
    }

    bool TryRead(ExtraCheck t, out string error)
    {
        static bool Int(string? s, int min, int max, out int v) =>
            int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

        error = "";
        if (!Int(_interval.Text, 5, 86400, out var interval)) { error = L.T("Interval: 5..86400 s"); return false; }
        if (!Int(_timeout.Text, 100, 60000, out var timeout)) { error = L.T("Timeout: 100..60000 ms"); return false; }
        if (!Int(_slow.Text, 0, 600000, out var slow)) { error = L.T("\"Unstable\" threshold: 0..600000 ms"); return false; }
        if (!Int(_confirm.Text, 1, 50, out var confirm)) { error = L.T("Consecutive checks: 1..50"); return false; }
        var k = Kind;
        int port = t.Port;
        if (k == CheckKind.Tcp && !Int(_port.Text, 1, 65535, out port)) { error = L.T("Port: 1..65535"); return false; }
        var url = (_url.Text ?? "").Trim();
        var codes = (_codes.Text ?? "").Trim();
        if (k == CheckKind.Http)
        {
            if (!CheckProbes.CodesValid(codes)) { error = L.T("Response codes: for example 200-399 or 200,204,301-302"); return false; }
            try { CheckProbes.BuildUri(_address.Length > 0 ? _address : "localhost", url); }
            catch (UriFormatException) { error = L.T("Invalid address (URL)"); return false; }
        }
        var jUrl = (_jUrl.Text ?? "").Trim();
        var okOp = OkOps[Math.Max(0, _okOp.SelectedIndex)];
        var warnOp = WarnOps[Math.Max(0, _warnOp.SelectedIndex)];
        if (k == CheckKind.Json)
        {
            try { CheckProbes.BuildUri(_address.Length > 0 ? _address : "localhost", jUrl); }
            catch (UriFormatException) { error = L.T("Invalid address (URL)"); return false; }
            if ((_jHeaders.Text ?? "").Contains('<')) { error = L.T("Replace placeholders like <TOKEN> in the headers with real values"); return false; }
            if (okOp != JsonOp.Exists && (_okA.Text ?? "").Trim().Length == 0) { error = L.T("Enter the value for the \"Up\" condition"); return false; }
            if (okOp == JsonOp.Between && (_okB.Text ?? "").Trim().Length == 0) { error = L.T("\"between\" needs two values"); return false; }
            if (warnOp != JsonOp.None && (_warnA.Text ?? "").Trim().Length == 0) { error = L.T("Enter the value for the \"Unstable\" condition"); return false; }
            if (warnOp == JsonOp.Between && (_warnB.Text ?? "").Trim().Length == 0) { error = L.T("\"between\" needs two values"); return false; }
        }
        var dnsName = (_dnsName.Text ?? "").Trim();
        var dnsExpect = (_dnsExpect.Text ?? "").Trim();
        if (k == CheckKind.Dns)
        {
            if (dnsName.Length == 0) { error = L.T("Enter the name for the DNS query"); return false; }
            if (dnsExpect.Length > 0 && !IPAddress.TryParse(dnsExpect, out _)) { error = L.T("The expected address must be an IP address"); return false; }
        }

        t.Kind = k;
        t.Name = (_name.Text ?? "").Trim();
        t.Enabled = _enabled.IsChecked == true;
        t.IntervalSec = interval;
        t.TimeoutMs = timeout;
        t.SlowMs = slow;
        t.ConfirmCycles = confirm;
        t.Port = port;
        t.Url = k == CheckKind.Json ? jUrl : url;
        t.Headers = _jHeaders.Text ?? "";
        t.JsonPath = (_jPath.Text ?? "").Trim();
        t.OkOp = okOp;
        t.OkValue = (_okA.Text ?? "").Trim();
        t.OkValue2 = (_okB.Text ?? "").Trim();
        t.WarnOp = warnOp;
        t.WarnValue = (_warnA.Text ?? "").Trim();
        t.WarnValue2 = (_warnB.Text ?? "").Trim();
        t.ExpectCodes = codes.Length == 0 ? "200-399" : codes;
        t.ExpectText = _text.Text ?? "";
        t.IgnoreCertErrors = k == CheckKind.Json ? _jIgnoreCert.IsChecked == true : _ignoreCert.IsChecked == true;
        t.DnsName = dnsName;
        t.DnsType = _dnsType.SelectedIndex == 1 ? "AAAA" : "A";
        t.DnsExpect = dnsExpect;
        return true;
    }

    void Save()
    {
        if (!TryRead(_c, out var e)) { _err.Text = e; return; }
        Close(true);
    }

    async Task TestAsync(Button b)
    {
        _err.Text = "";
        if (_address.Length == 0) { _err.Text = L.T("Enter the device address first"); return; }
        var tmp = ConfigStore.Copy(_c);
        if (!TryRead(tmp, out var e)) { _err.Text = e; return; }
        b.IsEnabled = false;
        _test.Text = L.T("Checking…");
        _test.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            var r = await Task.Run(() => CheckProbes.RunAsync(new Device { Address = _address, Name = _address }, tmp, null, CancellationToken.None));
            _test.Text = (r.Ok ? L.T("Success: ") : L.T("Error: ")) + r.Detail;
            _test.Foreground = Icons.Brush(r.Raw);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}
