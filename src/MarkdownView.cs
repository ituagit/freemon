using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMon;

/// <summary>
/// Minimal Markdown renderer for the README: headings, paragraphs, bullet lists, tables, code blocks,
/// **bold** / `code` / [links](url) shown as plain text. Enough for our own documents, no extra dependency.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    static readonly FontFamily Mono = new("Consolas, DejaVu Sans Mono, Liberation Mono, monospace");

    public MarkdownView(string markdown)
    {
        Spacing = 6;
        Margin = new Thickness(16, 12);
        var lines = markdown.Replace("\r", "").Split('\n');
        var para = new List<string>();
        List<string>? code = null;
        var table = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            Children.Add(Text(string.Join(" ", para)));
            para.Clear();
        }

        void FlushTable()
        {
            if (table.Count == 0) return;
            var rows = table.Where(r => !Regex.IsMatch(r, @"^\|?\s*:?-{3,}")).Select(Cells).ToList();
            int cols = rows.Max(r => r.Count);
            var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(c == cols - 1 ? GridLength.Star : GridLength.Auto));
            for (int r = 0; r < rows.Count; r++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var t = Text(rows[r][c]);
                    t.Margin = new Thickness(0, 2, 18, 2);
                    if (r == 0) t.FontWeight = FontWeight.SemiBold;
                    Grid.SetRow(t, r);
                    Grid.SetColumn(t, c);
                    g.Children.Add(t);
                }
            }
            Children.Add(g);
            table.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```"))
            {
                if (code is null)
                {
                    FlushPara();
                    FlushTable();
                    code = new List<string>();
                }
                else
                {
                    Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                        Padding = new Thickness(10, 6),
                        CornerRadius = new CornerRadius(4),
                        Child = new TextBlock { Text = string.Join("\n", code), FontFamily = Mono, FontSize = 12 },
                    });
                    code = null;
                }
                continue;
            }
            if (code is not null)
            {
                code.Add(raw);
                continue;
            }
            if (line.StartsWith('|'))
            {
                FlushPara();
                table.Add(line);
                continue;
            }
            FlushTable();
            if (line.Length == 0)
            {
                FlushPara();
                continue;
            }
            if (line.StartsWith('#'))
            {
                FlushPara();
                int level = line.TakeWhile(ch => ch == '#').Count();
                var h = Text(line[level..].Trim());
                h.FontWeight = FontWeight.Bold;
                h.FontSize = level switch { 1 => 22, 2 => 17, _ => 14 };
                h.Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 2);
                Children.Add(h);
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushPara();
                var row = new DockPanel();
                var bullet = new TextBlock { Text = "•", Width = 16 };
                DockPanel.SetDock(bullet, Dock.Left);
                row.Children.Add(bullet);
                row.Children.Add(Text(line[2..]));
                Children.Add(row);
                continue;
            }
            if (raw.StartsWith("  ") && Children.LastOrDefault() is DockPanel prev && para.Count == 0)
            {
                // continuation of the previous bullet
                if (prev.Children.LastOrDefault() is TextBlock tb) tb.Text += " " + Inline(line.Trim());
                continue;
            }
            para.Add(line.Trim());
        }
        FlushPara();
        FlushTable();
    }

    static List<string> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    static string Inline(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1");
        return s.Replace("**", "").Replace("`", "");
    }

    static TextBlock Text(string s) => new() { Text = Inline(s), TextWrapping = TextWrapping.Wrap };
}
