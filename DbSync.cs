using Microsoft.Data.Sqlite;

namespace TimeTracker;

public record SyncActivity(string Uid, string Name, int? Side, bool Billable, bool Archived, string UpdatedAt);
public record SyncClient(string Uid, string Name, double Rate, bool Archived, string UpdatedAt);
public record SyncEntry(string Uid, string ActivityUid, string Client, double Rate, string Start, string? End, string Note, bool Deleted, string UpdatedAt);

// Data access used by SyncService. Rows are matched by uid; the newer updated_at wins.
// Anything changed locally is dirty=1 until it has been pushed; rows applied from remote are dirty=0.
public partial class Db
{
    public string? GetMeta(string key) =>
        Query("SELECT value FROM meta WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    public void SetMeta(string key, string value) => Quietly(() =>
        Exec("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value)));

    static T Quietly<T>(Func<T> f)
    {
        _applying = true;
        try { return f(); } finally { _applying = false; }
    }

    static void Quietly(Action a) => Quietly(() => { a(); return 0; });

    // ---- push side ----
    public List<SyncActivity> DirtyActivities() => Query(
        "SELECT uid,name,side,billable,archived,updated_at FROM activities WHERE dirty=1",
        r => new SyncActivity(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt64(3) != 0, r.GetInt64(4) != 0, r.GetString(5)));

    public List<SyncClient> DirtyClients() => Query(
        "SELECT uid,name,rate,archived,updated_at FROM clients WHERE dirty=1",
        r => new SyncClient(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3) != 0, r.GetString(4)));

    public List<SyncEntry> DirtyEntries() => Query(
        """
        SELECT e.uid,a.uid,e.client,e.rate,e.start,e."end",e.note,e.deleted,e.updated_at
        FROM entries e JOIN activities a ON a.id=e.activity_id WHERE e.dirty=1
        """,
        r => new SyncEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt64(7) != 0, r.GetString(8)));

    /// <summary>Marks a row pushed, unless it was edited again since (then it stays dirty for the next push).</summary>
    public void MarkClean(string table, string uid, string updatedAt) => Quietly(() =>
        Exec($"UPDATE {table} SET dirty=0 WHERE uid=$u AND updated_at=$t", ("$u", uid), ("$t", updatedAt)));

    // ---- pull side ----
    /// <summary>Returns true if the local row was created or changed.</summary>
    public bool ApplyActivity(SyncActivity r) => Quietly(() =>
    {
        var local = Query("SELECT updated_at FROM activities WHERE uid=$u", x => x.GetString(0), ("$u", r.Uid)).FirstOrDefault();
        if (local == null)
        {
            // Same project created independently on two machines before they ever synced: treat as one.
            var twin = Query("SELECT id FROM activities WHERE dirty=1 AND lower(name)=lower($n) AND uid<>$u LIMIT 1",
                x => x.GetInt64(0), ("$n", r.Name), ("$u", r.Uid)).FirstOrDefault();
            if (twin != 0)
            {
                Exec("UPDATE activities SET uid=$u WHERE id=$id", ("$u", r.Uid), ("$id", twin));
                local = Query("SELECT updated_at FROM activities WHERE uid=$u", x => x.GetString(0), ("$u", r.Uid)).First();
            }
        }
        if (local == null)
            Exec("INSERT INTO activities(uid,name,side,billable,archived,updated_at,dirty) VALUES($u,$n,$s,$b,$a,$t,0)",
                ("$u", r.Uid), ("$n", r.Name), ("$s", r.Side), ("$b", r.Billable ? 1 : 0), ("$a", r.Archived ? 1 : 0), ("$t", r.UpdatedAt));
        else if (string.CompareOrdinal(r.UpdatedAt, local) > 0)
            Exec("UPDATE activities SET name=$n,side=$s,billable=$b,archived=$a,updated_at=$t,dirty=0 WHERE uid=$u",
                ("$u", r.Uid), ("$n", r.Name), ("$s", r.Side), ("$b", r.Billable ? 1 : 0), ("$a", r.Archived ? 1 : 0), ("$t", r.UpdatedAt));
        else return false;
        return true;
    });

    public bool ApplyClient(SyncClient r) => Quietly(() =>
    {
        var local = Query("SELECT updated_at FROM clients WHERE uid=$u", x => x.GetString(0), ("$u", r.Uid)).FirstOrDefault();
        if (local == null)
        {
            // Client names are unique here, so a same-named client is the same client.
            var twin = Query("SELECT id FROM clients WHERE name=$n LIMIT 1", x => x.GetInt64(0), ("$n", r.Name)).FirstOrDefault();
            if (twin != 0)
            {
                Exec("UPDATE clients SET uid=$u WHERE id=$id", ("$u", r.Uid), ("$id", twin));
                local = Query("SELECT updated_at FROM clients WHERE uid=$u", x => x.GetString(0), ("$u", r.Uid)).First();
            }
        }
        if (local == null)
            Exec("INSERT INTO clients(uid,name,rate,archived,updated_at,dirty) VALUES($u,$n,$r,$a,$t,0)",
                ("$u", r.Uid), ("$n", r.Name), ("$r", r.Rate), ("$a", r.Archived ? 1 : 0), ("$t", r.UpdatedAt));
        else if (string.CompareOrdinal(r.UpdatedAt, local) > 0)
            Exec("UPDATE clients SET name=$n,rate=$r,archived=$a,updated_at=$t,dirty=0 WHERE uid=$u",
                ("$u", r.Uid), ("$n", r.Name), ("$r", r.Rate), ("$a", r.Archived ? 1 : 0), ("$t", r.UpdatedAt));
        else return false;
        return true;
    });

    /// <summary>Applies a pulled entry. Missing = its project isn't known locally yet (retry on the next pull).</summary>
    public (bool Applied, bool Missing) ApplyEntry(SyncEntry r) => Quietly(() =>
    {
        var aid = Query("SELECT id FROM activities WHERE uid=$u", x => (long?)x.GetInt64(0), ("$u", r.ActivityUid)).FirstOrDefault();
        if (aid == null) return (false, true);
        var local = Query("SELECT updated_at FROM entries WHERE uid=$u", x => x.GetString(0), ("$u", r.Uid)).FirstOrDefault();
        if (local == null)
            Exec("INSERT INTO entries(uid,activity_id,client,rate,start,\"end\",note,deleted,updated_at,dirty) VALUES($u,$a,$c,$r,$s,$e,$n,$d,$t,0)",
                ("$u", r.Uid), ("$a", aid), ("$c", r.Client), ("$r", r.Rate), ("$s", r.Start), ("$e", r.End), ("$n", r.Note), ("$d", r.Deleted ? 1 : 0), ("$t", r.UpdatedAt));
        else if (string.CompareOrdinal(r.UpdatedAt, local) > 0)
            Exec("UPDATE entries SET activity_id=$a,client=$c,rate=$r,start=$s,\"end\"=$e,note=$n,deleted=$d,updated_at=$t,dirty=0 WHERE uid=$u",
                ("$u", r.Uid), ("$a", aid), ("$c", r.Client), ("$r", r.Rate), ("$s", r.Start), ("$e", r.End), ("$n", r.Note), ("$d", r.Deleted ? 1 : 0), ("$t", r.UpdatedAt));
        else return (false, false);
        return (true, false);
    });

    /// <summary>
    /// If two machines each started a timer before they synced there are two open entries; keep the newest
    /// running and end each older one when the next one began. Returns true if anything changed.
    /// </summary>
    public bool ReconcileOpenEntries()
    {
        var open = Query("SELECT id,start FROM entries WHERE \"end\" IS NULL AND deleted=0 ORDER BY start DESC",
            r => (Id: r.GetInt64(0), Start: r.GetString(1)));
        for (var i = 1; i < open.Count; i++)
            Exec("UPDATE entries SET \"end\"=$e, updated_at=$t, dirty=1 WHERE id=$id", ("$e", open[i - 1].Start), ("$t", Now()), ("$id", open[i].Id));
        return open.Count > 1;
    }
}
