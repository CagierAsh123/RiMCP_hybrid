namespace RimWorldCodeRag;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Evaluation;
using RimWorldCodeRag.Indexer;
using RimWorldCodeRag.Retrieval;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || HelpRequested(args))
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var tail = args.Skip(1).ToArray();
        switch (command)
        {
            case "index":
                return await RunIndexAsync(tail);
            case "rough-search":
                return await RunRoughSearchAsync(tail);
            case "get-uses":
                return RunGetUses(tail);
            case "get-used-by":
                return RunGetUsedBy(tail);
            case "get-item":
                return RunGetItem(tail);
            case "bench":
                return await RetrievalBenchmark.RunAsync(ParseOptions(tail));
            case "pack-vectors":
                return RunPackVectors(tail);
            case "build-mod-catalog":
                return RunBuildModCatalog(tail);
            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                PrintUsage();
                return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            if (key is "no-incremental")
            {
                result[key] = "true";
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            {
                result[key] = "all"; // --force without value means --force all
                continue;
            }

            var value = args[++i];

            // Repeated --force flags must accumulate ("--force lucene --force graph"),
            // otherwise the last one silently wins.
            if (key.Equals("force", StringComparison.OrdinalIgnoreCase) && result.TryGetValue(key, out var prior))
            {
                result[key] = $"{prior},{value}";
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string GetOrDefault(Dictionary<string, string> options, string key, string fallback)
    {
        return options.TryGetValue(key, out var value) ? value : fallback;
    }

    private static async Task<int> RunIndexAsync(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("root", out var root))
        {
            Console.Error.WriteLine("Missing required option --root <path>.");
            return 1;
        }

        var lucene = GetOrDefault(options, "lucene", Path.Combine("index", "lucene"));
        var vec = GetOrDefault(options, "vec", Path.Combine("index", "vec"));
        var graph = GetOrDefault(options, "graph", Path.Combine("index", "graph.db"));
        var meta = GetOrDefault(options, "meta", Path.Combine("index", "meta"));
        var model = GetOrDefault(options, "model", Path.Combine("models", "e5-base-v2"));
        var apiKey = GetOrDefault(options, "api-key", "");
        var modelName = GetOrDefault(options, "model-name", "");
        var pythonExec = GetOrDefault(options, "python-exec", "python");
        var pythonScriptCandidate = GetOrDefault(options, "python-script", Path.Combine("python", "embed.py"));
        var pythonBatch = int.TryParse(GetOrDefault(options, "python-batch", "1024"), out var parsedBatch) ? Math.Max(1, parsedBatch) : 1024;
        var embeddingServerUrl = GetOrDefault(options, "embedding-server", "");

        string? resolvedPythonScript = null;
        if (!string.IsNullOrWhiteSpace(pythonScriptCandidate) && File.Exists(pythonScriptCandidate))
        {
            resolvedPythonScript = Path.GetFullPath(pythonScriptCandidate);
        }

        var threads = int.TryParse(GetOrDefault(options, "threads", Environment.ProcessorCount.ToString()), out var parsedThreads) ? parsedThreads : Environment.ProcessorCount;
        var incremental = !options.ContainsKey("no-incremental");
        var forceValue = GetOrDefault(options, "force", "").ToLowerInvariant();

        var config = new IndexingConfig
        {
            SourceRoot = Path.GetFullPath(root),
            LuceneIndexPath = Path.GetFullPath(lucene),
            VectorIndexPath = Path.GetFullPath(vec),
            GraphPath = Path.GetFullPath(graph),
            MetadataPath = Path.GetFullPath(meta),
            ModelPath = Path.GetFullPath(model),
            ApiKey = apiKey,
            ModelName = modelName,
            EmbeddingServerUrl = string.IsNullOrWhiteSpace(embeddingServerUrl) ? null : embeddingServerUrl,
            PythonExecutablePath = resolvedPythonScript is null ? null : pythonExec,
            PythonScriptPath = resolvedPythonScript,
            PythonBatchSize = pythonBatch,
            MaxDegreeOfParallelism = Math.Max(1, threads),
            Incremental = incremental,
            ForceRebuildLucene = forceValue.Contains("all") || forceValue.Contains("lucene"),
            ForceRebuildEmbeddings = forceValue.Contains("all") || forceValue.Contains("embed"),
            ForceRebuildGraph = forceValue.Contains("all") || forceValue.Contains("graph")
        };

        Console.WriteLine("[index] Configuration:");
        Console.WriteLine($"  root:    {config.SourceRoot}");
        Console.WriteLine($"  lucene:  {config.LuceneIndexPath}");
        Console.WriteLine($"  vectors: {config.VectorIndexPath}");
        Console.WriteLine($"  graph:   {config.GraphPath}");
        if (config.PythonScriptPath is not null)
        {
            Console.WriteLine($"  python:  {config.PythonExecutablePath} {config.PythonScriptPath}");
        }
        if (config.ForceFullRebuild)
        {
            Console.WriteLine("  force:   full rebuild enabled");
        }
        else
        {
            if (config.ForceRebuildLucene) Console.WriteLine("  force:   lucene rebuild enabled");
            if (config.ForceRebuildEmbeddings) Console.WriteLine("  force:   embedding rebuild enabled");
            if (config.ForceRebuildGraph) Console.WriteLine("  force:   graph rebuild enabled");
        }

        var pipeline = new IndexingPipeline(config);
        await pipeline.RunAsync();
        return 0;
    }

    private static async Task<int> RunRoughSearchAsync(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("Missing required option --query <text>.");
            return 1;
        }

        var lucene = GetOrDefault(options, "lucene", Path.Combine("index", "lucene"));
        var vec = GetOrDefault(options, "vec", Path.Combine("index", "vec"));
        var model = GetOrDefault(options, "model", Path.Combine("models", "e5-base-v2"));
        var apikey = GetOrDefault(options, "apikey", "");
        var pythonScriptCandidate = GetOrDefault(options, "python-script", Path.Combine("python", "embed.py"));
        var pythonExecCandidate = GetOrDefault(options, "python-exec", "python");
        var embeddingServerUrl = GetOrDefault(options, "embedding-server", "");

        var maxResults = int.TryParse(GetOrDefault(options, "max-results", "20"), out var parsedMax) ? Math.Max(1, parsedMax) : 20;
    var lexicalCandidates = int.TryParse(GetOrDefault(options, "lexical-k", "1000"), out var parsedLex) ? Math.Max(1, parsedLex) : 1000;
        var semanticDefault = RoughSearchConfig.DefaultSemanticCandidates.ToString(CultureInfo.InvariantCulture);
        var semanticCandidates = int.TryParse(GetOrDefault(options, "semantic-k", semanticDefault), out var parsedSem) ? Math.Max(1, parsedSem) : RoughSearchConfig.DefaultSemanticCandidates;
        var kind = options.TryGetValue("kind", out var kindValue) ? kindValue : null; // "csharp", "cs", "def", or null
        
        string? pythonScript = null;
        if (!string.IsNullOrWhiteSpace(pythonScriptCandidate) && File.Exists(pythonScriptCandidate))
        {
            pythonScript = Path.GetFullPath(pythonScriptCandidate);
        }

        var config = new RoughSearchConfig
        {
            LuceneIndexPath = Path.GetFullPath(lucene),
            VectorIndexPath = Path.GetFullPath(vec),
            MaxResults = maxResults,
            LexicalCandidates = lexicalCandidates,
            SemanticCandidates = semanticCandidates,
            ApiKey = apikey,
            ModelName = model,
            Kind = kind, // ← Pass kind parameter
            EmbeddingServerUrl = string.IsNullOrWhiteSpace(embeddingServerUrl) ? null : embeddingServerUrl,
            PythonScriptPath = pythonScript,
            PythonExecutablePath = pythonScript is null ? null : pythonExecCandidate,
            ModelPath = pythonScript is null ? null : Path.GetFullPath(model)
        };

        try
        {
            using var searcher = new RoughSearcher(config);
            var results = await searcher.SearchAsync(query).ConfigureAwait(false);
            if (results.Count == 0)
            {
                Console.WriteLine("No matches found.");
                return 0;
            }

            foreach (var result in results)
            {
                Console.WriteLine($"[{result.Source}] score={result.Score:F3} item={result.ItemId} symbol={result.SymbolId}");
                Console.WriteLine($"  path: {result.Path}");
                Console.WriteLine($"  lang: {result.Language}; kind: {result.SymbolKind}");
                if (!string.IsNullOrWhiteSpace(result.Signature))
                {
                    Console.WriteLine($"  signature: {result.Signature}");
                }
                if (result.Language != RimWorldCodeRag.Common.LanguageKind.CSharp)
                {
                    Console.WriteLine($"  preview: {result.Preview}");
                }
                Console.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rough-search failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Convert a legacy <c>vectors.jsonl</c> into the packed binary format (task 1.1) without
    /// re-running the embedding model.
    /// </summary>
    private static int RunPackVectors(string[] args)
    {
        var options = ParseOptions(args);
        var vec = GetOrDefault(options, "vec", Path.Combine("index", "vec"));
        var overwrite = options.ContainsKey("overwrite");

        if (!Directory.Exists(vec))
        {
            Console.Error.WriteLine($"Error: vector directory not found: {vec}");
            return 1;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = VectorPacker.Pack(vec, overwrite);
            watch.Stop();

            Console.WriteLine($"[pack] {result.Count:N0} vectors x {result.Dimensions}d");
            Console.WriteLine($"[pack] {VectorBinaryFormat.BinFileName}: {result.BinBytes / (1024.0 * 1024.0):N0} MB");
            Console.WriteLine($"[pack] {VectorBinaryFormat.MetaFileName}: {result.MetaBytes / (1024.0 * 1024.0):N1} MB");
            if (result.Skipped > 0)
            {
                Console.WriteLine($"[pack] skipped {result.Skipped:N0} unusable row(s)");
            }
            Console.WriteLine($"[pack] done in {watch.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"[pack] the legacy file is untouched; delete it manually once the packed index is verified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"pack-vectors failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Rebuild only the mod alias/vocabulary table (<c>index/mods.json</c>) from a source-tree
    /// snapshot, without touching Lucene, the vectors or the graph.
    /// </summary>
    private static int RunBuildModCatalog(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("root", out var root))
        {
            Console.Error.WriteLine("Missing required option --root <path>.");
            return 1;
        }

        var vec = GetOrDefault(options, "vec", Path.Combine("index", "vec"));
        var threads = int.TryParse(GetOrDefault(options, "threads", Environment.ProcessorCount.ToString()), out var parsedThreads)
            ? parsedThreads
            : Environment.ProcessorCount;

        try
        {
            var sourceRoot = Path.GetFullPath(root);
            var vectorPath = Path.GetFullPath(vec);
            var indexRoot = PathExclusionFilter.ResolveIndexRoot(vectorPath);

            // Dummy paths: the snapshot only reads SourceRoot and MaxDegreeOfParallelism.
            var config = new IndexingConfig
            {
                SourceRoot = sourceRoot,
                LuceneIndexPath = Path.Combine(indexRoot, "lucene"),
                VectorIndexPath = vectorPath,
                GraphPath = Path.Combine(indexRoot, "graph"),
                MetadataPath = Path.Combine(indexRoot, "meta"),
                ModelPath = string.Empty,
                MaxDegreeOfParallelism = Math.Max(1, threads),
                Incremental = true
            };

            PathExclusionFilter.EnsureFile(indexRoot);
            var exclusion = PathExclusionFilter.LoadForIndex(vectorPath);
            config = new IndexingConfig
            {
                SourceRoot = sourceRoot,
                LuceneIndexPath = config.LuceneIndexPath,
                VectorIndexPath = config.VectorIndexPath,
                GraphPath = config.GraphPath,
                MetadataPath = config.MetadataPath,
                ModelPath = config.ModelPath,
                MaxDegreeOfParallelism = config.MaxDegreeOfParallelism,
                Incremental = true,
                ExclusionFilter = exclusion
            };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var chunker = new Chunker(config, new MetadataStore(Path.Combine(config.MetadataPath, "mtimes.catalog.json")));
            var chunks = chunker.BuildFullSnapshot();
            var entries = ModCatalogBuilder.Build(sourceRoot, chunks);
            ModCatalog.Save(indexRoot, entries);
            watch.Stop();

            Console.WriteLine($"[mod-catalog] {chunks.Count:N0} chunks -> {entries.Count} mods in {watch.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"[mod-catalog] wrote {Path.Combine(indexRoot, ModCatalog.FileName)}");
            foreach (var entry in entries.OrderByDescending(e => e.ChunkCount).Take(15))
            {
                Console.WriteLine($"  {entry.ChunkCount,7:N0}  {entry.Dir}");
                Console.WriteLine($"           aliases: {string.Join(" | ", entry.Aliases)}");
                Console.WriteLine($"           namespaces: {string.Join(", ", entry.Namespaces)}");
                Console.WriteLine($"           defPrefixes: {string.Join(", ", entry.DefNamePrefixes)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"build-mod-catalog failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunGetUses(string[] args)
    {
        var options = ParseOptions(args);

        if (!options.TryGetValue("symbol", out var symbol))
        {
            Console.Error.WriteLine("Error: --symbol is required");
            return 1;
        }

        var kind = options.TryGetValue("kind", out var kindValue) ? kindValue : null;
        var graphPath = GetOrDefault(options, "graph", Path.Combine("index", "graph"));
        var page = int.TryParse(GetOrDefault(options, "page", "1"), out var p) ? p : 1;

        var config = new GraphQueryConfig
        {
            ItemId = symbol.Contains('@', StringComparison.Ordinal) ? symbol : null,
            SymbolId = symbol,
            Direction = GraphDirection.Uses,
            Kind = kind,
            Page = page
        };

        try
        {
            using var querier = new GraphQuerier(graphPath);
            var pagedResult = querier.Query(config);
            var totalPages = (int)Math.Ceiling((double)pagedResult.TotalCount / pagedResult.PageSize);

            if (pagedResult.Results.Count == 0)
            {
                Console.WriteLine($"[get-uses] {symbol} uses 0 symbols (kind={kind ?? "all"})");
                return 0;
            }

            Console.WriteLine($"[get-uses] {symbol} uses {pagedResult.TotalCount} symbol(s) (kind={kind ?? "all"}) - Page {pagedResult.Page}/{totalPages}");
            foreach (var result in pagedResult.Results)
            {
                Console.WriteLine($"  [{result.EdgeKind}] Score={result.Score:F4} (PR={result.PageRank:F4} Dups={result.DuplicateCount}) Item={result.ItemId} Symbol={result.SymbolId}");
            }

            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Hint: Run 'index' command first to build the graph.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"get-uses failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunGetUsedBy(string[] args)
    {
        var options = ParseOptions(args);

        if (!options.TryGetValue("symbol", out var symbol))
        {
            Console.Error.WriteLine("Error: --symbol is required");
            return 1;
        }

        var kind = options.TryGetValue("kind", out var kindValue) ? kindValue : null;
        var graphPath = GetOrDefault(options, "graph", Path.Combine("index", "graph"));
        var page = int.TryParse(GetOrDefault(options, "page", "1"), out var p) ? p : 1;

        var config = new GraphQueryConfig
        {
            ItemId = symbol.Contains('@', StringComparison.Ordinal) ? symbol : null,
            SymbolId = symbol,
            Direction = GraphDirection.UsedBy,
            Kind = kind,
            Page = page
        };

        try
        {
            using var querier = new GraphQuerier(graphPath);
            var pagedResult = querier.Query(config);
            var totalPages = (int)Math.Ceiling((double)pagedResult.TotalCount / pagedResult.PageSize);

            if (pagedResult.Results.Count == 0)
            {
                Console.WriteLine($"[get-used-by] {symbol} is used by 0 symbols (kind={kind ?? "all"})");
                return 0;
            }

            Console.WriteLine($"[get-used-by] {symbol} is used by {pagedResult.TotalCount} symbol(s) (kind={kind ?? "all"}) - Page {pagedResult.Page}/{totalPages}");
            foreach (var result in pagedResult.Results)
            {
                Console.WriteLine($"  [{result.EdgeKind}] Score={result.Score:F4} (PR={result.PageRank:F4} Dups={result.DuplicateCount}) Item={result.ItemId} Symbol={result.SymbolId}");
            }

            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Hint: Run 'index' command first to build the graph.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"get-used-by failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunGetItem(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            if (!options.TryGetValue("symbol", out var symbolId) || string.IsNullOrWhiteSpace(symbolId))
            {
                Console.Error.WriteLine("Error: --symbol is required.");
                Console.Error.WriteLine("Usage: get-item --symbol <item-id> [--max-lines <n>] [--lucene <path>]");
                return 1;
            }

            var luceneDir = GetOrDefault(options, "lucene", Path.Combine("index", "lucene"));
            var maxLines = 0;
            if (options.TryGetValue("max-lines", out var maxLinesStr) && int.TryParse(maxLinesStr, out var parsed))
            {
                maxLines = parsed;
            }

            if (!System.IO.Directory.Exists(luceneDir))
            {
                Console.Error.WriteLine($"Error: Lucene index not found at '{luceneDir}'");
                Console.Error.WriteLine("Hint: Run 'index' command first to build the index.");
                return 1;
            }

            using var retriever = new ExactRetriever(luceneDir);
            var result = retriever.GetItem(symbolId, maxLines);

            if (result == null)
            {
                Console.Error.WriteLine($"Error: Item not found: '{symbolId}'");
                Console.Error.WriteLine("Hint: Use 'rough-search' to find available item IDs.");
                return 1;
            }

            // Print metadata header
            Console.WriteLine($"Item: {result.ItemId}");
            Console.WriteLine($"Symbol: {result.SymbolId}");
            Console.WriteLine($"Type: {result.SymbolKind}");
            Console.WriteLine($"Language: {result.Language}");
            if (!string.IsNullOrWhiteSpace(result.Namespace))
                Console.WriteLine($"Namespace: {result.Namespace}");
            if (!string.IsNullOrWhiteSpace(result.ContainingType))
                Console.WriteLine($"Class: {result.ContainingType}");
            if (!string.IsNullOrWhiteSpace(result.Signature))
                Console.WriteLine($"Signature: {result.Signature}");
            if (!string.IsNullOrWhiteSpace(result.DefType))
                Console.WriteLine($"DefType: {result.DefType}");
            Console.WriteLine($"File: {result.Path}");
            Console.WriteLine($"Lines: {result.DisplayedLines}/{result.TotalLines}");

            if (result.Truncated)
            {
                Console.WriteLine($"Showing first {result.DisplayedLines} lines (use --max-lines 0 for full code)");
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(result.SourceCode);
            Console.WriteLine(new string('=', 60));

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"get-item failed: {ex.Message}");
            return 1;
        }
    }

    private static bool HelpRequested(IReadOnlyList<string> arguments)
    {
        return arguments.Any(a => a is "-h" or "--help" or "help");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  RimWorldCodeRag index --root <path> [--lucene <dir>] [--vec <dir>] [--graph <file>] [--meta <dir>] [--model <dir>] [--python-script <file>] [--python-exec <file>] [--python-batch <n>] [--embedding-server <url>] [--api-key <key>] [--model-name <name>] [--threads <n>] [--no-incremental] [--force]");
        Console.WriteLine("  RimWorldCodeRag rough-search --query <text> [--lucene <dir>] [--vec <dir>] [--kind <type>] [--model <dir>] [--python-script <file>] [--python-exec <file>] [--max-results <n>] [--lexical-k <n>] [--semantic-k <n>]");
        Console.WriteLine("  RimWorldCodeRag get-uses --symbol <id> [--kind <type>] [--graph <path>]");
        Console.WriteLine("  RimWorldCodeRag get-used-by --symbol <id> [--kind <type>] [--graph <path>]");
        Console.WriteLine("  RimWorldCodeRag get-item --symbol <id> [--max-lines <n>] [--lucene <dir>]");
        Console.WriteLine("  RimWorldCodeRag bench --queries <file.json> [--out <file.json>] [--compare <file.json>] [--label <name>] [--lucene <dir>] [--vec <dir>] [--embedding-server <url>] [--max-results <n>] [--warmup <n>] [--hybrid] [--fusion weighted|rrf] [--weights <lex>,<sem>] [--semantic-k <n>] [--diagnose] [--no-exclude] [--no-dedupe]");
        Console.WriteLine("  RimWorldCodeRag pack-vectors [--vec <dir>] [--overwrite]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  index             Build search index from source code and XML Defs");
        Console.WriteLine("  rough-search      Perform hybrid lexical + semantic search");
        Console.WriteLine("  get-uses          Query symbols that the given symbol uses/references");
        Console.WriteLine("  get-used-by       Query symbols that use/reference the given symbol");
        Console.WriteLine("  get-item          Retrieve full source code for a specific symbol");
        Console.WriteLine("  bench             Run the labeled retrieval benchmark and emit IR metrics (Recall/MRR/nDCG/CP)");
        Console.WriteLine("  pack-vectors      Repack a legacy vectors.jsonl into vectors.bin + vectors.meta.jsonl (no re-embedding)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --kind <type>     Filter by type: 'csharp'/'cs' (C# only), 'xml'/'def' (XML Defs only), or omit for all");
        Console.WriteLine("  --symbol <id>     Item ID or symbol ID. Prefer item IDs from rough-search/get-uses/get-used-by for precise retrieval");
        Console.WriteLine("  --max-lines <n>   Limit output to first N lines (0 = show all, default: 0)");
        Console.WriteLine("  --graph <path>    Path to graph files (default: 'index/graph')");
        Console.WriteLine("  --lucene <dir>    Path to Lucene index directory (default: 'index/lucene')");
        Console.WriteLine("  --embedding-server <url>  URL of persistent embedding server (e.g., 'http://127.0.0.1:5000') to avoid subprocess cold starts");
        Console.WriteLine("  --api-key <key>   API key for remote embedding service");
        Console.WriteLine("  --model-name <name> Model name for remote embedding service");
    }
}
