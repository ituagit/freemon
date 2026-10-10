using System.Security.Cryptography;

namespace FreeMon;

public sealed class SecurityInfo
{
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; } = PasswordCrypto.DefaultIterations;

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// PBKDF2-SHA256, 64 bytes: first 32 = stored verifier, last 32 = HMAC key (never stored).
/// </summary>
public static class PasswordCrypto
{
    public const int DefaultIterations = 600_000;
    public const int MinLength = 6;

    public static (SecurityInfo Info, byte[] Key) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var d = Derive(password, salt, DefaultIterations);
        var info = new SecurityInfo
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(d[..32]),
            Iterations = DefaultIterations,
        };
        return (info, d[32..]);
    }

    /// <summary>Returns HMAC key when password is correct, otherwise null.</summary>
    public static byte[]? Verify(SecurityInfo s, string password)
    {
        try
        {
            var salt = Convert.FromBase64String(s.Salt);
            var hash = Convert.FromBase64String(s.Hash);
            if (salt.Length < 8 || hash.Length != 32 || s.Iterations < 10_000) return null;
            var d = Derive(password, salt, s.Iterations);
            return CryptographicOperations.FixedTimeEquals(d.AsSpan(0, 32), hash) ? d[32..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 64);
}

public sealed record IntegrityResult(bool Ok, bool SignedCopyValid, bool CanAccept, List<string> Changes);

public enum UnlockStatus { Wrong, Ok, Tampered }

public sealed record UnlockOutcome(UnlockStatus Status, IntegrityResult? Integrity);

public enum TamperChoice { Cancel, Revert, Accept }

/// <summary>Detects config changes made outside the program (checked when the administrator logs in).</summary>
public static class Integrity
{
    public static byte[] Sign(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    /// <summary>Plain bytes of the (encrypted) config files; null if missing or not decryptable.</summary>
    static byte[]? ReadBytes(string path) => ConfigStore.TryReadPlain(path);

    static byte[]? ReadSig()
    {
        try { return File.Exists(Paths.SigFile) ? Convert.FromBase64String(File.ReadAllText(Paths.SigFile).Trim()) : null; }
        catch { return null; }
    }

    static bool Valid(byte[] key, byte[]? data, byte[]? sig) =>
        data is not null && sig is not null && CryptographicOperations.FixedTimeEquals(Sign(key, data), sig);

    public static IntegrityResult Check(byte[] key)
    {
        var sig = ReadSig();
        var cur = ReadBytes(Paths.ConfigFile);
        if (Valid(key, cur, sig)) return new IntegrityResult(true, true, true, new List<string>());

        var signed = ReadBytes(Paths.SignedCopyFile);
        bool signedOk = Valid(key, signed, sig);
        var signedCfg = signedOk ? ConfigStore.TryParse(signed) : null;
        var changes = signedCfg is not null
            ? Diff(signedCfg, ConfigStore.TryParse(cur))
            : new List<string> { L.T("There is no signed copy or it was changed too — nothing to compare with.") };
        return new IntegrityResult(false, signedCfg is not null, true, changes);
    }

    /// <summary>Password does not match the current config, but matches the last signed copy (password was replaced outside).</summary>
    public static (byte[] Key, IntegrityResult Result)? TryRescue(string password)
    {
        var sig = ReadSig();
        var signed = ReadBytes(Paths.SignedCopyFile);
        if (sig is null || signed is null) return null;
        var sc = ConfigStore.TryParse(signed);
        if (sc?.Security is null) return null;
        var key = PasswordCrypto.Verify(sc.Security, password);
        if (key is null || !Valid(key, signed, sig)) return null;
        var changes = Diff(sc, ConfigStore.TryParse(ReadBytes(Paths.ConfigFile)));
        return (key, new IntegrityResult(false, true, false, changes));
    }

    public static AppConfig? LoadSigned() => ConfigStore.TryParse(ReadBytes(Paths.SignedCopyFile));

    public static AppConfig? LoadCurrent() => ConfigStore.TryParse(ReadBytes(Paths.ConfigFile));

    /// <summary>Human-readable list: a = last signed (trusted), b = current file.</summary>
    public static List<string> Diff(AppConfig a, AppConfig? b)
    {
        var res = new List<string>();
        if (b is null)
        {
            res.Add(L.T("The current configuration file is damaged or deleted."));
            return res;
        }
        string J(object? o) => ConfigStore.ToJson(o);

        if (J(a.Security) != J(b.Security)) res.Add(L.T("Administrator password changed (or removed / added)"));
        if (J(a.Global) != J(b.Global)) res.Add(L.T("Global ping parameters changed"));
        if (a.RetentionDays != b.RetentionDays) res.Add(L.F("Log retention: {0} → {1} days", a.RetentionDays, b.RetentionDays));
        if (a.SuppressChildrenWhenParentDown != b.SuppressChildrenWhenParentDown) res.Add(L.T("\"Unreachable via parent\" mode changed"));
        if (a.AutoLockMinutes != b.AutoLockMinutes) res.Add(L.F("Auto-lock: {0} → {1} min", a.AutoLockMinutes, b.AutoLockMinutes));
        if (J(a.Notify) != J(b.Notify)) res.Add(L.T("Notification settings changed"));
        if (J(a.Report) != J(b.Report)) res.Add(L.T("Report settings changed"));
        if (J(a.Backup) != J(b.Backup)) res.Add(L.T("Backup settings changed"));
        if (a.CloseAction != b.CloseAction) res.Add(L.T("Window \"Close\" button action changed"));
        if (a.Theme != b.Theme) res.Add(L.T("Interface theme changed"));
        if (a.ViewMode != b.ViewMode) res.Add(L.T("Access mode changed (view mode)"));
        if (a.ShowLastEventInStatusBar != b.ShowLastEventInStatusBar) res.Add(L.T("Display of events in the status bar changed"));
        if (a.AllowReportsInViewMode != b.AllowReportsInViewMode) res.Add(b.AllowReportsInViewMode ? L.T("History and reports allowed in view mode") : L.T("History and reports not allowed in view mode"));
        if (J(a.MapLoosePos) != J(b.MapLoosePos)) res.Add(L.T("Position of the \"Standalone devices\" card changed"));
        if (a.MapOrigin != b.MapOrigin) res.Add(L.T("Map layout origin changed"));
        if (a.AllowLinksInViewMode != b.AllowLinksInViewMode) res.Add(b.AllowLinksInViewMode ? L.T("Device connections allowed in view mode") : L.T("Device connections not allowed in view mode"));
        if (a.SshMode != b.SshMode || a.SshCommand != b.SshCommand) res.Add(L.T("SSH launch method changed"));
        if (J(a.Channels) != J(b.Channels)) res.Add(L.T("Notification channels changed (e-mail / webhooks)"));

        var ta = a.Types.GroupBy(t => t.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var tb = b.Types.GroupBy(t => t.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        foreach (var k in ta.Keys.Except(tb.Keys)) res.Add(L.F("Type removed: {0}", ta[k].Name));
        foreach (var k in tb.Keys.Except(ta.Keys)) res.Add(L.F("Type added: {0}", tb[k].Name));
        foreach (var k in ta.Keys.Intersect(tb.Keys))
        {
            if (J(ta[k].Profile) != J(tb[k].Profile)) res.Add(L.F("Type parameters changed: {0}", ta[k].Name));
            if (ta[k].Icon != tb[k].Icon) res.Add(L.F("Type icon changed: {0}", ta[k].Name));
        }

        var da = a.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var db = b.Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var k in da.Keys.Except(db.Keys)) res.Add(L.F("Device removed: {0} ({1})", da[k].Name, da[k].Address));
        foreach (var k in db.Keys.Except(da.Keys)) res.Add(L.F("Device added: {0} ({1})", db[k].Name, db[k].Address));
        foreach (var k in da.Keys.Intersect(db.Keys))
        {
            var x = da[k];
            var y = db[k];
            var f = new List<string>();
            if (x.Name != y.Name) f.Add(L.T("name"));
            if (x.Address != y.Address) f.Add(L.T("address"));
            if (x.Type != y.Type) f.Add(L.T("type"));
            if (x.ParentId != y.ParentId) f.Add(L.T("parent"));
            if (x.Group != y.Group) f.Add(L.T("group"));
            if (x.Description != y.Description) f.Add(L.T("description"));
            if (x.Enabled != y.Enabled) f.Add(y.Enabled ? L.T("monitoring enabled") : L.T("monitoring disabled"));
            if (J(x.Profile) != J(y.Profile)) f.Add(L.T("ping parameters"));
            if (J(x.MapPos) != J(y.MapPos) || J(x.MapCenterPos) != J(y.MapCenterPos) || J(x.MapCardPos) != J(y.MapCardPos)) f.Add(L.T("map position"));
            if (J(x.Checks) != J(y.Checks)) f.Add(L.T("additional checks"));
            if (J(x.Primary) != J(y.Primary)) f.Add(L.T("main check"));
            if (J(x.Links) != J(y.Links)) f.Add(L.T("connections (SSH / web)"));
            if (x.PlaySounds != y.PlaySounds) f.Add(y.PlaySounds ? L.T("sounds turned on") : L.T("sounds turned off"));
            if (x.SendAlerts != y.SendAlerts) f.Add(y.SendAlerts ? L.T("sending events to channels enabled") : L.T("sending events to channels disabled"));
            if (f.Count > 0) res.Add(L.F("Device changed {0} ({1}): {2}", x.Name, x.Address, string.Join(", ", f)));
        }

        if (res.Count == 0) res.Add(L.T("No meaningful changes found (only the file formatting or the signature changed)."));
        return res;
    }
}
