using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

public sealed class AboutWindow : Window
{
    public const int ReadmeTab = 1;
    public const int ManualTab = 2;
    public const int EncryptionTab = 4;

    public AboutWindow(int selectTab = 0)
    {
        Title = L.T("About");
        Width = 760;
        Height = 620;
        MinWidth = 520;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppInfo.Icon;

        var logo = new Image { Source = null, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top };
        using (var s = typeof(AppInfo).Assembly.GetManifestResourceStream("freemon.png"))
            if (s is not null) logo.Source = new Avalonia.Media.Imaging.Bitmap(s);

        var title = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 0, 0) };
        title.Children.Add(new TextBlock { Text = "FreeMon", FontSize = 22, FontWeight = FontWeight.Bold });
        title.Children.Add(new TextBlock { Text = L.T("Network device availability monitoring"), Opacity = 0.8 });
        title.Children.Add(new TextBlock { Text = L.F("Version {0}", AppInfo.Version) });
        title.Children.Add(new TextBlock { Text = L.F("© {0} {1}. License: {2}.", AppInfo.Year, AppInfo.Author, AppInfo.LicenseName) });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(logo);
        head.Children.Add(title);

        string info =
            L.F("Data: {0} ({1})\nConfiguration: {2}\nLog: {3}, {4}\nPing: {5}\nNetwork: see the \"Network policy\" tab\nRendering: {6}\nRuntime: {7}\nAutostart: {8} — {9}", Paths.DataDir, Paths.KindText(Paths.Kind), Paths.ConfigFile, Paths.DbFile, Paths.EventsFile, ProbeFactory.Mode, (AppInfo.SoftwareRender ? L.T("software") : L.T("hardware (default)")), AppInfo.Runtime, (Autostart.IsEnabled() ? L.T("enabled") : L.T("disabled")), Autostart.Where);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = L.T("Information"), Content = Box(info) });
        var readme = AppInfo.Resource(L.Language == "uk" ? "README.uk.md" : "README.md");
        tabs.Items.Add(new TabItem { Header = L.T("Features (README)"), Content = new ScrollViewer { Content = new MarkdownView(readme) } });
        var manual = AppInfo.Resource(L.Language == "uk" ? "MANUAL.uk.md" : "MANUAL.md");
        tabs.Items.Add(new TabItem { Header = L.T("User manual"), Content = new ScrollViewer { Content = new MarkdownView(manual) } });
        tabs.Items.Add(new TabItem { Header = L.T("Network policy"), Content = Box(NetworkPolicy.Uk + "\n\n\n" + NetworkPolicy.En) });
        tabs.Items.Add(new TabItem { Header = L.T("Encryption policy"), Content = Box(EncryptionPolicy.Uk + "\n\n\n" + EncryptionPolicy.En) });
        tabs.Items.Add(new TabItem { Header = L.T("FreeMon license"), Content = Box(AppInfo.Resource("LICENSE.txt")) });
        tabs.Items.Add(new TabItem { Header = L.T("Third-party components"), Content = Box(AppInfo.Resource("THIRD_PARTY_NOTICES.txt")) });

        var close = Ui.ButtonRow(Ui.Btn(L.T("Close"), (_, _) => Close(), true));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(close);
        root.Children.Add(tabs);
        tabs.SelectedIndex = Math.Clamp(selectTab, 0, tabs.Items.Count - 1);
        Content = root;
    }

    static Control Box(string text) => new TextBox
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Liberation Mono, monospace"),
        FontSize = 12,
    };
}
