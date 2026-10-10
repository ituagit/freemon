using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace FreeMon;

/// <summary>
/// Installation key: 256 random bits created on first start, stored wrapped (bound to this computer / account).
/// Everything else (config, secrets, log seals, report codes) uses keys derived from it.
/// </summary>
public static class InstallKey
{
    static readonly byte[] Magic = "FMK1"u8.ToArray();
    static readonly byte[] AppSalt = SHA256.HashData("FreeMon install key wrap v1"u8.ToArray());
    static byte[]? _key;

    public static string FilePath => Path.Combine(Paths.DataDir, "install.key");

    public static bool Exists => File.Exists(FilePath);

    public static bool Loaded => _key is not null;

    /// <summary>Loads the key; creates a new one only when createIfMissing (new installation).</summary>
    public static void Ensure(bool createIfMissing)
    {
        if (_key is not null) return;
        if (File.Exists(FilePath))
        {
            _key = Unwrap(File.ReadAllBytes(FilePath));
            return;
        }
        if (!createIfMissing) throw new InstallKeyMissingException();
        var k = RandomNumberGenerator.GetBytes(32);
        Store(k);
        _key = k;
    }

    /// <summary>Replaces the key (restore from backup) and stores it bound to this computer.</summary>
    public static void Replace(byte[] raw)
    {
        Store(raw);
        _key = raw.ToArray();
    }

    public static byte[] Raw => _key ?? throw new InvalidOperationException("install key not loaded");

    static void Store(byte[] raw)
    {
        var wrapped = Wrap(raw);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, wrapped);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, FilePath, true);
    }

    static byte[] Wrap(byte[] raw)
    {
        byte mode;
        byte[] body;
        if (OperatingSystem.IsWindows())
        {
            mode = Paths.Kind == StorageKind.AllUsers ? (byte)2 : (byte)1;
            body = WinProtect(raw, mode == 2);
        }
        else
        {
            mode = 3;
            body = Aead.Encrypt(HostKey(), raw, "install-key");
        }
        return Magic.Concat(new[] { mode }).Concat(body).ToArray();
    }

    static byte[] Unwrap(byte[] data)
    {
        if (data.Length < 6 || !data.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("bad key file");
        var body = data.AsSpan(5).ToArray();
        return data[4] switch
        {
            1 or 2 when OperatingSystem.IsWindows() => WinUnprotect(body, data[4] == 2),
            3 when !OperatingSystem.IsWindows() => Aead.Decrypt(HostKey(), body, "install-key"),
            _ => throw new CryptographicException("key file from another platform"),
        };
    }

    [SupportedOSPlatform("windows")]
    static byte[] WinProtect(byte[] raw, bool machine) =>
        ProtectedData.Protect(raw, AppSalt, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    static byte[] WinUnprotect(byte[] body, bool machine) =>
        ProtectedData.Unprotect(body, AppSalt, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    /// <summary>Host-bound wrapping key (application-specific keyed hash of the host identity).</summary>
    static byte[] HostKey()
    {
        string? id = null;
        foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(p)) id = File.ReadAllText(p).Trim();
            }
            catch
            {
                // try the next one
            }
            if (!string.IsNullOrEmpty(id)) break;
        }
        if (string.IsNullOrEmpty(id)) throw new CryptographicException("host identity is not available");
        return HMACSHA256.HashData(AppSalt, Encoding.ASCII.GetBytes(id));
    }

    /// <summary>Purpose-specific key derived from the installation key.</summary>
    public static byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Raw, 32, null, Encoding.UTF8.GetBytes("FreeMon/" + purpose));
}

public sealed class InstallKeyMissingException : Exception
{
    public InstallKeyMissingException() : base("install key is missing") { }
}

/// <summary>AES-256-GCM: "FME1" | nonce(12) | tag(16) | ciphertext. Purpose string is bound as associated data.</summary>
public static class Aead
{
    static readonly byte[] Magic = "FME1"u8.ToArray();

    public static byte[] Encrypt(byte[] key, byte[] plain, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Magic.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
    }

    public static byte[] Decrypt(byte[] key, byte[] data, string purpose)
    {
        if (data.Length < 32 || !data.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("not an encrypted FreeMon file");
        var nonce = data.AsSpan(4, 12);
        var tag = data.AsSpan(16, 16);
        var cipher = data.AsSpan(32);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }

    public static bool LooksEncrypted(byte[] data) => data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(Magic);
}

/// <summary>Canonical text of log rows for hourly seals (same code for sealing and verification).</summary>
public static class SealFormat
{
    public const long HourMs = 3_600_000;

    static string D(double? v) => v.HasValue ? v.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "~";

    public static string Cycle(long ts, string dev, int sent, int recv, double loss, double? rmin, double? ravg, double? rmax, double? jit, int status, string? error) =>
        $"C|{ts}|{dev}|{sent}|{recv}|{D(loss)}|{D(rmin)}|{D(ravg)}|{D(rmax)}|{D(jit)}|{status}|{error ?? "~"}\n";

    public static string Event(long ts, string dev, string? name, string? addr, int from, int to, string? text) =>
        $"E|{ts}|{dev}|{name ?? "~"}|{addr ?? "~"}|{from}|{to}|{text ?? "~"}\n";

    public static string Check(long ts, string dev, string chk, int ok, double? ms, int status, string? detail) =>
        $"K|{ts}|{dev}|{chk}|{ok}|{D(ms)}|{status}|{detail ?? "~"}\n";

    public static byte[] Mac(byte[] key, string dev, long hour, byte[] digest) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{dev}|{hour}|").Concat(digest).ToArray());
}

/// <summary>Report verification codes: FMV1.&lt;device&gt;.&lt;from&gt;.&lt;to&gt;.&lt;mac&gt;</summary>
public static class ReportCode
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    static string Payload(HistoryResult r) =>
        string.Join("|",
            "FMV1",
            r.Device.Id,
            HistoryCalc.Ms(r.FromUtc),
            HistoryCalc.Ms(r.ToUtc),
            Math.Round(r.UpSec),
            Math.Round(r.DegradedSec),
            Math.Round(r.DownSec),
            Math.Round(r.NoDataSec),
            r.Incidents.Count,
            r.CycleCount);

    static string Encode(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (var x in b.Take(15)) sb.Append(Alphabet[x % Alphabet.Length]);
        return sb.ToString();
    }

    public static string Create(HistoryResult r)
    {
        var mac = HMACSHA256.HashData(InstallKey.Derive("report"), Encoding.UTF8.GetBytes(Payload(r)));
        return $"FMV1.{r.Device.Id}.{HistoryCalc.Ms(r.FromUtc)}.{HistoryCalc.Ms(r.ToUtc)}.{Encode(mac)}";
    }

    public static bool TryParse(string code, out string deviceId, out DateTime fromUtc, out DateTime toUtc, out string mac)
    {
        deviceId = "";
        mac = "";
        fromUtc = toUtc = default;
        var p = (code ?? "").Trim().Split('.');
        if (p.Length != 5 || p[0] != "FMV1" || !long.TryParse(p[2], out var f) || !long.TryParse(p[3], out var t)) return false;
        deviceId = p[1];
        fromUtc = HistoryCalc.Utc(f);
        toUtc = HistoryCalc.Utc(t);
        mac = p[4];
        return true;
    }

    public static bool Matches(HistoryResult r, string mac)
    {
        var expect = Encode(HMACSHA256.HashData(InstallKey.Derive("report"), Encoding.UTF8.GetBytes(Payload(r))));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(mac ?? ""));
    }
}
