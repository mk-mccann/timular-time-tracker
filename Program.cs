using System.Diagnostics;
using TimeTracker;

const string Url = "http://localhost:5178";
static void OpenPage()
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", Url);
    }
    catch (Exception e) { Console.WriteLine($"Open {Url} in your browser ({e.Message})"); }
}

// Already running: show the existing page rather than failing on the port with nothing on screen.
using var instance = new Mutex(true, @"Local\TimularTracker", out var firstInstance);
if (!firstInstance)
{
    OpenPage();
    return;
}

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimularTracker");
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
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(zei);
builder.Services.AddHostedService(_ => zei);

var app = builder.Build();
string indexHtml;
using (var s = typeof(Program).Assembly.GetManifestResourceStream("index.html")!)
using (var rd = new StreamReader(s)) indexHtml = rd.ReadToEnd();
app.MapGet("/", () => Results.Content(indexHtml, "text/html; charset=utf-8"));

static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
static DateOnly ParseDay(string? s) => s != null ? DateOnly.Parse(s) : DateOnly.FromDateTime(DateTime.Now);

app.MapGet("/api/state", (Db db, ZeiTracker zei) =>
{
    var r = db.Running();
    return new
    {
        running = r == null ? null : new { entryId = r.Value.EntryId, activityId = r.Value.ActivityId, name = r.Value.Name, client = r.Value.Client, start = r.Value.StartUtc },
        device = new { status = zei.Status, connected = zei.Connected, side = zei.Side, raw = zei.RawSide },
    };
});

app.MapGet("/api/activities", (Db db) => db.Activities());
app.MapPost("/api/activities", (Db db, ActivityIn a) => new { id = db.AddActivity(a.Name.Trim(), a.Client?.Trim() ?? "", a.Side) });
app.MapPut("/api/activities/{id:long}", (Db db, long id, ActivityIn a) => { db.UpdateActivity(id, a.Name.Trim(), a.Client?.Trim() ?? "", a.Side); return Results.NoContent(); });
app.MapDelete("/api/activities/{id:long}", (Db db, long id) => { db.ArchiveActivity(id); return Results.NoContent(); });

app.MapPost("/api/start/{id:long}", (Db db, long id) => { db.Start(id); return Results.NoContent(); });
app.MapPost("/api/stop", (Db db) => { db.Stop(); return Results.NoContent(); });

app.MapGet("/api/day", (Db db, string? date) =>
{
    var d = ParseDay(date);
    return db.Segments(d, d).Select(s => new
    {
        s.EntryId, s.ActivityId, s.Name, s.Client, start = s.StartUtc, end = s.EndUtc, s.Running, s.Note,
    });
});

app.MapPost("/api/entries", (Db db, EntryIn e) => new { id = db.AddEntry(e.ActivityId, Utc(e.Start), e.End == null ? null : Utc(e.End.Value), e.Note ?? "") });
app.MapPut("/api/entries/{id:long}", (Db db, long id, EntryIn e) => { db.UpdateEntry(id, e.ActivityId, Utc(e.Start), e.End == null ? null : Utc(e.End.Value), e.Note ?? ""); return Results.NoContent(); });
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

record ActivityIn(string Name, string? Client, int? Side);
record EntryIn(long ActivityId, DateTime Start, DateTime? End, string? Note);
