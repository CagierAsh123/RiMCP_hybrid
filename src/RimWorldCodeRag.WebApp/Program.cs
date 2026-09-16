using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;
using RimWorldCodeRag.Retrieval;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var config = builder.Configuration;
var webPort = config.GetValue("WebApp:Port", 5800);
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");

var app = builder.Build();
app.UseStaticFiles();

// ── Resolve index root (same logic as McpServer) ──
string ResolveIndexRoot()
{
    var fromConfig = config["RIMWORLD_INDEX_ROOT"] ?? config["WebApp:IndexRoot"];
    if (!string.IsNullOrWhiteSpace(fromConfig)) return Path.GetFullPath(fromConfig);

    var cwd = Directory.GetCurrentDirectory();
    var dir = new DirectoryInfo(cwd);
    while (dir != null)
    {
        try
        {
            if (dir.GetFiles("*.sln").Any() || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                var candidate = Path.Combine(dir.FullName, "src", "RimWorldCodeRag", "index");
                if (Directory.Exists(candidate)) return candidate;
                break;
            }
        }
        catch { }
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(cwd, "..", "..", "index"));
}

var indexRoot = ResolveIndexRoot();
var embeddingServerUrl = config["EMBEDDING_SERVER_URL"] ?? config["WebApp:EmbeddingServerUrl"];
var apiKey = config["APIKEY"] ?? config["WebApp:ApiKey"];
var modelName = config["EMBEDDING_MODELNAME"] ?? config["WebApp:ModelName"];

Console.WriteLine($"=== RiMCP Web Frontend ===");
Console.WriteLine($"Index Root: {indexRoot}");
Console.WriteLine($"Embedding Server: {embeddingServerUrl ?? "(not configured)"}");
Console.WriteLine($"Listening on: http://localhost:{webPort}");
Console.WriteLine($"==========================");

// ── Lazy-initialized retrieval services ──
var searcherLock = new object();
Lazy<RoughSearcher> lazySearcher = CreateSearcherLazy();
Lazy<GraphQuerier> lazyGraphQuerier = CreateGraphQuerierLazy();
Lazy<ExactRetriever> lazyRetriever = CreateRetrieverLazy();

Lazy<RoughSearcher> CreateSearcherLazy() => new(() =>
{
    var cfg = new RoughSearchConfig
    {
        LuceneIndexPath = Path.Combine(indexRoot, "lucene"),
        VectorIndexPath = Path.Combine(indexRoot, "vec"),
        EmbeddingServerUrl = embeddingServerUrl,
        ApiKey = apiKey,
        ModelName = modelName,
        MaxResults = 20,
        UseSemanticScoringOnly = true
    };
    return new RoughSearcher(cfg);
});

Lazy<GraphQuerier> CreateGraphQuerierLazy() => new(() =>
    new GraphQuerier(Path.Combine(indexRoot, "graph")));

Lazy<ExactRetriever> CreateRetrieverLazy() => new(() =>
    new ExactRetriever(Path.Combine(indexRoot, "lucene")));

// ── Index build state ──
var buildCts = new CancellationTokenSource();
var buildRunning = false;
var buildLog = new ConcurrentQueue<string>();
var buildProgress = "";

// ── Embedding server process ──
Process? embeddingProcess = null;

// ═══════════════════════════════════════════════════
// API 1: Search
// ═══════════════════════════════════════════════════
app.MapGet("/api/search", async (string q, string? kind, int? max) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "query is required" });

    var maxResults = Math.Clamp(max ?? 20, 1, 100);
    var startTime = DateTime.Now;

    try
    {
        IReadOnlyList<RoughSearchResult> results;
        if (kind == null && maxResults == 20)
        {
            results = await lazySearcher.Value.SearchAsync(q);
        }
        else
        {
            var perReq = new RoughSearchConfig
            {
                LuceneIndexPath = Path.Combine(indexRoot, "lucene"),
                VectorIndexPath = Path.Combine(indexRoot, "vec"),
                EmbeddingServerUrl = embeddingServerUrl,
                ApiKey = apiKey,
                ModelName = modelName,
                UseSemanticScoringOnly = true,
                Kind = kind,
                MaxResults = maxResults
            };
            using var tmp = new RoughSearcher(perReq);
            results = await tmp.SearchAsync(q);
        }

        var elapsed = DateTime.Now - startTime;
        return Results.Ok(new
        {
            results = results.Select(r => new
            {
                itemId = r.ItemId,
                symbolId = r.SymbolId,
                kind = r.Language.ToString().ToLowerInvariant(),
                symbolKind = r.SymbolKind.ToString(),
                path = r.Path,
                @namespace = r.Namespace,
                containingType = r.ContainingType,
                signature = r.Signature,
                title = ((r.Signature ?? r.Preview ?? r.SymbolId).Split('\n')[0] ?? "").Trim(),
                score = Math.Round(r.Score, 4),
                preview = r.Language == LanguageKind.CSharp ? "" : r.Preview
            }).ToArray(),
            totalFound = results.Count,
            queryTime = $"{elapsed.TotalSeconds:F2}s"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ═══════════════════════════════════════════════════
// API 2: Graph - Uses
// ═══════════════════════════════════════════════════
app.MapGet("/api/graph/uses", async (string symbol, string? kind, int? depth, int? max, int? page) =>
{
    try
    {
        var cfg = new GraphQueryConfig
        {
            ItemId = symbol.Contains('@', StringComparison.Ordinal) ? symbol : null,
            SymbolId = symbol,
            Direction = GraphDirection.Uses,
            Kind = (kind == "all" || kind == null) ? null : kind,
            MaxDepth = Math.Clamp(depth ?? 1, 1, 2)
        };
        var result = await Task.Run(() => lazyGraphQuerier.Value.Query(cfg));
        return Results.Ok(FormatGraphResult(symbol, result, "uses"));
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// ═══════════════════════════════════════════════════
// API 3: Graph - UsedBy
// ═══════════════════════════════════════════════════
app.MapGet("/api/graph/used-by", async (string symbol, string? kind, int? depth, int? max, int? page) =>
{
    try
    {
        var cfg = new GraphQueryConfig
        {
            ItemId = symbol.Contains('@', StringComparison.Ordinal) ? symbol : null,
            SymbolId = symbol,
            Direction = GraphDirection.UsedBy,
            Kind = (kind == "all" || kind == null) ? null : kind,
            MaxDepth = Math.Clamp(depth ?? 1, 1, 2)
        };
        var result = await Task.Run(() => lazyGraphQuerier.Value.Query(cfg));
        return Results.Ok(FormatGraphResult(symbol, result, "usedBy"));
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

object FormatGraphResult(string symbol, PagedGraphQueryResult result, string direction)
{
    var totalPages = (int)Math.Ceiling(result.TotalCount / (double)Math.Max(result.PageSize, 1));
    return new
    {
        sourceItem = result.Results.FirstOrDefault()?.ItemId,
        sourceSymbol = symbol,
        direction,
        edges = result.Results.Select(e => new
        {
            itemId = e.ItemId,
            targetSymbol = e.SymbolId,
            edgeKind = e.EdgeKind.ToString(),
            distance = e.Distance
        }).ToArray(),
        pagination = new
        {
            page = result.Page,
            pageSize = result.PageSize,
            totalResults = result.TotalCount,
            totalPages,
            hasNextPage = result.Page < totalPages
        }
    };
}

// ═══════════════════════════════════════════════════
// API 4: Get Item (source code)
// ═══════════════════════════════════════════════════
app.MapGet("/api/item", async (string symbol, int? maxLines) =>
{
    try
    {
        var result = await Task.Run(() => lazyRetriever.Value.GetItem(symbol, maxLines ?? 0));
        if (result == null)
            return Results.NotFound(new { error = $"Symbol '{symbol}' not found" });

        return Results.Ok(new
        {
            symbolId = result.SymbolId,
            language = result.Language.ToString().ToLowerInvariant(),
            symbolKind = result.SymbolKind.ToString(),
            path = result.Path,
            @namespace = result.Namespace,
            containingType = result.ContainingType,
            signature = result.Signature,
            defType = result.DefType,
            totalLines = result.TotalLines,
            displayedLines = result.DisplayedLines,
            truncated = result.Truncated,
            sourceCode = result.SourceCode
        });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// ═══════════════════════════════════════════════════
// API 5: Index Info
// ═══════════════════════════════════════════════════
app.MapGet("/api/index/info", () =>
{
    static (bool exists, int fileCount, double sizeMB) DirInfo(string path)
    {
        if (!Directory.Exists(path)) return (false, 0, 0);
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        var size = files.Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);
        return (true, files.Length, Math.Round(size, 1));
    }

    var lucenePath = Path.Combine(indexRoot, "lucene");
    var vecPath = Path.Combine(indexRoot, "vec");
    var graphPath = Path.Combine(indexRoot, "graph");

    var lucene = DirInfo(lucenePath);
    var vectors = DirInfo(vecPath);
    var graph = DirInfo(graphPath);

    DateTime? lastModified = null;
    foreach (var p in new[] { lucenePath, vecPath, graphPath })
    {
        if (!Directory.Exists(p)) continue;
        var latest = Directory.GetFiles(p, "*", SearchOption.AllDirectories)
            .Select(f => File.GetLastWriteTimeUtc(f))
            .DefaultIfEmpty()
            .Max();
        if (lastModified == null || latest > lastModified) lastModified = latest;
    }

    return Results.Ok(new
    {
        indexRoot,
        exists = Directory.Exists(indexRoot),
        lucene = new { lucene.exists, lucene.fileCount, lucene.sizeMB },
        vectors = new { vectors.exists, vectors.fileCount, vectors.sizeMB },
        graph = new { graph.exists, graph.fileCount, graph.sizeMB },
        lastModified
    });
});

// ═══════════════════════════════════════════════════
// API 6: Index Build
// ═══════════════════════════════════════════════════
app.MapPost("/api/index/build", async (HttpRequest req) =>
{
    if (buildRunning)
        return Results.Conflict(new { error = "A build is already running" });

    var body = await req.ReadFromJsonAsync<JsonElement>();
    var sourceRoot = body.TryGetProperty("sourceRoot", out var sr) ? sr.GetString() ?? "" : "";
    var force = body.TryGetProperty("force", out var f) ? f.GetString() ?? "none" : "none";

    if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        return Results.BadRequest(new { error = "sourceRoot is required and must exist" });

    buildRunning = true;
    buildLog.Clear();
    buildProgress = "Starting...";
    buildCts = new CancellationTokenSource();

    _ = Task.Run(async () =>
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var logWriter = new LogCapturingWriter(buildLog, origOut);
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        try
        {
            Directory.CreateDirectory(indexRoot);
            var idxConfig = new IndexingConfig
            {
                SourceRoot = sourceRoot,
                LuceneIndexPath = Path.Combine(indexRoot, "lucene"),
                VectorIndexPath = Path.Combine(indexRoot, "vec"),
                GraphPath = Path.Combine(indexRoot, "graph", "graph"),
                MetadataPath = indexRoot,
                ModelPath = Path.Combine(Directory.GetCurrentDirectory(), "models", "e5-base-v2"),
                EmbeddingServerUrl = embeddingServerUrl,
                ApiKey = apiKey,
                ModelName = modelName,
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                ForceRebuildLucene = force is "all" or "lucene",
                ForceRebuildEmbeddings = force is "all" or "embed",
                ForceRebuildGraph = force is "all" or "graph"
            };

            var pipeline = new IndexingPipeline(idxConfig);
            await pipeline.RunAsync(buildCts.Token);
            buildProgress = "Completed";
            buildLog.Enqueue("[build] Completed successfully.");

            // Reload services
            lock (searcherLock)
            {
                if (lazySearcher.IsValueCreated) (lazySearcher.Value as IDisposable)?.Dispose();
                if (lazyGraphQuerier.IsValueCreated) (lazyGraphQuerier.Value as IDisposable)?.Dispose();
                if (lazyRetriever.IsValueCreated) (lazyRetriever.Value as IDisposable)?.Dispose();
                lazySearcher = CreateSearcherLazy();
                lazyGraphQuerier = CreateGraphQuerierLazy();
                lazyRetriever = CreateRetrieverLazy();
            }
        }
        catch (Exception ex)
        {
            buildProgress = $"Failed: {ex.Message}";
            buildLog.Enqueue($"[build] ERROR: {ex.Message}");
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
            buildRunning = false;
        }
    });

    return Results.Ok(new { status = "started" });
});

app.MapGet("/api/index/status", () => Results.Ok(new
{
    running = buildRunning,
    progress = buildProgress,
    logLines = buildLog.Count
}));

app.MapGet("/api/index/log", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    var sent = 0;
    while (!ctx.RequestAborted.IsCancellationRequested)
    {
        var snapshot = buildLog.ToArray();
        for (var i = sent; i < snapshot.Length; i++)
        {
            await ctx.Response.WriteAsync($"data: {snapshot[i]}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
        sent = snapshot.Length;

        if (!buildRunning && sent >= snapshot.Length)
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
            break;
        }
        await Task.Delay(300);
    }
});

app.MapPost("/api/index/path", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var newPath = body.TryGetProperty("indexRoot", out var p) ? p.GetString() ?? "" : "";
    if (string.IsNullOrWhiteSpace(newPath) || !Directory.Exists(newPath))
        return Results.BadRequest(new { error = "Path does not exist" });

    indexRoot = Path.GetFullPath(newPath);
    lock (searcherLock)
    {
        if (lazySearcher.IsValueCreated) (lazySearcher.Value as IDisposable)?.Dispose();
        if (lazyGraphQuerier.IsValueCreated) (lazyGraphQuerier.Value as IDisposable)?.Dispose();
        if (lazyRetriever.IsValueCreated) (lazyRetriever.Value as IDisposable)?.Dispose();
        lazySearcher = CreateSearcherLazy();
        lazyGraphQuerier = CreateGraphQuerierLazy();
        lazyRetriever = CreateRetrieverLazy();
    }
    return Results.Ok(new { indexRoot });
});

// ═══════════════════════════════════════════════════
// API 7: Config
// ═══════════════════════════════════════════════════
app.MapGet("/api/config", () => Results.Ok(new
{
    indexRoot,
    dataRoot = config["WebApp:DataRoot"] ?? "",
    embeddingServerUrl = embeddingServerUrl ?? "",
    apiKey = string.IsNullOrWhiteSpace(apiKey) ? "" : "***",
    modelName = modelName ?? "",
    webPort
}));

app.MapPut("/api/config", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    var existing = File.Exists(settingsPath)
        ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(settingsPath))
        : JsonSerializer.Deserialize<JsonElement>("{}");

    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(existing.GetRawText())
               ?? new Dictionary<string, object>();

    var webApp = new Dictionary<string, object>();
    if (body.TryGetProperty("indexRoot", out var ir)) webApp["IndexRoot"] = ir.GetString() ?? "";
    if (body.TryGetProperty("embeddingServerUrl", out var eu)) webApp["EmbeddingServerUrl"] = eu.GetString() ?? "";
    if (body.TryGetProperty("apiKey", out var ak)) webApp["ApiKey"] = ak.GetString() ?? "";
    if (body.TryGetProperty("modelName", out var mn)) webApp["ModelName"] = mn.GetString() ?? "";
    if (body.TryGetProperty("webPort", out var wp)) webApp["Port"] = wp.GetInt32();

    dict["WebApp"] = webApp;
    var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(settingsPath, json);

    return Results.Ok(new { saved = true });
});

app.MapGet("/api/config/mcp-snippet", () =>
{
    var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
    var projDir = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".";

    return Results.Ok(new
    {
        claudeDesktop = new
        {
            mcpServers = new
            {
                rimworld_code_rag = new
                {
                    command = "dotnet",
                    args = new[] { "run", "--project", Path.Combine(projDir, "..", "RimWorldCodeRag.McpServer") }
                }
            }
        },
        vscode = new
        {
            servers = new
            {
                rimworld_code_rag = new
                {
                    command = "dotnet",
                    args = new[] { "run", "--project", Path.Combine(projDir, "..", "RimWorldCodeRag.McpServer") }
                }
            }
        }
    });
});

// ═══════════════════════════════════════════════════
// API 8: Models
// ═══════════════════════════════════════════════════
app.MapGet("/api/models", () =>
{
    var modelsDir = Path.Combine(Directory.GetCurrentDirectory(), "models");
    var models = new List<object>();
    if (Directory.Exists(modelsDir))
    {
        foreach (var d in Directory.GetDirectories(modelsDir))
        {
            var name = Path.GetFileName(d);
            var size = Directory.GetFiles(d, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);
            models.Add(new { name, path = d, sizeMB = Math.Round(size, 1), installed = true });
        }
    }

    var serverRunning = embeddingProcess != null && !embeddingProcess.HasExited;
    return Results.Ok(new
    {
        models,
        embeddingServer = new
        {
            running = serverRunning,
            url = embeddingServerUrl ?? "http://127.0.0.1:5000",
            model = serverRunning ? "e5-base-v2" : (string?)null
        }
    });
});

app.MapGet("/api/models/embedding-server", async () =>
{
    var running = embeddingProcess != null && !embeddingProcess.HasExited;
    if (running && !string.IsNullOrWhiteSpace(embeddingServerUrl))
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{embeddingServerUrl}/health");
            running = resp.IsSuccessStatusCode;
        }
        catch { running = false; }
    }
    return Results.Ok(new { running, url = embeddingServerUrl ?? "http://127.0.0.1:5000" });
});

app.MapPost("/api/models/embedding-server/start", () =>
{
    if (embeddingProcess != null && !embeddingProcess.HasExited)
        return Results.Ok(new { status = "already running" });

    var modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models", "e5-base-v2");
    var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "RimWorldCodeRag", "python", "embedding_server.py");
    if (!File.Exists(scriptPath))
        scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RimWorldCodeRag", "python", "embedding_server.py");

    embeddingProcess = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{scriptPath}\" --model \"{modelPath}\" --port 5000",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    embeddingProcess.Start();
    embeddingServerUrl = "http://127.0.0.1:5000";
    return Results.Ok(new { status = "started", url = embeddingServerUrl });
});

app.MapPost("/api/models/embedding-server/stop", () =>
{
    if (embeddingProcess == null || embeddingProcess.HasExited)
        return Results.Ok(new { status = "not running" });

    embeddingProcess.Kill(true);
    embeddingProcess.Dispose();
    embeddingProcess = null;
    return Results.Ok(new { status = "stopped" });
});

// ── SPA fallback ──
app.MapFallbackToFile("index.html");

// ── Auto-open browser ──
_ = Task.Run(async () =>
{
    await Task.Delay(1500);
    var url = $"http://localhost:{webPort}";
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch { }
});

app.Run();

// ═══════════════════════════════════════════════════
// Helper: Log capturing TextWriter
// ═══════════════════════════════════════════════════
class LogCapturingWriter : TextWriter
{
    private readonly ConcurrentQueue<string> _queue;
    private readonly TextWriter _inner;
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public LogCapturingWriter(ConcurrentQueue<string> queue, TextWriter inner)
    {
        _queue = queue;
        _inner = inner;
    }

    public override void WriteLine(string? value)
    {
        var line = value ?? "";
        _queue.Enqueue(line);
        _inner.WriteLine(line);
    }

    public override void Write(string? value)
    {
        if (value != null && value.Contains('\n'))
        {
            foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _queue.Enqueue(line.TrimEnd('\r'));
                _inner.Write(line);
            }
        }
        else
        {
            _inner.Write(value);
        }
    }
}
