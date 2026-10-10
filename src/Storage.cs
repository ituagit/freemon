using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FreeMon;

public enum StorageKind { User, AllUsers, Portable, Custom }

public sealed class LocationFile
{
    public string DataDir { get; set; } = "";
}

/// <summary>
/// Data location. Search order: portable.flag -> ProgramData (Windows) -> location.json pointer -> user dir -> wizard.
/// </summary>
public static class Paths
{
    public static string DataDir { get; private set; } = "";
    public static StorageKind Kind { get; private set; }
    public static bool Portable => Kind == StorageKind.Portable;

    public static string ExeDir => AppContext.BaseDirectory;
    public static string PortableFlag => Path.Combine(ExeDir, "portable.flag");
    public static string PortableDir => Path.Combine(ExeDir, "data");

    public static string UserDir => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        "FreeMon");

    public static string? AllUsersDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeMon")
        : null;

    public static string PointerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeMon", "location.json");

    /// <summary>Encrypted configuration (AES-GCM with a key derived from the installation key).</summary>
    public static string ConfigFile => Path.Combine(DataDir, "config.fmc");
    /// <summary>Plain configuration of versions before 1.8 (migrated automatically).</summary>
    public static string LegacyConfigFile => Path.Combine(DataDir, "config.json");
    public static string SigFile => Path.Combine(DataDir, "config.hmac");
    public static string SignedCopyFile => Path.Combine(DataDir, "config.signed.fmc");
    public static string LegacySignedCopyFile => Path.Combine(DataDir, "config.signed.json");
    public static string SecretsFile => Path.Combine(DataDir, "secrets.fmc");
    public static string LegacySecretsFile => Path.Combine(DataDir, "secrets.json");
    public static string BackupStateFile => Path.Combine(DataDir, "backup.last");

    public static bool HasConfig(string dir) =>
        File.Exists(Path.Combine(dir, "config.fmc")) || File.Exists(Path.Combine(dir, "config.json"));
    public static string ProtectedFlag => Path.Combine(DataDir, "protected.flag");
    public static string DbFile => Path.Combine(DataDir, "log.db");
    public static string EventsFile => Path.Combine(DataDir, "events.log");
    public static string LockFile => Path.Combine(DataDir, "freemon.lock");

    public static string CrashFile => DataDir.Length > 0
        ? Path.Combine(DataDir, "crash.log")
        : Path.Combine(Path.GetTempPath(), "freemon-crash.log");

    public static string KindText(StorageKind k) => k switch
    {
        StorageKind.AllUsers => L.T("for all users"),
        StorageKind.Portable => L.T("portable, next to the program"),
        StorageKind.Custom => L.T("custom folder"),
        _ => L.T("only for the current user"),
    };

    public static bool TryDetect()
    {
        if (File.Exists(PortableFlag)) { Set(StorageKind.Portable, PortableDir); return true; }
        if (AllUsersDir is { } au && HasConfig(au)) { Set(StorageKind.AllUsers, au); return true; }
        var custom = ReadPointer();
        if (custom is not null) { Set(StorageKind.Custom, custom); return true; }
        if (HasConfig(UserDir)) { Set(StorageKind.User, UserDir); return true; }
        return false;
    }

    static void Set(StorageKind kind, string dir)
    {
        Directory.CreateDirectory(dir);
        Kind = kind;
        DataDir = dir;
    }

    static string? ReadPointer()
    {
        try
        {
            if (!File.Exists(PointerFile)) return null;
            var p = JsonSerializer.Deserialize<LocationFile>(File.ReadAllText(PointerFile));
            return string.IsNullOrWhiteSpace(p?.DataDir) ? null : p.DataDir;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Pointer read", ex);
            return null;
        }
    }

    /// <summary>Called by the first-run wizard. Throws with a readable message on failure.</summary>
    public static void Apply(StorageKind kind, string? customDir)
    {
        string dir = kind switch
        {
            StorageKind.User => UserDir,
            StorageKind.AllUsers => AllUsersDir ?? throw new InvalidOperationException(L.T("A shared folder for all users is available only on Windows")),
            StorageKind.Portable => PortableDir,
            _ => Path.GetFullPath(string.IsNullOrWhiteSpace(customDir) ? throw new InvalidOperationException(L.T("No folder specified")) : customDir.Trim()),
        };

        if (kind == StorageKind.AllUsers && OperatingSystem.IsWindows())
            SharedDir.Create(dir);
        else
            Directory.CreateDirectory(dir);

        var probe = Path.Combine(dir, ".freemon-write-test");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);

        if (kind == StorageKind.Portable)
            File.WriteAllText(PortableFlag, "FreeMon portable mode: data is stored in ./data\n");

        if (kind == StorageKind.Custom)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
            File.WriteAllText(PointerFile, JsonSerializer.Serialize(new LocationFile { DataDir = dir }));
        }

        Kind = kind;
        DataDir = dir;
    }
}

[SupportedOSPlatform("windows")]
static class SharedDir
{
    /// <summary>Creates ProgramData\FreeMon and grants BUILTIN\Users Modify on it (inherited by files).</summary>
    public static void Create(string path)
    {
        var di = Directory.CreateDirectory(path);
        try
        {
            var sec = di.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            sec.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ProgramData ACL", ex);
            throw new IOException(L.F("Could not grant the \"Users\" group permissions on {0}: {1}", path, ex.Message), ex);
        }
    }
}

public static class ConfigStore
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(object? o) => JsonSerializer.Serialize(o, Opts);

    public static AppConfig Parse(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json.TrimStart('\uFEFF'), Opts) ?? new AppConfig();
        Normalize(cfg);
        return cfg;
    }

    public static AppConfig Parse(byte[] bytes) => Parse(Encoding.UTF8.GetString(bytes));

    public static AppConfig? TryParse(byte[]? bytes)
    {
        if (bytes is null) return null;
        try { return Parse(bytes); }
        catch (Exception ex) { CrashLog.Write("Config parse", ex); return null; }
    }

    /// <summary>Warning shown after start (config restored from a copy, migrated, damaged…).</summary>
    public static string? LoadWarning { get; private set; }

    static byte[] ConfigKey => InstallKey.Derive("config");

    /// <summary>Plain bytes of an encrypted (or legacy plain) config file; null if missing.</summary>
    public static byte[]? ReadPlain(string path)
    {
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        return Aead.LooksEncrypted(data) ? Aead.Decrypt(ConfigKey, data, "config") : data;
    }

    public static byte[]? TryReadPlain(string path)
    {
        try { return ReadPlain(path); }
        catch (Exception ex) { CrashLog.Write("Config read " + Path.GetFileName(path), ex); return null; }
    }

    public static void WriteEncrypted(string path, byte[] plain)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Aead.Encrypt(ConfigKey, plain, "config"));
        File.Move(tmp, path, true);
    }

    /// <summary>Configs of versions before 1.8 were plain JSON: encrypt the exact bytes (keeps the admin signature valid).</summary>
    static void MigrateLegacy()
    {
        foreach (var (plain, enc) in new[] { (Paths.LegacyConfigFile, Paths.ConfigFile), (Paths.LegacySignedCopyFile, Paths.SignedCopyFile) })
        {
            if (!File.Exists(plain) || File.Exists(enc)) continue;
            var bytes = File.ReadAllBytes(plain);
            WriteEncrypted(enc, bytes);
            if (!ReadPlain(enc)!.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("encryption self-check failed");
            File.Move(plain, plain + ".pre-encryption", true);
            LoadWarning = L.T("The configuration has been encrypted. The old unencrypted files were renamed to *.pre-encryption — check that everything works and delete them manually.");
        }
        var bak = Paths.LegacyConfigFile + ".bak";
        if (File.Exists(bak)) File.Move(bak, bak + ".pre-encryption", true);
    }

    public static AppConfig Load()
    {
        MigrateLegacy();
        if (!File.Exists(Paths.ConfigFile)) return Parse("{}");
        foreach (var (path, note) in new[]
                 {
                     (Paths.ConfigFile, (string?)null),
                     (Paths.ConfigFile + ".bak", L.T("The configuration file is damaged or changed outside the program — the previous copy was loaded.")),
                     (Paths.SignedCopyFile, L.T("The configuration file is damaged or changed outside the program — the last copy certified by the administrator was loaded.")),
                 })
        {
            try
            {
                var plain = ReadPlain(path);
                if (plain is null) continue;
                var cfg = Parse(plain);
                if (note is not null) LoadWarning = note;
                return cfg;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Config load " + Path.GetFileName(path), ex);
            }
        }
        var broken = Paths.ConfigFile + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try { File.Move(Paths.ConfigFile, broken); } catch { /* keep going */ }
        LoadWarning = L.T("The configuration file is damaged or changed outside the program and there are no valid copies. Started with an empty configuration; restore the data from a backup (Settings → Backups).");
        return Parse("{}");
    }

    public static void Normalize(AppConfig cfg)
    {
        cfg.Global ??= PingProfile.Defaults();
        var def = PingProfile.Defaults();
        foreach (var f in ProfileFields.All)
            if (f.Get(cfg.Global) is null) f.Set(cfg.Global, f.Get(def));

        cfg.Types ??= new List<DeviceType>();
        cfg.Devices ??= new List<Device>();
        foreach (var t in cfg.Types)
        {
            t.Name ??= "";
            t.Icon ??= "";
            t.Profile ??= new PingProfile();
        }
        cfg.Notify ??= new NotifySettings();
        cfg.Report ??= new ReportSettings();
        cfg.Backup ??= new BackupSettings();
        cfg.Backup.Folder ??= "";
        cfg.Backup.PublicKey ??= "";
        cfg.Backup.EncryptedPrivateKey ??= "";
        cfg.Backup.IntervalHours = cfg.Backup.IntervalHours is 24 or 168 ? cfg.Backup.IntervalHours : 24;
        cfg.Backup.Keep = Math.Clamp(cfg.Backup.Keep, 1, 365);
        cfg.Report.Organization ??= "";
        cfg.Report.Language ??= "";
        cfg.Report.PreparedBy ??= "";
        cfg.Notify.PopupSeconds = Math.Clamp(cfg.Notify.PopupSeconds, 0, 3600);
        // order: built-in types, then user types, "Інше" always last
        var ordered = new List<DeviceType>();
        foreach (var bt in AppConfig.BuiltInTypes.Where(b => b != AppConfig.OtherType))
            ordered.Add(cfg.Types.FirstOrDefault(t => string.Equals(t.Name, bt, StringComparison.OrdinalIgnoreCase)) ?? new DeviceType { Name = bt });
        ordered.AddRange(cfg.Types.Where(t => !AppConfig.IsBuiltInType(t.Name)));
        ordered.Add(cfg.Types.FirstOrDefault(t => string.Equals(t.Name, AppConfig.OtherType, StringComparison.OrdinalIgnoreCase)) ?? new DeviceType { Name = AppConfig.OtherType });
        cfg.Types = ordered;

        var ids = new HashSet<string>();
        foreach (var d in cfg.Devices)
        {
            d.Profile ??= new PingProfile();
            d.Name ??= "";
            d.Address ??= "";
            d.Type ??= "";
            d.Group ??= "";
            d.Description ??= "";
            d.Checks ??= new List<ExtraCheck>();
            d.Links ??= new List<DeviceLink>();
            foreach (var l in d.Links)
            {
                if (string.IsNullOrWhiteSpace(l.Id)) l.Id = Guid.NewGuid().ToString("N");
                if (!Enum.IsDefined(l.Kind)) l.Kind = LinkKind.Https;
                l.Port = Math.Clamp(l.Port, 1, 65535);
                l.Name ??= "";
                l.Path ??= "";
                l.User ??= "";
            }
            var checkIds = new HashSet<string>();
            foreach (var c in d.Checks)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || !checkIds.Add(c.Id)) { c.Id = Guid.NewGuid().ToString("N"); checkIds.Add(c.Id); }
                NormalizeCheck(c);
            }
            if (d.Primary is not null)
            {
                d.Primary.Enabled = true;
                NormalizeCheck(d.Primary);
            }
            if (string.IsNullOrWhiteSpace(d.Id) || !ids.Add(d.Id))
            {
                d.Id = Guid.NewGuid().ToString("N");
                ids.Add(d.Id);
            }
        }
        foreach (var d in cfg.Devices)
            if (d.ParentId is not null && (d.ParentId == d.Id || !ids.Contains(d.ParentId)))
                d.ParentId = null;

        if (cfg.RetentionDays < 1) cfg.RetentionDays = 30;
        cfg.AutoLockMinutes = Math.Clamp(cfg.AutoLockMinutes, 0, 1440);
        if (!Enum.IsDefined(cfg.CloseAction)) cfg.CloseAction = CloseAction.Auto;
        if (!Enum.IsDefined(cfg.Theme)) cfg.Theme = UiTheme.Auto;
        if (!Enum.IsDefined(cfg.MapOrigin)) cfg.MapOrigin = MapOrigin.TopLeft;
        if (!Enum.IsDefined(cfg.SshMode)) cfg.SshMode = SshMode.Terminal;
        cfg.SshCommand ??= "";
        cfg.Channels ??= new List<AlertChannel>();
        var chIds = new HashSet<string>();
        foreach (var ch in cfg.Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Id) || !chIds.Add(ch.Id)) { ch.Id = Guid.NewGuid().ToString("N"); chIds.Add(ch.Id); }
            ch.Name ??= "";
            ch.SmtpHost ??= "";
            ch.SmtpUser ??= "";
            ch.MailFrom ??= "";
            ch.MailTo ??= "";
            ch.Preset ??= "";
            ch.Method = string.Equals(ch.Method, "GET", StringComparison.OrdinalIgnoreCase) ? "GET" : "POST";
            ch.ContentType ??= "application/json";
            ch.BodyTemplate ??= "";
            ch.SmtpPort = Math.Clamp(ch.SmtpPort, 1, 65535);
            if (!Enum.IsDefined(ch.Kind)) ch.Kind = ChannelKind.Webhook;
            if (!Enum.IsDefined(ch.SmtpSecurity)) ch.SmtpSecurity = SmtpSecurity.StartTls;
        }
    }

    /// <summary>Atomic save. When protected and key is known: writes HMAC, signed copy and protected.flag.</summary>
    static void NormalizeCheck(ExtraCheck c)
    {
        c.Name ??= "";
        c.Url ??= "";
        c.ExpectCodes ??= "200-399";
        c.ExpectText ??= "";
        c.DnsName ??= "";
        c.DnsType = c.DnsType == "AAAA" ? "AAAA" : "A";
        c.DnsExpect ??= "";
        if (!Enum.IsDefined(c.Kind)) c.Kind = CheckKind.Tcp;
        c.IntervalSec = Math.Clamp(c.IntervalSec, 5, 86400);
        c.TimeoutMs = Math.Clamp(c.TimeoutMs, 100, 60000);
        c.SlowMs = Math.Clamp(c.SlowMs, 0, 600000);
        c.ConfirmCycles = Math.Clamp(c.ConfirmCycles, 1, 50);
        c.Port = Math.Clamp(c.Port, 1, 65535);
        c.Headers ??= "";
        c.JsonPath ??= "";
        c.OkValue ??= "";
        c.OkValue2 ??= "";
        c.WarnValue ??= "";
        c.WarnValue2 ??= "";
        if (!Enum.IsDefined(c.OkOp)) c.OkOp = JsonOp.Exists;
        if (!Enum.IsDefined(c.WarnOp)) c.WarnOp = JsonOp.None;
    }

    public static void Save(AppConfig cfg, byte[]? key)
    {
        // never downgrade the schema marker written by a newer version
        cfg.Version = Math.Max(cfg.Version, AppConfig.CurrentVersion);
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(cfg, Opts));
        if (File.Exists(Paths.ConfigFile))
            File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".bak", true);
        WriteEncrypted(Paths.ConfigFile, bytes);

        if (cfg.Security is not null && key is not null)
        {
            File.WriteAllText(Paths.SigFile, Convert.ToBase64String(Integrity.Sign(key, bytes)));
            WriteEncrypted(Paths.SignedCopyFile, bytes);
            if (!File.Exists(Paths.ProtectedFlag))
                File.WriteAllText(Paths.ProtectedFlag, "FreeMon: config is password-protected\n");
        }
        else if (cfg.Security is null && File.Exists(Paths.ProtectedFlag))
        {
            File.Delete(Paths.ProtectedFlag);
        }
    }

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Opts), Opts)!;

    public static AppConfig Clone(AppConfig cfg) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, Opts), Opts)!;
}

/// <summary>Single-writer log: every ping cycle -> SQLite, status changes -> SQLite + events.log.</summary>
public sealed class LogStore
{
    abstract record Item;
    sealed record CycleItem(string Dev, CycleStats S) : Item;
    sealed record CheckItem(string Dev, string Chk, CheckResult R) : Item;
    sealed record CountersignItem(byte[] AdminSealKey, bool All) : Item;
    sealed record EventItem(DateTime Utc, string Dev, string Name, string Addr, DevStatus From, DevStatus To, string Text) : Item;

    const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        CREATE TABLE IF NOT EXISTS cycles(
            ts INTEGER NOT NULL, dev TEXT NOT NULL, sent INTEGER, recv INTEGER, loss REAL,
            rmin REAL, ravg REAL, rmax REAL, jitter REAL, status INTEGER, error TEXT);
        CREATE INDEX IF NOT EXISTS ix_cycles_dev_ts ON cycles(dev, ts);
        CREATE TABLE IF NOT EXISTS events(
            ts INTEGER NOT NULL, dev TEXT NOT NULL, name TEXT, addr TEXT,
            st_from INTEGER, st_to INTEGER, text TEXT);
        CREATE INDEX IF NOT EXISTS ix_events_dev_ts ON events(dev, ts);
        CREATE TABLE IF NOT EXISTS checks(
            ts INTEGER NOT NULL, dev TEXT NOT NULL, chk TEXT NOT NULL, ok INTEGER, ms REAL, status INTEGER, detail TEXT);
        CREATE INDEX IF NOT EXISTS ix_checks_dev_ts ON checks(dev, chk, ts);
        CREATE TABLE IF NOT EXISTS seals(
            dev TEXT NOT NULL, hour INTEGER NOT NULL, digest BLOB NOT NULL, mac BLOB NOT NULL, admin_mac BLOB,
            PRIMARY KEY(dev, hour));
        """;

    readonly Channel<Item> _ch = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    readonly string _db;
    readonly string _events;
    readonly Func<int> _retentionDays;
    readonly byte[] _sealKey = InstallKey.Derive("seal");
    readonly Task _task;

    public LogStore(string db, string events, Func<int> retentionDays)
    {
        _db = db;
        _events = events;
        _retentionDays = retentionDays;
        _task = Task.Run(() => WriterAsync());
    }

    public void AddCycle(string devId, CycleStats s) => _ch.Writer.TryWrite(new CycleItem(devId, s));

    public void AddCheck(string devId, string checkId, CheckResult r) => _ch.Writer.TryWrite(new CheckItem(devId, checkId, r));

    /// <summary>Administrator logged in: countersign all hourly seals made since the last countersignature.</summary>
    public void Countersign(byte[] adminSealKey, bool all = false) => _ch.Writer.TryWrite(new CountersignItem(adminSealKey, all));

    public void AddEvent(string devId, string name, string addr, DevStatus from, DevStatus to, string text) =>
        _ch.Writer.TryWrite(new EventItem(DateTime.UtcNow, devId, name, addr, from, to, text));

    public async Task CloseAsync()
    {
        _ch.Writer.TryComplete();
        try { await _task.ConfigureAwait(false); }
        catch (Exception ex) { CrashLog.Write("LogStore close", ex); }
    }

    static long Ms(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    static object Db(double? v) => v.HasValue ? v.Value : DBNull.Value;

    async Task WriterAsync()
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = _db }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();
            using (var init = con.CreateCommand())
            {
                init.CommandText = Schema;
                init.ExecuteNonQuery();
            }
            MigrateDb(con);

            using var ev = new StreamWriter(
                new FileStream(_events, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };

            var nextPurge = DateTime.UtcNow.AddMinutes(1);
            var nextSeal = DateTime.UtcNow.AddMinutes(1);
            byte[]? pendingCountersign = null;
            bool countersignAll = false;
            while (await _ch.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    using var tx = con.BeginTransaction();
                    using var c1 = con.CreateCommand();
                    c1.Transaction = tx;
                    c1.CommandText = "INSERT INTO cycles(ts,dev,sent,recv,loss,rmin,ravg,rmax,jitter,status,error) VALUES($ts,$dev,$sent,$recv,$loss,$rmin,$ravg,$rmax,$jit,$st,$err)";
                    using var c3 = con.CreateCommand();
                    c3.Transaction = tx;
                    c3.CommandText = "INSERT INTO checks(ts,dev,chk,ok,ms,status,detail) VALUES($ts,$dev,$chk,$ok,$ms,$st,$det)";
                    using var c2 = con.CreateCommand();
                    c2.Transaction = tx;
                    c2.CommandText = "INSERT INTO events(ts,dev,name,addr,st_from,st_to,text) VALUES($ts,$dev,$name,$addr,$from,$to,$text)";

                    while (_ch.Reader.TryRead(out var it))
                    {
                        switch (it)
                        {
                            case CountersignItem csi:
                                pendingCountersign = csi.AdminSealKey;
                                countersignAll |= csi.All;
                                break;

                            case CycleItem ci:
                                c1.Parameters.Clear();
                                c1.Parameters.AddWithValue("$ts", Ms(ci.S.TimeUtc));
                                c1.Parameters.AddWithValue("$dev", ci.Dev);
                                c1.Parameters.AddWithValue("$sent", ci.S.Sent);
                                c1.Parameters.AddWithValue("$recv", ci.S.Recv);
                                c1.Parameters.AddWithValue("$loss", ci.S.LossPct);
                                c1.Parameters.AddWithValue("$rmin", Db(ci.S.Min));
                                c1.Parameters.AddWithValue("$ravg", Db(ci.S.Avg));
                                c1.Parameters.AddWithValue("$rmax", Db(ci.S.Max));
                                c1.Parameters.AddWithValue("$jit", Db(ci.S.Jitter));
                                c1.Parameters.AddWithValue("$st", (int)ci.S.Raw);
                                c1.Parameters.AddWithValue("$err", (object?)ci.S.Error ?? DBNull.Value);
                                c1.ExecuteNonQuery();
                                break;

                            case CheckItem ck:
                                c3.Parameters.Clear();
                                c3.Parameters.AddWithValue("$ts", Ms(ck.R.TimeUtc));
                                c3.Parameters.AddWithValue("$dev", ck.Dev);
                                c3.Parameters.AddWithValue("$chk", ck.Chk);
                                c3.Parameters.AddWithValue("$ok", ck.R.Ok ? 1 : 0);
                                c3.Parameters.AddWithValue("$ms", Db(ck.R.Ms));
                                c3.Parameters.AddWithValue("$st", (int)ck.R.Raw);
                                c3.Parameters.AddWithValue("$det", ck.R.Detail);
                                c3.ExecuteNonQuery();
                                break;

                            case EventItem ei:
                                c2.Parameters.Clear();
                                c2.Parameters.AddWithValue("$ts", Ms(ei.Utc));
                                c2.Parameters.AddWithValue("$dev", ei.Dev);
                                c2.Parameters.AddWithValue("$name", ei.Name);
                                c2.Parameters.AddWithValue("$addr", ei.Addr);
                                c2.Parameters.AddWithValue("$from", (int)ei.From);
                                c2.Parameters.AddWithValue("$to", (int)ei.To);
                                c2.Parameters.AddWithValue("$text", ei.Text);
                                c2.ExecuteNonQuery();
                                ev.WriteLine($"{ei.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\t{ei.Name}\t{ei.Addr}\t{ei.Text}");
                                break;
                        }
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    CrashLog.Write("LogStore write", ex);
                }

                if (DateTime.UtcNow >= nextSeal)
                {
                    try { Sealer.SealDue(con, _sealKey); }
                    catch (Exception ex) { CrashLog.Write("LogStore seal", ex); }
                    nextSeal = DateTime.UtcNow.AddMinutes(5);
                }
                if (pendingCountersign is not null)
                {
                    try
                    {
                        Sealer.SealDue(con, _sealKey);
                        Sealer.Countersign(con, pendingCountersign, countersignAll);
                    }
                    catch (Exception ex) { CrashLog.Write("LogStore countersign", ex); }
                    pendingCountersign = null;
                    countersignAll = false;
                }
                if (DateTime.UtcNow >= nextPurge)
                {
                    try { Purge(con); }
                    catch (Exception ex) { CrashLog.Write("LogStore purge", ex); }
                    nextPurge = DateTime.UtcNow.AddHours(1);
                }
            }
            SqliteConnection.ClearPool(con);
        }
        catch (Exception ex)
        {
            CrashLog.Write("LogStore fatal", ex);
            await foreach (var _ in _ch.Reader.ReadAllAsync().ConfigureAwait(false)) { }
        }
    }

    /// <summary>Log schema version = PRAGMA user_version. Only additive changes are allowed between versions.</summary>
    public const int DbVersion = 3;

    static void MigrateDb(SqliteConnection con)
    {
        using var get = con.CreateCommand();
        get.CommandText = "PRAGMA user_version;";
        var v = Convert.ToInt32(get.ExecuteScalar());
        if (v < DbVersion)
        {
            using var set = con.CreateCommand();
            set.CommandText = $"PRAGMA user_version = {DbVersion};";
            set.ExecuteNonQuery();
        }
    }

    void Purge(SqliteConnection con)
    {
        var days = Math.Max(1, _retentionDays());
        var cut = Ms(DateTime.UtcNow.AddDays(-days));
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM cycles WHERE ts < $cut; DELETE FROM events WHERE ts < $cut; DELETE FROM checks WHERE ts < $cut; DELETE FROM seals WHERE hour + 3600000 <= $cut;";
        cmd.Parameters.AddWithValue("$cut", cut);
        cmd.ExecuteNonQuery();
    }
}
