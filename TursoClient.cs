using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TimeTracker;

/// <summary>Where to sync to. Read from TT_TURSO_URL / TT_TURSO_TOKEN, or turso.json in the data folder.</summary>
public record TursoConfig(string Url, string Token)
{
    public static TursoConfig? Load(string dataDir)
    {
        var url = Environment.GetEnvironmentVariable("TT_TURSO_URL");
        var token = Environment.GetEnvironmentVariable("TT_TURSO_TOKEN");
        var file = Path.Combine(dataDir, "turso.json");
        if ((string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) && File.Exists(file))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("url", out var u)) url ??= u.GetString();
            if (doc.RootElement.TryGetProperty("token", out var t)) token ??= t.GetString();
        }
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return null;
        return new TursoConfig(url.Trim(), token.Trim());
    }
}

/// <summary>Minimal Turso/libSQL client over the HTTP pipeline API (no native libraries, so it publishes everywhere).</summary>
public sealed class TursoClient : IDisposable
{
    readonly HttpClient _http;
    readonly Uri _endpoint;

    public TursoClient(TursoConfig cfg)
    {
        var url = cfg.Url.Replace("libsql://", "https://").TrimEnd('/');
        _endpoint = new Uri(url + "/v2/pipeline");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
    }

    public sealed record Result(List<string> Cols, List<object?[]> Rows);

    /// <summary>One statement: SQL with positional ? arguments (string, long, int, bool, double, null).</summary>
    public async Task<Result> Execute(string sql, object?[]? args = null, CancellationToken ct = default) =>
        (await Batch([(sql, args)], ct))[0];

    /// <summary>Runs statements in order on one connection. Throws if any fails.</summary>
    public async Task<List<Result>> Batch(IReadOnlyList<(string Sql, object?[]? Args)> stmts, CancellationToken ct = default)
    {
        var requests = new JsonArray();
        foreach (var (sql, args) in stmts)
        {
            var stmt = new JsonObject { ["sql"] = sql };
            if (args is { Length: > 0 }) stmt["args"] = new JsonArray(args.Select(Arg).ToArray());
            requests.Add(new JsonObject { ["type"] = "execute", ["stmt"] = stmt });
        }
        requests.Add(new JsonObject { ["type"] = "close" });

        using var body = new StringContent(new JsonObject { ["requests"] = requests }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_endpoint, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new IOException($"Turso HTTP {(int)resp.StatusCode}: {Trim(text)}");

        var results = JsonNode.Parse(text)!["results"]!.AsArray();
        var list = new List<Result>();
        for (var i = 0; i < stmts.Count; i++)
        {
            var r = results[i]!;
            if (r["type"]!.GetValue<string>() != "ok")
                throw new IOException("Turso error: " + Trim(r["error"]?["message"]?.GetValue<string>() ?? r.ToJsonString()));
            var res = r["response"]!["result"]!;
            var cols = res["cols"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
            var rows = res["rows"]!.AsArray().Select(row => row!.AsArray().Select(Cell).ToArray()).ToList();
            list.Add(new Result(cols, rows));
        }
        return list;
    }

    static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    static JsonNode Arg(object? v) => v switch
    {
        null => new JsonObject { ["type"] = "null" },
        bool b => new JsonObject { ["type"] = "integer", ["value"] = b ? "1" : "0" },
        int or long => new JsonObject { ["type"] = "integer", ["value"] = Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture) },
        double d => new JsonObject { ["type"] = "float", ["value"] = d },
        _ => new JsonObject { ["type"] = "text", ["value"] = v.ToString() },
    };

    static object? Cell(JsonNode? n)
    {
        var type = n!["type"]!.GetValue<string>();
        return type switch
        {
            "null" => null,
            "integer" => long.Parse(n["value"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            "float" => n["value"]!.GetValue<double>(),
            _ => n["value"]!.GetValue<string>(),
        };
    }

    public void Dispose() => _http.Dispose();
}
