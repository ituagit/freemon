using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FreeMon;

/// <summary>Status colors and tray/window icons generated in code (no binary assets).</summary>
public static class Icons
{
    static readonly Dictionary<DevStatus, WindowIcon> IconCache = new();
    static readonly Dictionary<DevStatus, IBrush> BrushCache = new();
    static readonly uint[] CrcTable = MakeCrcTable();

    public static (byte R, byte G, byte B) Rgb(DevStatus s) => s switch
    {
        DevStatus.Up => (0x3B, 0x8C, 0x2A),
        DevStatus.Degraded => (0xE0, 0x9A, 0x1E),
        DevStatus.Down => (0xD9, 0x3A, 0x3A),
        DevStatus.Unreachable => (0x9E, 0x7A, 0x7A),
        DevStatus.Disabled => (0x70, 0x70, 0x70),
        _ => (0xA8, 0xA8, 0xA8),
    };

    public static IBrush Brush(DevStatus s)
    {
        if (!BrushCache.TryGetValue(s, out var b))
        {
            var (r, g, bl) = Rgb(s);
            b = new SolidColorBrush(Color.FromRgb(r, g, bl));
            BrushCache[s] = b;
        }
        return b;
    }

    public static WindowIcon ForStatus(DevStatus s)
    {
        if (IconCache.TryGetValue(s, out var icon)) return icon;
        var (r, g, b) = Rgb(s);
        using var ms = new MemoryStream(CirclePng(32, r, g, b));
        icon = new WindowIcon(new Bitmap(ms));
        IconCache[s] = icon;
        return icon;
    }

    static byte[] CirclePng(int n, byte r, byte g, byte b)
    {
        var rgba = new byte[n * n * 4];
        double c = (n - 1) / 2.0;
        double rad = n / 2.0 - 1.0;
        byte dr = (byte)(r * 0.6), dg = (byte)(g * 0.6), db = (byte)(b * 0.6);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                double dist = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                double a = Math.Clamp(rad + 0.5 - dist, 0, 1);
                bool edge = dist > rad - 2.5;
                int i = (y * n + x) * 4;
                rgba[i] = edge ? dr : r;
                rgba[i + 1] = edge ? dg : g;
                rgba[i + 2] = edge ? db : b;
                rgba[i + 3] = (byte)(a * 255);
            }
        }
        return EncodePng(n, n, rgba);
    }

    static byte[] EncodePng(int w, int h, byte[] rgba)
    {
        int stride = w * 4;
        var raw = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (stride + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] z;
        using (var zms = new MemoryStream())
        {
            using (var zs = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw, 0, raw.Length);
            z = zms.ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        var ihdr = new byte[13];
        Be(ihdr, 0, w);
        Be(ihdr, 4, h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z);
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void Be(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        Be(len, 0, data.Length);
        s.Write(len, 0, 4);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        var cb = new byte[4];
        Be(cb, 0, unchecked((int)crc));
        s.Write(cb, 0, 4);
    }

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
