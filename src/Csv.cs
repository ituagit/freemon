using System.Globalization;
using System.Text;

namespace FreeMon;

public static class Csv
{
    public static char DetectDelimiter(string line)
    {
        int sc = line.Count(c => c == ';');
        int cc = line.Count(c => c == ',');
        int tc = line.Count(c => c == '\t');
        if (tc > sc && tc > cc) return '\t';
        return sc >= cc ? ';' : ',';
    }

    public static string Quote(string? s, char d)
    {
        s ??= "";
        return s.IndexOfAny(new[] { d, '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static List<List<string>> Parse(string text, char d)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    inQ = false; i++; continue;
                }
                sb.Append(c); i++; continue;
            }
            if (c == '"') { inQ = true; i++; continue; }
            if (c == d) { row.Add(sb.ToString()); sb.Clear(); i++; continue; }
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                row.Add(sb.ToString()); sb.Clear();
                rows.Add(row); row = new List<string>();
                i++; continue;
            }
            sb.Append(c); i++;
        }
        if (sb.Length > 0 || row.Count > 0)
        {
            row.Add(sb.ToString());
            rows.Add(row);
        }
        return rows;
    }
}

public sealed record ImportReport(int Added, int Updated, List<string> Errors);

public static class DeviceCsv
{
    static readonly string[] BaseCols = { "name", "address", "type", "parent_address", "group", "description", "enabled" };

    public static string Export(AppConfig cfg)
    {
        const char d = ';';
        var sb = new StringBuilder();
        sb.Append(string.Join(d, BaseCols.Concat(ProfileFields.All.Select(f => f.Key)))).Append("\r\n");
        var byId = cfg.Devices.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var dev in cfg.Devices)
        {
            var parent = dev.ParentId is not null && byId.TryGetValue(dev.ParentId, out var p) ? p.Address : "";
            var vals = new List<string> { dev.Name, dev.Address, dev.Type, parent, dev.Group, dev.Description, dev.Enabled ? "1" : "0" };
            vals.AddRange(ProfileFields.All.Select(f => f.Get(dev.Profile)?.ToString(CultureInfo.InvariantCulture) ?? ""));
            sb.Append(string.Join(d, vals.Select(v => Csv.Quote(v, d)))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Merge by address: existing devices are updated, new ones added. Columns that are absent are not touched.</summary>
    public static ImportReport Import(AppConfig cfg, string text)
    {
        var errors = new List<string>();
        int added = 0, updated = 0;
        text = text.TrimStart('\uFEFF');
        var nl = text.IndexOf('\n');
        var delim = Csv.DetectDelimiter(nl < 0 ? text : text[..nl]);
        var rows = Csv.Parse(text, delim);
        if (rows.Count == 0)
        {
            errors.Add(L.T("The file is empty"));
            return new ImportReport(0, 0, errors);
        }

        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Count; i++)
        {
            var k = rows[0][i].Trim();
            if (k.Length > 0 && !idx.ContainsKey(k)) idx[k] = i;
        }
        if (!idx.ContainsKey("name") || !idx.ContainsKey("address"))
        {
            errors.Add(L.T("The header lacks the required columns name and address"));
            return new ImportReport(0, 0, errors);
        }

        bool hasParent = idx.ContainsKey("parent_address");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<(Device Dev, string ParentAddr, int Line)>();

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            int line = r + 1;
            string G(string col) => idx.TryGetValue(col, out var k) && k < row.Count ? row[k].Trim() : "";

            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var name = G("name");
            var addr = G("address");
            if (name.Length == 0) { errors.Add(L.F("Line {0}: empty name", line)); continue; }
            if (!AddressUtil.IsValid(addr)) { errors.Add(L.F("Line {0}: invalid address '{1}'", line, addr)); continue; }
            if (!seen.Add(addr)) { errors.Add(L.F("Line {0}: address {1} is repeated in the file", line, addr)); continue; }

            var prof = new PingProfile();
            string? perr = null;
            foreach (var f in ProfileFields.All)
            {
                if (!idx.ContainsKey(f.Key)) continue;
                var s = G(f.Key);
                if (s.Length == 0) { f.Set(prof, null); continue; }
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < f.Min || v > f.Max)
                {
                    perr = L.F("Line {0}: {1}='{2}' (allowed {3}..{4})", line, f.Key, s, f.Min, f.Max);
                    break;
                }
                f.Set(prof, v);
            }
            if (perr is not null) { errors.Add(perr); continue; }

            bool enabled = true;
            var en = G("enabled").ToLowerInvariant();
            if (en.Length > 0)
            {
                if (en is "1" or "true" or "так" or "yes" or "y" or "+") enabled = true;
                else if (en is "0" or "false" or "ні" or "no" or "n" or "-") enabled = false;
                else { errors.Add(L.F("Line {0}: enabled='{1}'", line, en)); continue; }
            }

            var dev = cfg.Devices.FirstOrDefault(x => string.Equals(x.Address.Trim(), addr, StringComparison.OrdinalIgnoreCase));
            if (dev is null)
            {
                dev = new Device();
                cfg.Devices.Add(dev);
                added++;
            }
            else
            {
                updated++;
            }

            dev.Name = name;
            dev.Address = addr;
            if (idx.ContainsKey("type")) dev.Type = G("type");
            if (idx.ContainsKey("group")) dev.Group = G("group");
            if (idx.ContainsKey("description")) dev.Description = G("description");
            if (idx.ContainsKey("enabled")) dev.Enabled = enabled;
            foreach (var f in ProfileFields.All)
                if (idx.ContainsKey(f.Key)) f.Set(dev.Profile, f.Get(prof));

            if (dev.Type.Length > 0 && ProfileResolver.TypeProfile(cfg, dev.Type) is null)
                cfg.Types.Add(new DeviceType { Name = dev.Type });

            if (hasParent) links.Add((dev, G("parent_address"), line));
        }

        foreach (var (dev, pa, line) in links)
        {
            if (pa.Length == 0) { dev.ParentId = null; continue; }
            var parent = cfg.Devices.FirstOrDefault(x => string.Equals(x.Address.Trim(), pa, StringComparison.OrdinalIgnoreCase));
            if (parent is null)
            {
                errors.Add(L.F("Line {0}: parent device {1} not found", line, pa));
                dev.ParentId = null;
                continue;
            }
            if (CreatesCycle(cfg, dev, parent))
            {
                errors.Add(L.F("Line {0}: circular dependency with {1}", line, pa));
                dev.ParentId = null;
                continue;
            }
            dev.ParentId = parent.Id;
        }

        return new ImportReport(added, updated, errors);
    }

    public static bool CreatesCycle(AppConfig cfg, Device dev, Device parent)
    {
        var byId = cfg.Devices.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        Device? cur = parent;
        for (int i = 0; i < 64 && cur is not null; i++)
        {
            if (cur.Id == dev.Id) return true;
            cur = cur.ParentId is not null && byId.TryGetValue(cur.ParentId, out var p) ? p : null;
        }
        return false;
    }
}
