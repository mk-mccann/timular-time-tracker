using System.Diagnostics;
using TimeTracker;

// TT_PORT / TT_DATA_DIR let a second copy run side by side (e.g. to test sync on one machine).
var port = int.TryParse(Environment.GetEnvironmentVariable("TT_PORT"), out var p) ? p : 5178;
var Url = $"http://localhost:{port}";
void OpenPage()
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", Url);
    }
    catch (Exception e) { Console.WriteLine($"Open {Url} in your browser ({e.Message})"); }
}

// Already running: show the existing page rather than failing on the port with nothing on screen.
using var instance = new Mutex(true, @"Local\TimularTracker" + (port == 5178 ? "" : port.ToString()), out var firstInstance);
if (!firstInstance)
{
    OpenPage();
    return;
}

var dataDir = Environment.GetEnvironmentVariable("TT_DATA_DIR") is { Length: > 0 } custom ? custom
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimularTracker");
Directory.CreateDirectory(dataDir);
var logPath = Path.Combine(dataDir, "tracker.log");
File.WriteAllText(logPath, ""); // each launch starts a fresh log
AppDomain.CurrentDomain.UnhandledException += (_, e) => File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Fatal: {e.ExceptionObject}{Environment.NewLine}");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Url);
var fileLog = new FileLoggerProvider(logPath);
builder.Logging.AddProvider(fileLog);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // the page polls, so per-request lines would pile up

var dbPath = Path.Combine(dataDir, "tracker.db");
var db = new Db(dbPath);
var zei = new ZeiTracker(db, fileLog);
var sync = new SyncService(db, TursoConfig.Load(dataDir), fileLog);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(zei);
builder.Services.AddSingleton(sync);
builder.Services.AddHostedService(_ => zei);
builder.Services.AddHostedService(_ => sync);

var app = builder.Build();
string indexHtml;
using (var s = typeof(Program).Assembly.GetManifestResourceStream("index.html")!)
using (var rd = new StreamReader(s)) indexHtml = rd.ReadToEnd();
app.MapGet("/", () => Results.Content(indexHtml, "text/html; charset=utf-8"));

static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
static DateOnly ParseDay(string? s) => s != null ? DateOnly.Parse(s) : DateOnly.FromDateTime(DateTime.Now);

app.MapGet("/api/state", (Db db, ZeiTracker zei, SyncService sync) =>
{
    var r = db.Running();
    return new
    {
        running = r == null ? null : new { entryId = r.Value.EntryId, activityId = r.Value.ActivityId, name = r.Value.Name, client = r.Value.Client, rate = r.Value.Rate, start = r.Value.StartUtc },
        device = new { status = zei.Status, connected = zei.Connected, side = zei.Side, raw = zei.RawSide },
        sync = new { enabled = sync.Enabled, status = sync.Status, last = sync.LastSync, revision = sync.Revision },
    };
});

app.MapGet("/api/activities", (Db db) => db.Activities());
app.MapPost("/api/activities", (Db db, ActivityIn a) => new { id = db.AddActivity(a.Name.Trim(), a.Side, a.Billable ?? true) });
app.MapPut("/api/activities/{id:long}", (Db db, long id, ActivityIn a) => { db.UpdateActivity(id, a.Name.Trim(), a.Side, a.Billable ?? true); return Results.NoContent(); });
app.MapDelete("/api/activities/{id:long}", (Db db, long id) => { db.ArchiveActivity(id); return Results.NoContent(); });

app.MapGet("/api/clients", (Db db) => db.Clients());
app.MapPost("/api/clients", (Db db, ClientIn c) => new { id = db.AddClient(c.Name.Trim(), c.Rate) });
app.MapPut("/api/clients/{id:long}", (Db db, long id, ClientIn c) => { db.UpdateClient(id, c.Name.Trim(), c.Rate); return Results.NoContent(); });
app.MapDelete("/api/clients/{id:long}", (Db db, long id) => { db.ArchiveClient(id); return Results.NoContent(); });
app.MapPost("/api/running/client", (Db db, ClientIn c) => { db.SetRunningClient(c.Name.Trim()); return Results.NoContent(); });

app.MapPost("/api/start/{id:long}", (Db db, long id) => { db.Start(id); return Results.NoContent(); });
app.MapPost("/api/stop", (Db db) => { db.Stop(); return Results.NoContent(); });

app.MapGet("/api/day", (Db db, string? date) =>
{
    var d = ParseDay(date);
    return db.Segments(d, d).Select(s => new
    {
        s.EntryId, s.ActivityId, s.Name, s.Client, s.Rate, s.Billable, start = s.StartUtc, end = s.EndUtc, s.Running, s.Note,
    });
});

app.MapPost("/api/entries", (Db db, EntryIn e) => new { id = db.AddEntry(e.ActivityId, e.Client?.Trim() ?? "", e.Rate ?? 0, Utc(e.Start), e.End == null ? null : Utc(e.End.Value), e.Note ?? "") });
app.MapPut("/api/entries/{id:long}", (Db db, long id, EntryIn e) => { db.UpdateEntry(id, e.ActivityId, e.Client?.Trim() ?? "", e.Rate ?? 0, Utc(e.Start), e.End == null ? null : Utc(e.End.Value), e.Note ?? ""); return Results.NoContent(); });
app.MapDelete("/api/entries/{id:long}", (Db db, long id) => { db.DeleteEntry(id); return Results.NoContent(); });

app.MapGet("/api/export", (Db db, string from, string to, string? client) =>
{
    var f = DateOnly.Parse(from); var t = DateOnly.Parse(to);
    var segs = db.Segments(f, t, string.IsNullOrWhiteSpace(client) ? null : client);
    var name = $"timesheet_{f:yyyyMMdd}-{t:yyyyMMdd}{(string.IsNullOrWhiteSpace(client) ? "" : "_" + client.Replace(' ', '_'))}.xlsx";
    return Results.File(Exporter.ToXlsx(segs, f, t), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!args.Contains("--no-browser"))
        OpenPage();
});

#if WINDOWS
var tray = Tray.Start(app.Lifetime, OpenPage);
app.Run();
tray.Join(TimeSpan.FromSeconds(2)); // let the tray icon remove itself
#else
Console.WriteLine($"Timular Tracker running at {Url} (Ctrl+C to quit)");
app.Run();
#endif

record ActivityIn(string Name, int? Side, bool? Billable);
record ClientIn(string Name, double Rate);
record EntryIn(long ActivityId, string? Client, double? Rate, DateTime Start, DateTime? End, string? Note);
