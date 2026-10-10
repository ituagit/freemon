using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace FreeMon;

public sealed record ReportMeta(string Organization, string PreparedBy, string Comment,
    string ParentName, string TypeName, string ProfileText);

/// <summary>Noto Sans (SIL OFL 1.1) embedded as resources: same PDF on Windows and Linux, Cyrillic included.</summary>
sealed class EmbeddedFontResolver : IFontResolver
{
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "NotoSans-Bold" : "NotoSans-Regular", false, italic);

    public byte[]? GetFont(string faceName)
    {
        using var s = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(faceName + ".ttf");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

public static class ReportPdf
{
    const string FontName = "Noto Sans";
    static readonly object FontGate = new();
    static bool _fontsReady;

    static void EnsureFonts()
    {
        lock (FontGate)
        {
            if (_fontsReady) return;
            GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
            _fontsReady = true;
        }
    }

    static Color Rgb(DevStatus s)
    {
        var (r, g, b) = Icons.Rgb(s);
        return new Color(r, g, b);
    }

    public static byte[] Build(HistoryResult r, ReportMeta meta, byte[] chartPng)
    {
        EnsureFonts();
        var d = r.Device;
        var doc = new Document();
        doc.Info.Title = L.RF("Availability report: {0}", d.Name);
        doc.Info.Author = string.IsNullOrWhiteSpace(meta.PreparedBy) ? "FreeMon" : meta.PreparedBy;

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontName;
        normal.Font.Size = 10;

        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.LeftMargin = Unit.FromCentimeter(2.0);
        sec.PageSetup.RightMargin = Unit.FromCentimeter(1.5);

        var foot = sec.Footers.Primary.AddParagraph();
        foot.Format.Font.Size = 8;
        foot.Format.Font.Color = Colors.Gray;
        foot.Format.Alignment = ParagraphAlignment.Center;
        foot.AddText(Safe(L.RF("FreeMon · generated {0:dd.MM.yyyy HH:mm} · page ", DateTime.Now)));
        foot.AddPageField();
        foot.AddText(Safe(L.R(" of ")));
        foot.AddNumPagesField();

        if (!string.IsNullOrWhiteSpace(meta.Organization))
        {
            var org = sec.AddParagraph(Safe(meta.Organization.Trim()));
            org.Format.Font.Size = 11;
            org.Format.Font.Bold = true;
            org.Format.SpaceAfter = Unit.FromPoint(6);
        }

        var title = sec.AddParagraph(Safe(L.R("Network device availability report")));
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(2);
        var sub = sec.AddParagraph(Safe(L.RF("Period: {0}", HistoryText.Period(r))));
        sub.Format.Font.Size = 11;
        sub.Format.SpaceAfter = Unit.FromPoint(10);

        // --- device info
        var info = sec.AddTable();
        info.Borders.Width = 0.5;
        info.Borders.Color = Colors.LightGray;
        info.AddColumn(Unit.FromCentimeter(4.5));
        info.AddColumn(Unit.FromCentimeter(12.9));
        void Info(string k, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var row = info.AddRow();
            row.TopPadding = Unit.FromPoint(2);
            row.BottomPadding = Unit.FromPoint(2);
            row.Cells[0].Shading.Color = new Color(0xF2, 0xF2, 0xF2);
            row.Cells[0].AddParagraph(Safe(k));
            row.Cells[1].AddParagraph(Safe(v));
        }
        Info(L.R("Device"), d.Name);
        Info(L.R("Address"), d.Address);
        Info(L.R("Type"), meta.TypeName);
        Info(L.R("Parent device"), meta.ParentName);
        Info(L.R("Description"), d.Description);
        Info(L.R("Check parameters"), meta.ProfileText);

        // --- summary
        Heading(sec, L.R("Summary"));
        sec.AddParagraph(Safe(HistoryText.Summary(r))).Format.SpaceAfter = Unit.FromPoint(8);

        var kpi = sec.AddTable();
        kpi.Borders.Width = 0.5;
        kpi.Borders.Color = Colors.LightGray;
        for (int i = 0; i < 4; i++) kpi.AddColumn(Unit.FromCentimeter(4.35));
        var kh = kpi.AddRow();
        var kv = kpi.AddRow();
        var downs = r.Incidents.Count(i => i.Kind == DevStatus.Down);
        (string Label, string Value, Color Col)[] k =
        {
            (L.R("Online"), r.AvailabilityPct is double a ? HistoryText.Num(a) + "%" : "—",
                r.AvailabilityPct is double a2 && a2 >= 99.9 ? Rgb(DevStatus.Up) : r.AvailabilityPct is double a3 && a3 >= 99 ? Rgb(DevStatus.Degraded) : Rgb(DevStatus.Down)),
            (L.R("Connection outages"), downs.ToString(), downs == 0 ? Rgb(DevStatus.Up) : Rgb(DevStatus.Down)),
            (L.R("Total downtime"), r.ObservedSec > 0 ? HistoryText.Dur(r.DownSec) : "—", r.DownSec > 0 ? Rgb(DevStatus.Down) : Rgb(DevStatus.Up)),
            (L.R("Average latency"), r.AvgRtt is double rtt ? HistoryText.Num(rtt, "0.#") + L.R(" ms") : "—", Colors.Black),
        };
        for (int i = 0; i < 4; i++)
        {
            var p1 = kh.Cells[i].AddParagraph(Safe(k[i].Label));
            p1.Format.Font.Size = 8;
            p1.Format.Font.Color = Colors.Gray;
            p1.Format.Alignment = ParagraphAlignment.Center;
            var p2 = kv.Cells[i].AddParagraph(Safe(k[i].Value));
            p2.Format.Font.Size = 14;
            p2.Format.Font.Bold = true;
            p2.Format.Font.Color = k[i].Col;
            p2.Format.Alignment = ParagraphAlignment.Center;
        }

        // --- chart
        Heading(sec, L.R("Chart for the period"));
        var img = sec.AddImage("base64:" + Convert.ToBase64String(chartPng));
        img.Width = Unit.FromCentimeter(17.4);
        img.LockAspectRatio = true;

        // --- incidents (with monitoring gaps in the same chronological table)
        Heading(sec, L.R("Incidents"));
        var rows = r.Incidents.Select(i => (Start: i.StartUtc, Inc: (Incident?)i, Gap: ((DateTime S, DateTime E)?)null))
            .Concat(r.Gaps.Select(g => (Start: g.StartUtc, Inc: (Incident?)null, Gap: ((DateTime S, DateTime E)?)(g.StartUtc, g.EndUtc))))
            .OrderBy(x => x.Start)
            .ToList();
        if (rows.Count == 0)
        {
            sec.AddParagraph(Safe(L.R("No incidents recorded.")));
        }
        else
        {
            var t = sec.AddTable();
            t.Borders.Width = 0.5;
            t.Borders.Color = Colors.LightGray;
            t.AddColumn(Unit.FromCentimeter(0.9));
            t.AddColumn(Unit.FromCentimeter(5.6));
            t.AddColumn(Unit.FromCentimeter(3.5));
            t.AddColumn(Unit.FromCentimeter(3.5));
            t.AddColumn(Unit.FromCentimeter(3.9));
            var hr = t.AddRow();
            hr.HeadingFormat = true;
            hr.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
            string[] heads = { "№", L.R("Event"), L.R("Start"), L.R("End"), L.R("Duration") };
            for (int i = 0; i < heads.Length; i++)
            {
                var hp = hr.Cells[i].AddParagraph(Safe(heads[i]));
                hp.Format.Font.Bold = true;
            }
            const int maxRows = 300;
            int no = 0;
            foreach (var x in rows.Take(maxRows))
            {
                no++;
                var row = t.AddRow();
                row.Cells[0].AddParagraph(no.ToString());
                if (x.Inc is { } inc)
                {
                    var kp = row.Cells[1].AddParagraph(Safe(HistoryText.KindText(inc) + (inc.HasGap ? L.R(" (with a monitoring gap)") : "")));
                    kp.Format.Font.Color = Rgb(inc.Kind);
                    row.Cells[2].AddParagraph(Safe(inc.StartedBefore ? L.R("before the start of the period") : inc.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
                    row.Cells[3].AddParagraph(Safe(inc.Ongoing ? L.R("ongoing") : inc.EndUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
                    row.Cells[4].AddParagraph(Safe(HistoryText.Dur(inc.DurationSec)));
                }
                else if (x.Gap is { } g)
                {
                    row.Shading.Color = new Color(0xF4, 0xF4, 0xF4);
                    var kp = row.Cells[1].AddParagraph(Safe(L.R("No monitoring")));
                    kp.Format.Font.Color = new Color(0x80, 0x80, 0x80);
                    kp.Format.Font.Italic = true;
                    row.Cells[2].AddParagraph(g.S.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
                    row.Cells[3].AddParagraph(g.E.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
                    row.Cells[4].AddParagraph(Safe(HistoryText.Dur((g.E - g.S).TotalSeconds)));
                }
            }
            if (rows.Count > maxRows)
                sec.AddParagraph(Safe(L.RF("… and {0} more. The full list is in the raw log (TXT).", rows.Count - maxRows)));
        }

        // --- monitoring gaps
        if (r.Gaps.Count > 0)
        {
            Heading(sec, L.R("Monitoring gaps"));
            var gp = sec.AddParagraph(Safe(L.R("FreeMon was not running or monitoring of the device was off. There is no data for these periods and they are not counted in availability.")));
            gp.Format.SpaceAfter = Unit.FromPoint(4);
            var gt = sec.AddTable();
            gt.Borders.Width = 0.5;
            gt.Borders.Color = Colors.LightGray;
            gt.AddColumn(Unit.FromCentimeter(0.9));
            gt.AddColumn(Unit.FromCentimeter(5.5));
            gt.AddColumn(Unit.FromCentimeter(5.5));
            gt.AddColumn(Unit.FromCentimeter(5.5));
            var ghr = gt.AddRow();
            ghr.HeadingFormat = true;
            ghr.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
            string[] gheads = { "№", L.R("Start"), L.R("End"), L.R("Duration") };
            for (int i = 0; i < gheads.Length; i++) ghr.Cells[i].AddParagraph(Safe(gheads[i])).Format.Font.Bold = true;
            const int maxGaps = 50;
            int gno = 0;
            foreach (var (gs, ge) in r.Gaps.Take(maxGaps))
            {
                gno++;
                var row = gt.AddRow();
                row.Cells[0].AddParagraph(Safe(gno.ToString()));
                row.Cells[1].AddParagraph(Safe(gs.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
                row.Cells[2].AddParagraph(Safe(ge.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
                row.Cells[3].AddParagraph(Safe(HistoryText.Dur((ge - gs).TotalSeconds)));
            }
            if (r.Gaps.Count > maxGaps)
                sec.AddParagraph(Safe(L.RF("… and {0} more. The full list is in the raw log (TXT).", r.Gaps.Count - maxGaps)));
        }

        // --- comment
        if (!string.IsNullOrWhiteSpace(meta.Comment))
        {
            Heading(sec, L.R("Comment"));
            var cp = sec.AddParagraph();
            var lines = meta.Comment.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) cp.AddLineBreak();
                cp.AddText(Safe(lines[i]));
            }
        }

        // --- authenticity
        Heading(sec, L.R("Report authenticity"));
        var ip = sec.AddParagraph(Safe(HistoryText.IntegrityText(r)));
        ip.Format.SpaceAfter = Unit.FromPoint(4);
        if (r.Integrity is { Ok: false }) ip.Format.Font.Color = Rgb(DevStatus.Down);
        var codeP = sec.AddParagraph();
        codeP.Format.Font.Size = 8;
        codeP.AddText(Safe(L.R("Verification code: ")));
        codeP.AddFormattedText(Safe(ReportCode.Create(r)), TextFormat.Bold);
        codeP.AddLineBreak();
        codeP.AddText(Safe(L.R("To verify: FreeMon → \"History and reports\" → \"Verify report…\" — the program recalculates the figures from the log and compares them with the code.")));

        // --- signature
        var sig = sec.AddParagraph();
        sig.Format.SpaceBefore = Unit.FromPoint(24);
        sig.AddText(Safe(L.R("Prepared by: ") + (string.IsNullOrWhiteSpace(meta.PreparedBy) ? "______________________" : meta.PreparedBy.Trim())));
        sig.AddText(Safe(L.R("          Signature: ______________          Date: ______________")));

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>The embedded Noto Sans has no arrows or symbols: replace them so the PDF shows no empty boxes.</summary>
    static string Safe(string? s) => (s ?? "")
        .Replace("→", "›").Replace("←", "‹").Replace("↺", "").Replace("⤢", "").Replace("▾", "").Replace("▸", "").Replace("⚠", "!");

    static void Heading(Section sec, string text)
    {
        var p = sec.AddParagraph(Safe(text));
        p.Format.Font.Size = 12;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(12);
        p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.KeepWithNext = true;
    }
}
