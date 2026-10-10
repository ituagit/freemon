using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FreeMon;

public sealed record IntegrityInfo(int SealedHours, List<DateTime> BadHours, DateTime? SealedUntilUtc, DateTime? AdminSealedUntilUtc, bool AdminVerified)
{
    public bool Ok => BadHours.Count == 0;
}

/// <summary>
/// Hourly seals of the log: for every device and closed hour a digest of all its rows (ping cycles, events, checks)
/// and a MAC with a key derived from the installation key. When the administrator logs in, new seals are
/// additionally countersigned with a key derived from the administrator password.
/// </summary>
public static class Sealer
{
    const long Hour = SealFormat.HourMs;
    const long Margin = 5 * 60_000;
    const int MaxHoursPerRun = 24 * 45;

    static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static object? Scalar(SqliteConnection con, string sql, params (string, object)[] ps)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static byte[] Digest(SqliteConnection con, string dev, long hour)
    {
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s) => ih.AppendData(Encoding.UTF8.GetBytes(s));
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,dev,sent,recv,loss,rmin,ravg,rmax,jitter,status,error FROM cycles WHERE dev=$d AND ts>=$h AND ts<$e ORDER BY ts, rowid";
            Bind(cmd, dev, hour);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                Add(SealFormat.Cycle(r.GetInt64(0), r.GetString(1), I(r, 2), I(r, 3), r.IsDBNull(4) ? 0 : r.GetDouble(4),
                    N(r, 5), N(r, 6), N(r, 7), N(r, 8), I(r, 9), S(r, 10)));
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,dev,name,addr,st_from,st_to,text FROM events WHERE (dev=$d OR (dev<>'' AND dev LIKE $d || '#%')) AND ts>=$h AND ts<$e ORDER BY ts, rowid";
            Bind(cmd, dev, hour);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                Add(SealFormat.Event(r.GetInt64(0), r.GetString(1), S(r, 2), S(r, 3), I(r, 4), I(r, 5), S(r, 6)));
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,dev,chk,ok,ms,status,detail FROM checks WHERE dev=$d AND ts>=$h AND ts<$e ORDER BY ts, rowid";
            Bind(cmd, dev, hour);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                Add(SealFormat.Check(r.GetInt64(0), r.GetString(1), r.GetString(2), I(r, 3), N(r, 4), I(r, 5), S(r, 6)));
        }
        return ih.GetHashAndReset();
    }

    static void Bind(SqliteCommand cmd, string dev, long hour)
    {
        cmd.Parameters.AddWithValue("$d", dev);
        cmd.Parameters.AddWithValue("$h", hour);
        cmd.Parameters.AddWithValue("$e", hour + Hour);
    }

    static int I(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
    static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static List<string> DevicesInHour(SqliteConnection con, long hour)
    {
        var res = new List<string>();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            "SELECT dev FROM cycles WHERE ts>=$h AND ts<$e UNION SELECT dev FROM checks WHERE ts>=$h AND ts<$e UNION SELECT CASE WHEN instr(dev,'#')>0 THEN substr(dev,1,instr(dev,'#')-1) ELSE dev END FROM events WHERE ts>=$h AND ts<$e";
        cmd.Parameters.AddWithValue("$h", hour);
        cmd.Parameters.AddWithValue("$e", hour + Hour);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(r.GetString(0));
        return res;
    }

    /// <summary>Seals every closed hour that is not sealed yet (a "*" row marks hours without data).</summary>
    public static void SealDue(SqliteConnection con, byte[] sealKey)
    {
        long upTo = (NowMs - Margin) / Hour * Hour - Hour;
        long start;
        if (Scalar(con, "SELECT MAX(hour) FROM seals") is long last) start = last + Hour;
        else
        {
            var first = new[] { "cycles", "events", "checks" }
                .Select(tb => Scalar(con, $"SELECT MIN(ts) FROM {tb}"))
                .OfType<long>().DefaultIfEmpty(long.MaxValue).Min();
            if (first == long.MaxValue) return;
            start = first / Hour * Hour;
        }
        int n = 0;
        using var tx = con.BeginTransaction();
        for (long h = start; h <= upTo && n < MaxHoursPerRun; h += Hour, n++)
        {
            var devs = DevicesInHour(con, h);
            if (devs.Count == 0) devs.Add("*");
            foreach (var dev in devs)
            {
                var digest = dev == "*" ? Array.Empty<byte>() : Digest(con, dev, h);
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO seals(dev,hour,digest,mac) VALUES($d,$h,$g,$m)";
                cmd.Parameters.AddWithValue("$d", dev);
                cmd.Parameters.AddWithValue("$h", h);
                cmd.Parameters.AddWithValue("$g", digest);
                cmd.Parameters.AddWithValue("$m", SealFormat.Mac(sealKey, dev, h, digest));
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public static byte[] AdminMac(byte[] adminSealKey, byte[] mac) => HMACSHA256.HashData(adminSealKey, mac);

    public static void Countersign(SqliteConnection con, byte[] adminSealKey, bool all = false)
    {
        var rows = new List<(string Dev, long Hour, byte[] Mac)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = all ? "SELECT dev,hour,mac FROM seals" : "SELECT dev,hour,mac FROM seals WHERE admin_mac IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1), (byte[])r["mac"]));
        }
        using var tx = con.BeginTransaction();
        foreach (var (dev, hour, mac) in rows)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE seals SET admin_mac=$a WHERE dev=$d AND hour=$h";
            cmd.Parameters.AddWithValue("$a", AdminMac(adminSealKey, mac));
            cmd.Parameters.AddWithValue("$d", dev);
            cmd.Parameters.AddWithValue("$h", hour);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Checks the seals of one device for a period (read-only connection).</summary>
    public static IntegrityInfo Verify(SqliteConnection con, string dev, long fromMs, long toMs, byte[] sealKey, byte[]? adminSealKey)
    {
        var bad = new List<DateTime>();
        int sealedCount = 0;
        long? lastSealed = Scalar(con, "SELECT MAX(hour) FROM seals") as long?;
        long? adminUntil = Scalar(con, "SELECT MAX(hour) FROM seals WHERE admin_mac IS NOT NULL") as long?;
        bool adminChecked = adminSealKey is not null;

        var seals = new Dictionary<long, (byte[] Digest, byte[] Mac, byte[]? Admin)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT hour,digest,mac,admin_mac FROM seals WHERE dev=$d AND hour>=$f AND hour<$t";
            cmd.Parameters.AddWithValue("$d", dev);
            cmd.Parameters.AddWithValue("$f", fromMs / Hour * Hour);
            cmd.Parameters.AddWithValue("$t", toMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                seals[r.GetInt64(0)] = ((byte[])r["digest"], (byte[])r["mac"], r.IsDBNull(3) ? null : (byte[])r["admin_mac"]);
        }

        for (long h = fromMs / Hour * Hour; h < toMs; h += Hour)
        {
            if (lastSealed is null || h > lastSealed) break;
            var digest = Digest(con, dev, h);
            bool hasData = digest.Length > 0 && !digest.AsSpan().SequenceEqual(SHA256.HashData(Array.Empty<byte>()));
            if (!seals.TryGetValue(h, out var s))
            {
                if (hasData) bad.Add(HistoryCalc.Utc(h)); // rows appeared in an hour that was sealed without them
                continue;
            }
            sealedCount++;
            bool ok = CryptographicOperations.FixedTimeEquals(s.Digest, digest) &&
                      CryptographicOperations.FixedTimeEquals(s.Mac, SealFormat.Mac(sealKey, dev, h, s.Digest));
            if (ok && adminSealKey is not null && s.Admin is not null)
                ok = CryptographicOperations.FixedTimeEquals(s.Admin, AdminMac(adminSealKey, s.Mac));
            if (!ok) bad.Add(HistoryCalc.Utc(h));
        }
        return new IntegrityInfo(sealedCount, bad,
            lastSealed is long ls ? HistoryCalc.Utc(ls + Hour) : null,
            adminUntil is long au ? HistoryCalc.Utc(au + Hour) : null,
            adminChecked);
    }
}
