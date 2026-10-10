using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FreeMon;

public sealed class BackupHeader
{
    public int V { get; set; } = 1;
    public string App { get; set; } = "";
    public string Created { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>"password" (manual backup) or "public" (automatic backup).</summary>
    public string Kind { get; set; } = "password";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
    public string EphemeralPublicKey { get; set; } = "";
    public string EncryptedPrivateKey { get; set; } = "";
    public bool HasLog { get; set; }
}

/// <summary>
/// Backup file: "FMB1\n" + header JSON + "\n" + AES-GCM(zip). The zip holds the installation key and the
/// plain configuration, so a backup restores everything on another computer — the backup password protects it.
/// </summary>
public static class BackupService
{
    static readonly byte[] Magic = "FMB1\n"u8.ToArray();
    const int Iter = 600_000;
    static readonly object Gate = new();

    public const string AutoPrefix = "freemon-auto-";

    static byte[] BuildZip(bool includeLog)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, byte[]? data)
            {
                if (data is null) return;
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data);
            }
            Put("install.key", InstallKey.Raw);
            Put("config.json", ConfigStore.TryReadPlain(Paths.ConfigFile));
            Put("config.signed.json", ConfigStore.TryReadPlain(Paths.SignedCopyFile));
            if (File.Exists(Paths.SigFile)) Put("config.hmac", File.ReadAllBytes(Paths.SigFile));
            if (File.Exists(Paths.ProtectedFlag)) Put("protected.flag", File.ReadAllBytes(Paths.ProtectedFlag));
            Put("secrets.json", SecretStore.ReadPlain());
            if (File.Exists(UiStateStore.FilePath)) Put("ui.json", File.ReadAllBytes(UiStateStore.FilePath));
            if (includeLog && File.Exists(Paths.DbFile)) Put("log.db", SnapshotDb());
        }
        return ms.ToArray();
    }

    /// <summary>Consistent copy of the SQLite log (online backup API), even while the app writes to it.</summary>
    static byte[] SnapshotDb()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "freemon-backup-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.DbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
            {
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
            }
            return File.ReadAllBytes(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temp file */ }
        }
    }

    static void WriteFile(string path, BackupHeader h, byte[] key, byte[] zip)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(h);
        var body = Aead.Encrypt(key, zip, "backup");
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(Magic);
            fs.Write(header);
            fs.WriteByte((byte)'\n');
            fs.Write(body);
        }
        File.Move(tmp, path, true);
    }

    static BackupHeader NewHeader(string kind, bool hasLog) => new()
    {
        App = "FreeMon " + AppInfo.Version,
        Created = DateTimeOffset.Now.ToString("o"),
        Host = Environment.MachineName,
        Kind = kind,
        HasLog = hasLog,
    };

    /// <summary>Manual backup protected by a password.</summary>
    public static void CreateWithPassword(string path, string password, bool includeLog)
    {
        lock (Gate)
        {
            var zip = BuildZip(includeLog);
            var h = NewHeader("password", includeLog);
            var salt = RandomNumberGenerator.GetBytes(16);
            h.Salt = Convert.ToBase64String(salt);
            h.Iterations = Iter;
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iter, HashAlgorithmName.SHA256, 32);
            WriteFile(path, h, key, zip);
        }
    }

    /// <summary>Key pair for automatic backups: the private key is stored only encrypted with the backup password.</summary>
    public static (string PublicKey, string EncryptedPrivateKey) CreateKeyPair(string password)
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var enc = ec.ExportEncryptedPkcs8PrivateKey(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iter));
        return (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(enc));
    }

    /// <summary>Automatic backup: encrypted to the stored public key, no password needed.</summary>
    public static void CreateWithPublicKey(string path, BackupSettings b)
    {
        lock (Gate)
        {
            var zip = BuildZip(b.IncludeLog);
            var h = NewHeader("public", b.IncludeLog);
            using var recipient = ECDiffieHellman.Create();
            recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b.PublicKey), out _);
            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var salt = RandomNumberGenerator.GetBytes(16);
            var secret = eph.DeriveRawSecretAgreement(recipient.PublicKey);
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "FreeMon/backup"u8.ToArray());
            h.Salt = Convert.ToBase64String(salt);
            h.EphemeralPublicKey = Convert.ToBase64String(eph.ExportSubjectPublicKeyInfo());
            h.EncryptedPrivateKey = b.EncryptedPrivateKey;
            WriteFile(path, h, key, zip);
        }
    }

    public sealed record Opened(BackupHeader Header, Dictionary<string, byte[]> Files);

    /// <summary>Opens and decrypts a backup; throws CryptographicException on a wrong password.</summary>
    public static Opened Open(string path, string password)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 10 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException(L.T("this is not a FreeMon backup file"));
        int nl = Array.IndexOf(data, (byte)'\n', Magic.Length);
        if (nl < 0) throw new InvalidDataException(L.T("damaged backup file"));
        var h = JsonSerializer.Deserialize<BackupHeader>(data.AsSpan(Magic.Length, nl - Magic.Length)) ?? throw new InvalidDataException(L.T("damaged header"));
        var body = data.AsSpan(nl + 1).ToArray();
        byte[] key;
        if (h.Kind == "public")
        {
            using var priv = ECDiffieHellman.Create();
            priv.ImportEncryptedPkcs8PrivateKey(password, Convert.FromBase64String(h.EncryptedPrivateKey), out _);
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(h.EphemeralPublicKey), out _);
            var secret = priv.DeriveRawSecretAgreement(peer.PublicKey);
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Convert.FromBase64String(h.Salt), "FreeMon/backup"u8.ToArray());
        }
        else
        {
            key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(h.Salt), h.Iterations, HashAlgorithmName.SHA256, 32);
        }
        var zip = Aead.Decrypt(key, body, "backup");
        var files = new Dictionary<string, byte[]>();
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var e in za.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[e.FullName] = ms.ToArray();
        }
        if (!files.ContainsKey("install.key") || !files.ContainsKey("config.json")) throw new InvalidDataException(L.T("the backup lacks required files"));
        return new Opened(h, files);
    }

    /// <summary>Writes an opened backup into the current data folder. The log is replaced only if includeLog.</summary>
    public static void Apply(Opened o, bool includeLog)
    {
        // The installation key goes with the log (seals and report codes depend on it).
        // Restoring only the settings keeps the current key, so the current log stays verifiable.
        if (includeLog || !InstallKey.Loaded) InstallKey.Replace(o.Files["install.key"]);
        ConfigStore.WriteEncrypted(Paths.ConfigFile, o.Files["config.json"]);
        if (o.Files.TryGetValue("config.signed.json", out var signed)) ConfigStore.WriteEncrypted(Paths.SignedCopyFile, signed);
        if (o.Files.TryGetValue("config.hmac", out var sig)) File.WriteAllBytes(Paths.SigFile, sig);
        if (o.Files.TryGetValue("protected.flag", out var pf)) File.WriteAllBytes(Paths.ProtectedFlag, pf);
        if (o.Files.TryGetValue("secrets.json", out var sec)) SecretStore.WritePlain(sec);
        if (o.Files.TryGetValue("ui.json", out var ui))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiStateStore.FilePath)!);
            File.WriteAllBytes(UiStateStore.FilePath, ui);
        }
        if (includeLog && o.Files.TryGetValue("log.db", out var db))
        {
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(Paths.DbFile + ext)) File.Move(Paths.DbFile + ext, Paths.DbFile + ext + ".before-restore", true);
            if (File.Exists(Paths.DbFile)) File.Move(Paths.DbFile, Paths.DbFile + ".before-restore", true);
            File.WriteAllBytes(Paths.DbFile, db);
        }
    }

    /// <summary>Called every 10 minutes: makes an automatic backup when it is due and keeps the newest N.</summary>
    public static void AutoTick(AppCore core)
    {
        try
        {
            var b = core.Config.Backup;
            if (!b.AutoEnabled || string.IsNullOrWhiteSpace(b.PublicKey) || string.IsNullOrWhiteSpace(b.Folder)) return;
            long last = 0;
            if (File.Exists(Paths.BackupStateFile)) long.TryParse(File.ReadAllText(Paths.BackupStateFile).Trim(), out last);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - last < b.IntervalHours * 3_600_000L) return;
            Directory.CreateDirectory(b.Folder);
            var file = Path.Combine(b.Folder, $"{AutoPrefix}{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.fmbackup");
            CreateWithPublicKey(file, b);
            File.WriteAllText(Paths.BackupStateFile, now.ToString());
            var old = new DirectoryInfo(b.Folder).GetFiles($"{AutoPrefix}{Environment.MachineName}-*.fmbackup")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(b.Keep);
            foreach (var f in old) f.Delete();
            core.Monitor.SystemEvent(L.T("Automatic backup saved: ") + file);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Auto backup", ex);
            core.Monitor.SystemEvent(L.T("Could not make an automatic backup: ") + ex.Message);
        }
    }

    public static DateTime? LastAutoBackup()
    {
        try
        {
            if (File.Exists(Paths.BackupStateFile) && long.TryParse(File.ReadAllText(Paths.BackupStateFile).Trim(), out var ms))
                return HistoryCalc.Utc(ms).ToLocalTime();
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
