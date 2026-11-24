using Microsoft.Data.Sqlite;

namespace TimeTracker;

public record Activity(long Id, string Name, int? Side, bool Billable);

public record Client(long Id, string Name, double Rate);

public record Segment(long EntryId, long ActivityId, string Name, string Client, double Rate, bool Billable,
                      DateTime StartUtc, DateTime EndUtc, bool Running, string Note)
{
    public double Seconds => (EndUtc - StartUtc).TotalSeconds;
    public double Amount => Billable ? Seconds / 3600 * Rate : 0;
    public DateOnly Day => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(StartUtc, TimeZoneInfo.Local));
}

public partial class Db
{
    const string Fmt = "yyyy-MM-ddTHH:mm:ss.fffZ";
    readonly string _cs;
    readonly object _lock = new();

    /// <summary>Raised after a local write (not after changes applied by sync), so a sync can be scheduled.</summary>
    public event Action? Changed;

    // Set while sync applies remote rows on this thread, so those writes don't trigger another sync.
    [ThreadStatic] static bool _applying;

    static string Now() => S(DateTime.UtcNow);
    static string NewUid() => Guid.NewGuid().ToString("N");

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = $"Data Source={path}";
        Exec("""
            CREATE TABLE IF NOT EXISTS activities(
              id INTEGER PRIMARY KEY, name TEXT NOT NULL,
              side INTEGER NULL, archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS entries(
              id INTEGER PRIMARY KEY, activity_id INTEGER NOT NULL REFERENCES activities(id),
              start TEXT NOT NULL, "end" TEXT NULL, note TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS clients(
              id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, rate REAL NOT NULL DEFAULT 0,
              archived INTEGER NOT NULL DEFAULT 0);
            """);
        Migrate();
    }

    // Older databases: entries had no client/rate of their own, tasks had no billable flag,
    // and tasks carried a default client (column dropped once migrated).
    void Migrate()
    {
        bool Has(string table, string col) => Query($"PRAGMA table_info({table})", r => r.GetString(1)).Contains(col);
        if (!Has("activities", "billable"))
            Exec("ALTER TABLE activities ADD COLUMN billable INTEGER NOT NULL DEFAULT 1");
        if (!Has("entries", "client"))
        {
            Exec("ALTER TABLE entries ADD COLUMN client TEXT NOT NULL DEFAULT ''");
            Exec("ALTER TABLE entries ADD COLUMN rate REAL NOT NULL DEFAULT 0");
            Exec("UPDATE entries SET client=(SELECT client FROM activities a WHERE a.id=entries.activity_id)");
        }
        if (Has("activities", "client"))
        {
            // Keep old default clients as known clients, then drop the column.
            Exec("INSERT OR IGNORE INTO clients(name) SELECT DISTINCT client FROM activities WHERE client<>''");
            Exec("ALTER TABLE activities DROP COLUMN client");
        }

        // Sync support: every row gets a global id (uid, since the integer ids collide across machines),
        // a last-modified time, and a dirty flag meaning "changed here, not yet pushed".
        // Existing rows start dirty so the first sync uploads them.
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        foreach (var t in new[] { "activities", "clients", "entries" })
        {
            if (!Has(t, "uid")) Exec($"ALTER TABLE {t} ADD COLUMN uid TEXT");
            if (!Has(t, "updated_at")) Exec($"ALTER TABLE {t} ADD COLUMN updated_at TEXT NOT NULL DEFAULT ''");
            if (!Has(t, "dirty")) Exec($"ALTER TABLE {t} ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1");
            Exec($"UPDATE {t} SET uid=lower(hex(randomblob(16))) WHERE uid IS NULL");
            Exec($"CREATE UNIQUE INDEX IF NOT EXISTS ix_{t}_uid ON {t}(uid)");
        }
        // Deleted entries are kept as tombstones so the deletion can sync to other machines.
        if (!Has("entries", "deleted")) Exec("ALTER TABLE entries ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0");
        Exec("UPDATE activities SET updated_at=$n WHERE updated_at=''", ("$n", Now()));
        Exec("UPDATE clients SET updated_at=$n WHERE updated_at=''", ("$n", Now()));
        Exec("UPDATE entries SET updated_at=COALESCE(\"end\",start) WHERE updated_at=''");
    }

    static string S(DateTime d) => d.ToUniversalTime().ToString(Fmt);
    static DateTime D(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    long Exec(string sql, params (string, object?)[] p)
    {
        long id;
        lock (_lock)
        {
            using var c = new SqliteConnection(_cs); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            id = (long)cmd.ExecuteScalar()!;
        }
        if (!_applying) Changed?.Invoke();
        return id;
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var c = new SqliteConnection(_cs); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    // ---- activities ----
    // Sorted by ZEI side number, unassigned last.
    public List<Activity> Activities() => Query(
        "SELECT id,name,side,billable FROM activities WHERE archived=0 ORDER BY side IS NULL, side, name",
        r => new Activity(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt64(3) != 0));

    public long AddActivity(string name, int? side, bool billable)
    {
        ClearSide(side);
        return Exec("INSERT INTO activities(uid,name,side,billable,updated_at,dirty) VALUES($u,$n,$s,$b,$t,1)",
            ("$u", NewUid()), ("$n", name), ("$s", side), ("$b", billable ? 1 : 0), ("$t", Now()));
    }

    public void UpdateActivity(long id, string name, int? side, bool billable)
    {
        ClearSide(side, id);
        Exec("UPDATE activities SET name=$n,side=$s,billable=$b,updated_at=$t,dirty=1 WHERE id=$id",
            ("$n", name), ("$s", side), ("$b", billable ? 1 : 0), ("$t", Now()), ("$id", id));
    }

    // Archived (not deleted) so past entries and reports keep their names.
    public void ArchiveActivity(long id) =>
        Exec("UPDATE activities SET archived=1, side=NULL, updated_at=$t, dirty=1 WHERE id=$id", ("$t", Now()), ("$id", id));

    void ClearSide(int? side, long exceptId = -1)
    {
        if (side != null) Exec("UPDATE activities SET side=NULL, updated_at=$t, dirty=1 WHERE side=$s AND id<>$id",
            ("$t", Now()), ("$s", side), ("$id", exceptId));
    }

    // ---- clients ----
    public List<Client> Clients() => Query("SELECT id,name,rate FROM clients WHERE archived=0 ORDER BY name",
        r => new Client(r.GetInt64(0), r.GetString(1), r.GetDouble(2)));

    public long AddClient(string name, double rate) =>
        Exec("INSERT INTO clients(uid,name,rate,updated_at,dirty) VALUES($u,$n,$r,$t,1) ON CONFLICT(name) DO UPDATE SET rate=$r, archived=0, updated_at=$t, dirty=1",
            ("$u", NewUid()), ("$n", name), ("$r", rate), ("$t", Now()));

    // Rate changes apply to new entries only; existing entries keep the rate they were logged at.
    public void UpdateClient(long id, string name, double rate) =>
        Exec("UPDATE clients SET name=$n,rate=$r,updated_at=$t,dirty=1 WHERE id=$id", ("$n", name), ("$r", rate), ("$t", Now()), ("$id", id));

    public void ArchiveClient(long id) => Exec("UPDATE clients SET archived=1, updated_at=$t, dirty=1 WHERE id=$id", ("$t", Now()), ("$id", id));

    public double RateFor(string client) =>
        Query("SELECT rate FROM clients WHERE name=$n", r => r.GetDouble(0), ("$n", client)).FirstOrDefault();

    // ---- tracking ----
    public long? RunningActivityId() => Query("SELECT activity_id FROM entries WHERE \"end\" IS NULL AND deleted=0 LIMIT 1",
        r => (long?)r.GetInt64(0)).FirstOrDefault();

    public (long EntryId, long ActivityId, string Name, string Client, double Rate, DateTime StartUtc)? Running() => Query(
        """
        SELECT e.id,a.id,a.name,e.client,e.rate,e.start FROM entries e JOIN activities a ON a.id=e.activity_id
        WHERE e."end" IS NULL AND e.deleted=0 ORDER BY e.start DESC LIMIT 1
        """, r => ((long, long, string, string, double, DateTime)?)(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDouble(4), D(r.GetString(5))))
        .FirstOrDefault();

    /// <summary>Switch the running entry to another client (at that client's rate).</summary>
    public void SetRunningClient(string client) =>
        Exec("UPDATE entries SET client=$c, rate=$r, updated_at=$t, dirty=1 WHERE \"end\" IS NULL AND deleted=0",
            ("$c", client), ("$r", RateFor(client)), ("$t", Now()));

    public void Stop() =>
        Exec("UPDATE entries SET \"end\"=$now, updated_at=$now, dirty=1 WHERE \"end\" IS NULL AND deleted=0", ("$now", Now()));

    public void Start(long activityId)
    {
        if (RunningActivityId() == activityId) return;
        // A new entry inherits the client of the entry it replaces,
        // so a stretch of work for one client carries across task switches.
        var client = Running()?.Client ?? "";
        Stop();
        var now = Now();
        Exec("INSERT INTO entries(uid,activity_id,start,client,rate,updated_at,dirty) VALUES($u,$a,$s,$c,$r,$s,1)",
            ("$u", NewUid()), ("$a", activityId), ("$s", now), ("$c", client), ("$r", RateFor(client)));
    }

    /// <summary>Called when the ZEI is flipped. Side 0 = resting/unknown.</summary>
    public void ApplySide(int side)
    {
        var a = side == 0 ? null : Activities().FirstOrDefault(x => x.Side == side);
        if (a == null) Stop(); else Start(a.Id);
    }

    // ---- entries ----
    public long AddEntry(long activityId, string client, double rate, DateTime start, DateTime? end, string note) =>
        Exec("INSERT INTO entries(uid,activity_id,client,rate,start,\"end\",note,updated_at,dirty) VALUES($u,$a,$c,$r,$s,$e,$n,$t,1)",
            ("$u", NewUid()), ("$a", activityId), ("$c", client), ("$r", rate), ("$s", S(start)), ("$e", end == null ? null : S(end.Value)), ("$n", note), ("$t", Now()));

    public void UpdateEntry(long id, long activityId, string client, double rate, DateTime start, DateTime? end, string note) =>
        Exec("UPDATE entries SET activity_id=$a,client=$c,rate=$r,start=$s,\"end\"=$e,note=$n,updated_at=$t,dirty=1 WHERE id=$id",
            ("$a", activityId), ("$c", client), ("$r", rate), ("$s", S(start)), ("$e", end == null ? null : S(end.Value)), ("$n", note), ("$t", Now()), ("$id", id));

    // A tombstone rather than a real delete, so the deletion reaches the other machines.
    public void DeleteEntry(long id) =>
        Exec("UPDATE entries SET deleted=1, updated_at=$t, dirty=1 WHERE id=$id", ("$t", Now()), ("$id", id));

    /// <summary>Entries in [from,to] (local dates), split at local midnight so each segment belongs to one day.</summary>
    public List<Segment> Segments(DateOnly from, DateOnly to, string? client = null)
    {
        var tz = TimeZoneInfo.Local;
        var lo = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
        var hi = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var now = DateTime.UtcNow;

        var rows = Query(
            """
            SELECT e.id,a.id,a.name,e.client,e.start,e."end",e.note,e.rate,a.billable FROM entries e
            JOIN activities a ON a.id=e.activity_id
            WHERE e.deleted=0 AND e.start < $hi AND (e."end" IS NULL OR e."end" > $lo)
            ORDER BY e.start
            """,
            r => (Id: r.GetInt64(0), Aid: r.GetInt64(1), Name: r.GetString(2), Client: r.GetString(3),
                  Start: D(r.GetString(4)), End: r.IsDBNull(5) ? (DateTime?)null : D(r.GetString(5)), Note: r.GetString(6),
                  Rate: r.GetDouble(7), Billable: r.GetInt64(8) != 0),
            ("$hi", S(hi)), ("$lo", S(lo)));

        var result = new List<Segment>();
        foreach (var e in rows)
        {
            if (client != null && !string.Equals(e.Client, client, StringComparison.OrdinalIgnoreCase)) continue;
            var open = e.End == null;
            var cur = e.Start > lo ? e.Start : lo;
            var end = Min(e.End ?? now, hi);
            while (cur < end)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(cur, tz);
                var nextMidnight = TimeZoneInfo.ConvertTimeToUtc(local.Date.AddDays(1), tz);
                var segEnd = Min(end, nextMidnight);
                result.Add(new Segment(e.Id, e.Aid, e.Name, e.Client, e.Rate, e.Billable, cur, segEnd, open && segEnd == now, e.Note));
                cur = segEnd;
            }
        }
        return result;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
