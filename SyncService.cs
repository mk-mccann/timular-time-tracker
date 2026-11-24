namespace TimeTracker;

/// <summary>
/// Keeps the local database in step with a shared Turso database. The local SQLite file stays the working copy
/// (the app works offline); this pulls other machines' changes and pushes ours, newest edit wins per row.
/// </summary>
public class SyncService(Db db, TursoConfig? cfg, ILogger log) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("TT_SYNC_SECONDS"), out var s) && s >= 5 ? s : 30);
    const int Page = 500;
    const int Chunk = 100;
    const string ServerNow = "strftime('%Y-%m-%dT%H:%M:%fZ','now')";

    readonly SemaphoreSlim _wake = new(0, 1);
    TursoClient? _turso;
    bool _schemaReady;

    public bool Enabled => cfg != null;
    public string Status { get; private set; } = cfg == null ? "Sync off" : "Sync starting…";
    public DateTime? LastSync { get; private set; }
    /// <summary>Bumped whenever a pull changed local data, so the page knows to reload.</summary>
    public int Revision { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (cfg == null) return;
        _turso = new TursoClient(cfg);
        db.Changed += Wake;
        try
        {
            var signalled = false;
            while (!ct.IsCancellationRequested)
            {
                if (signalled) await Task.Delay(TimeSpan.FromSeconds(1.5), ct); // let a burst of edits settle
                try
                {
                    await SyncOnce(ct);
                    Status = "Synced";
                    LastSync = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    Status = "Sync error: " + e.Message;
                    log.LogWarning("Sync: {Message}", e.Message);
                }
                signalled = await _wake.WaitAsync(Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { db.Changed -= Wake; _turso.Dispose(); }
    }

    void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    async Task SyncOnce(CancellationToken ct)
    {
        var t = _turso!;
        if (!_schemaReady) { await EnsureSchema(t, ct); _schemaReady = true; }

        var changed = false;
        changed |= await PullActivities(t, ct);
        changed |= await PullClients(t, ct);
        changed |= await PullEntries(t, ct);
        db.ReconcileOpenEntries(); // two timers started before syncing: keep the newest
        if (changed) Revision++;

        await Push(t, ct);
    }

    static async Task EnsureSchema(TursoClient t, CancellationToken ct) => await t.Batch(
    [
        ("""
         CREATE TABLE IF NOT EXISTS activities(
           uid TEXT PRIMARY KEY, name TEXT NOT NULL, side INTEGER, billable INTEGER NOT NULL, archived INTEGER NOT NULL,
           updated_at TEXT NOT NULL, synced_at TEXT NOT NULL)
         """, null),
        ("""
         CREATE TABLE IF NOT EXISTS clients(
           uid TEXT PRIMARY KEY, name TEXT NOT NULL, rate REAL NOT NULL, archived INTEGER NOT NULL,
           updated_at TEXT NOT NULL, synced_at TEXT NOT NULL)
         """, null),
        ("""
         CREATE TABLE IF NOT EXISTS entries(
           uid TEXT PRIMARY KEY, activity_uid TEXT NOT NULL, client TEXT NOT NULL, rate REAL NOT NULL,
           start TEXT NOT NULL, "end" TEXT, note TEXT NOT NULL, deleted INTEGER NOT NULL,
           updated_at TEXT NOT NULL, synced_at TEXT NOT NULL)
         """, null),
        ("CREATE INDEX IF NOT EXISTS ix_activities_synced ON activities(synced_at, uid)", null),
        ("CREATE INDEX IF NOT EXISTS ix_clients_synced ON clients(synced_at, uid)", null),
        ("CREATE INDEX IF NOT EXISTS ix_entries_synced ON entries(synced_at, uid)", null),
    ], ct);

    // ---- pull ----
    // Remote rows carry a server-assigned synced_at. We remember the newest one seen per table and ask for
    // everything at or after it (re-applying a row is harmless), paging by (synced_at, uid).

    async Task<bool> PullActivities(TursoClient t, CancellationToken ct) => await Pull(t, "activities",
        "uid,name,side,billable,archived,updated_at,synced_at", row =>
        {
            var changed = db.ApplyActivity(new SyncActivity(Str(row[0]), Str(row[1]), row[2] == null ? null : (int)Convert.ToInt64(row[2]),
                Convert.ToInt64(row[3]) != 0, Convert.ToInt64(row[4]) != 0, Str(row[5])));
            return changed ? Outcome.Changed : Outcome.Same;
        }, ct);

    async Task<bool> PullClients(TursoClient t, CancellationToken ct) => await Pull(t, "clients",
        "uid,name,rate,archived,updated_at,synced_at", row =>
        {
            var changed = db.ApplyClient(new SyncClient(Str(row[0]), Str(row[1]), Convert.ToDouble(row[2]), Convert.ToInt64(row[3]) != 0, Str(row[4])));
            return changed ? Outcome.Changed : Outcome.Same;
        }, ct);

    async Task<bool> PullEntries(TursoClient t, CancellationToken ct) => await Pull(t, "entries",
        "uid,activity_uid,client,rate,start,\"end\",note,deleted,updated_at,synced_at", row =>
        {
            var (applied, missing) = db.ApplyEntry(new SyncEntry(Str(row[0]), Str(row[1]), Str(row[2]), Convert.ToDouble(row[3]), Str(row[4]),
                row[5] == null ? null : Str(row[5]), Str(row[6]), Convert.ToInt64(row[7]) != 0, Str(row[8])));
            return missing ? Outcome.Missing : applied ? Outcome.Changed : Outcome.Same;
        }, ct);

    enum Outcome { Same, Changed, Missing }

    // Applies each remote row and reports whether local data actually changed.
    async Task<bool> Pull(TursoClient t, string table, string cols, Func<object?[], Outcome> apply, CancellationToken ct)
    {
        var key = "pull_" + table;
        var marker = db.GetMeta(key) ?? "";
        string? advance = null;   // newest synced_at applied with nothing unresolved before it
        var blocked = false;
        var any = false;
        string? curTs = null, curUid = null;

        while (true)
        {
            var sql = $"SELECT {cols} FROM {table} WHERE " +
                      (curUid == null ? "synced_at >= ?" : "(synced_at > ?1 OR (synced_at = ?1 AND uid > ?2))") +
                      $" ORDER BY synced_at, uid LIMIT {Page}";
            var res = await t.Execute(sql, curUid == null ? new object?[] { marker } : new object?[] { curTs, curUid }, ct);
            foreach (var row in res.Rows)
            {
                var uid = Str(row[0]);
                var ts = Str(row[^1]);
                var outcome = apply(row);
                if (outcome == Outcome.Missing) blocked = true;
                else if (!blocked) advance = ts;
                if (outcome == Outcome.Changed) any = true;
                curTs = ts; curUid = uid;
            }
            if (res.Rows.Count < Page) break;
            ct.ThrowIfCancellationRequested();
        }

        if (advance != null && string.CompareOrdinal(advance, marker) > 0) db.SetMeta(key, advance);
        if (blocked) log.LogInformation("Sync: some {Table} rows are waiting for their project; will retry", table);
        return any;
    }

    static string Str(object? o) => (string)o!;

    // ---- push ----
    async Task Push(TursoClient t, CancellationToken ct)
    {
        foreach (var chunk in db.DirtyActivities().Chunk(Chunk))
        {
            await t.Batch(chunk.Select(a => ((string, object?[]?))(Upsert("activities", "uid,name,side,billable,archived,updated_at",
                "name=excluded.name,side=excluded.side,billable=excluded.billable,archived=excluded.archived,updated_at=excluded.updated_at"),
                [a.Uid, a.Name, a.Side, a.Billable, a.Archived, a.UpdatedAt])).ToList(), ct);
            foreach (var a in chunk) db.MarkClean("activities", a.Uid, a.UpdatedAt);
        }
        foreach (var chunk in db.DirtyClients().Chunk(Chunk))
        {
            await t.Batch(chunk.Select(c => ((string, object?[]?))(Upsert("clients", "uid,name,rate,archived,updated_at",
                "name=excluded.name,rate=excluded.rate,archived=excluded.archived,updated_at=excluded.updated_at"),
                [c.Uid, c.Name, c.Rate, c.Archived, c.UpdatedAt])).ToList(), ct);
            foreach (var c in chunk) db.MarkClean("clients", c.Uid, c.UpdatedAt);
        }
        foreach (var chunk in db.DirtyEntries().Chunk(Chunk))
        {
            await t.Batch(chunk.Select(e => ((string, object?[]?))(Upsert("entries", "uid,activity_uid,client,rate,start,\"end\",note,deleted,updated_at",
                "activity_uid=excluded.activity_uid,client=excluded.client,rate=excluded.rate,start=excluded.start,\"end\"=excluded.\"end\"," +
                "note=excluded.note,deleted=excluded.deleted,updated_at=excluded.updated_at"),
                [e.Uid, e.ActivityUid, e.Client, e.Rate, e.Start, e.End, e.Note, e.Deleted, e.UpdatedAt])).ToList(), ct);
            foreach (var e in chunk) db.MarkClean("entries", e.Uid, e.UpdatedAt);
        }
    }

    // Insert, or overwrite only if this version is newer than what's there. synced_at is the server's clock.
    static string Upsert(string table, string cols, string sets)
    {
        var n = cols.Split(',').Length;
        return $"INSERT INTO {table}({cols},synced_at) VALUES({string.Join(",", Enumerable.Repeat("?", n))},{ServerNow}) " +
               $"ON CONFLICT(uid) DO UPDATE SET {sets},synced_at={ServerNow} WHERE excluded.updated_at > {table}.updated_at";
    }
}
