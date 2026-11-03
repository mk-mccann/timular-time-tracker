using Microsoft.Data.Sqlite;

namespace TimeTracker;

public record Activity(long Id, string Name, string Client, int? Side);

public record Segment(long EntryId, long ActivityId, string Name, string Client,
                      DateTime StartUtc, DateTime EndUtc, bool Running, string Note)
{
    public double Seconds => (EndUtc - StartUtc).TotalSeconds;
    public DateOnly Day => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(StartUtc, TimeZoneInfo.Local));
}

public class Db
{
    const string Fmt = "yyyy-MM-ddTHH:mm:ss.fffZ";
    readonly string _cs;
    readonly object _lock = new();

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = $"Data Source={path}";
        Exec("""
            CREATE TABLE IF NOT EXISTS activities(
              id INTEGER PRIMARY KEY, name TEXT NOT NULL, client TEXT NOT NULL DEFAULT '',
              side INTEGER NULL, archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS entries(
              id INTEGER PRIMARY KEY, activity_id INTEGER NOT NULL REFERENCES activities(id),
              start TEXT NOT NULL, "end" TEXT NULL, note TEXT NOT NULL DEFAULT '');
            """);
    }

    static string S(DateTime d) => d.ToUniversalTime().ToString(Fmt);
    static DateTime D(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    long Exec(string sql, params (string, object?)[] p)
    {
        lock (_lock)
        {
            using var c = new SqliteConnection(_cs); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT last_insert_rowid()";
            return (long)cmd.ExecuteScalar()!;
        }
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
    public List<Activity> Activities() => Query(
        "SELECT id,name,client,side FROM activities WHERE archived=0 ORDER BY client,name",
        r => new Activity(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3)));

    public long AddActivity(string name, string client, int? side)
    {
        ClearSide(side);
        return Exec("INSERT INTO activities(name,client,side) VALUES($n,$c,$s)", ("$n", name), ("$c", client), ("$s", side));
    }

    public void UpdateActivity(long id, string name, string client, int? side)
    {
        ClearSide(side, id);
        Exec("UPDATE activities SET name=$n,client=$c,side=$s WHERE id=$id", ("$n", name), ("$c", client), ("$s", side), ("$id", id));
    }

    // Archived (not deleted) so past entries and reports keep their names.
    public void ArchiveActivity(long id) =>
        Exec("UPDATE activities SET archived=1, side=NULL WHERE id=$id", ("$id", id));

    void ClearSide(int? side, long exceptId = -1)
    {
        if (side != null) Exec("UPDATE activities SET side=NULL WHERE side=$s AND id<>$id", ("$s", side), ("$id", exceptId));
    }

    // ---- tracking ----
    public long? RunningActivityId() => Query("SELECT activity_id FROM entries WHERE \"end\" IS NULL LIMIT 1",
        r => (long?)r.GetInt64(0)).FirstOrDefault();

    public (long EntryId, long ActivityId, string Name, string Client, DateTime StartUtc)? Running() => Query(
        """
        SELECT e.id,a.id,a.name,a.client,e.start FROM entries e JOIN activities a ON a.id=e.activity_id
        WHERE e."end" IS NULL ORDER BY e.start DESC LIMIT 1
        """, r => ((long, long, string, string, DateTime)?)(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), D(r.GetString(4))))
        .FirstOrDefault();

    public void Stop() =>
        Exec("UPDATE entries SET \"end\"=$now WHERE \"end\" IS NULL", ("$now", S(DateTime.UtcNow)));

    public void Start(long activityId)
    {
        if (RunningActivityId() == activityId) return;
        Stop();
        Exec("INSERT INTO entries(activity_id,start) VALUES($a,$s)", ("$a", activityId), ("$s", S(DateTime.UtcNow)));
    }

    /// <summary>Called when the ZEI is flipped. Side 0 = resting/unknown.</summary>
    public void ApplySide(int side)
    {
        var a = side == 0 ? null : Activities().FirstOrDefault(x => x.Side == side);
        if (a == null) Stop(); else Start(a.Id);
    }

    // ---- entries ----
    public long AddEntry(long activityId, DateTime start, DateTime? end, string note) =>
        Exec("INSERT INTO entries(activity_id,start,\"end\",note) VALUES($a,$s,$e,$n)",
            ("$a", activityId), ("$s", S(start)), ("$e", end == null ? null : S(end.Value)), ("$n", note));

    public void UpdateEntry(long id, long activityId, DateTime start, DateTime? end, string note) =>
        Exec("UPDATE entries SET activity_id=$a,start=$s,\"end\"=$e,note=$n WHERE id=$id",
            ("$a", activityId), ("$s", S(start)), ("$e", end == null ? null : S(end.Value)), ("$n", note), ("$id", id));

    public void DeleteEntry(long id) => Exec("DELETE FROM entries WHERE id=$id", ("$id", id));

    /// <summary>Entries in [from,to] (local dates), split at local midnight so each segment belongs to one day.</summary>
    public List<Segment> Segments(DateOnly from, DateOnly to, string? client = null)
    {
        var tz = TimeZoneInfo.Local;
        var lo = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
        var hi = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
        var now = DateTime.UtcNow;

        var rows = Query(
            """
            SELECT e.id,a.id,a.name,a.client,e.start,e."end",e.note FROM entries e
            JOIN activities a ON a.id=e.activity_id
            WHERE e.start < $hi AND (e."end" IS NULL OR e."end" > $lo)
            ORDER BY e.start
            """,
            r => (Id: r.GetInt64(0), Aid: r.GetInt64(1), Name: r.GetString(2), Client: r.GetString(3),
                  Start: D(r.GetString(4)), End: r.IsDBNull(5) ? (DateTime?)null : D(r.GetString(5)), Note: r.GetString(6)),
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
                result.Add(new Segment(e.Id, e.Aid, e.Name, e.Client, cur, segEnd, open && segEnd == now, e.Note));
                cur = segEnd;
            }
        }
        return result;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
