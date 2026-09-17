namespace RimWorldCodeRag.McpServer.Tools;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldCodeRag.Retrieval;


//rough_search MCP工具暴露接口
public sealed class RoughSearchTool : ITool, IDisposable
{
    private readonly Lazy<RoughSearcher> _searcher;
    private readonly string _indexRoot;
    private readonly string? _embeddingServerUrl;
    private readonly RoughSearchConfig _defaultConfig;
    private bool _disposed;

    public string Name => "rough_search";

    public string Description =>
        @"Search RimWorld source code (C#) and XML Def definitions. Returns matching indexed items with metadata — use get_item afterwards to read full source code.

WORKFLOW:
1. Search with keywords (class names, method names, Def names, or natural language)
2. Review the returned itemId and symbolId fields
3. Use get_item with the exact itemId to read full source code
4. Use get_uses/get_used_by with itemId for precise dependency exploration

QUERY TIPS:
- For C# code: use class/method names like 'Need_Food', 'JobDriver_Mine', 'CompPowerTrader'
- For XML Defs: use def names like 'Steel', 'MealSimple', 'Gun_BoltActionRifle'
- For concepts: use natural language like 'pawn hunger system', 'crafting recipe'
- Use kind='cs' to search only C# code, kind='xml' for only XML Defs
- Keep queries short (2-4 keywords work best). Avoid full sentences.

COMMON MODDING PATTERNS:
- To understand a system: search for the main class, then get_uses to see what it depends on
- To extend behavior: search for the base class, then get_used_by to see existing implementations
- To find XML structure: search with kind='xml' and the DefType name (e.g. 'ThingDef weapon')
- XML results include the full Def XML in the preview field";


    public RoughSearchTool(
        string indexRoot, 
        string? embeddingServerUrl = null,
        string? apiKey=null,
        string? modelName=null)
    {
        _indexRoot = indexRoot;
        _embeddingServerUrl = embeddingServerUrl;
        
        _defaultConfig = new RoughSearchConfig
        {
            LuceneIndexPath = Path.Combine(_indexRoot, "lucene"),
            VectorIndexPath = Path.Combine(_indexRoot, "vec"),
            EmbeddingServerUrl = _embeddingServerUrl,
            ApiKey=apiKey,
            ModelName=modelName,
            MaxResults = 20,

            // Fused retrieval (task 1.3). Weights measured on tests/retrieval-baseline.json:
            // 0.5/0.5 is WORSE than semantic-only (MRR 0.3853 -> 0.3154) because BM25 candidates
            // dilute good semantic hits; 0.3/0.7 beats semantic-only (MRR 0.4042, nDCG@10 0.4280,
            // Hits@1 14/40 vs 12/40). Recall@10 is unchanged either way — recall is limited by
            // candidate generation, not by ranking.
            UseSemanticScoringOnly = false,
            Fusion = FusionMode.WeightedSum,
            LexicalWeight = 0.3,
            SemanticWeight = 0.7,

            // Identifier-aware exact-symbol bonus (task 1.3). Needed once Qwen3-Embedding replaced e5:
            // the new model finds far more of the hard items (absent 12 -> 3) but ranks bare
            // identifiers worse, and raising the lexical weight instead collapses the
            // natural-language queries. Measured on tests/retrieval-baseline.json:
            //   e5   fused 0.3/0.7                  Recall@10 0.5000  MRR 0.4042  Hits@1 14/40
            //   qwen3 fused 0.3/0.7 + sk100         Recall@10 0.5750  MRR 0.4573  Hits@1 15/40
            SymbolMatchBoost = 0.6
        };

        _searcher = new Lazy<RoughSearcher>(() =>
        {
            Console.Error.WriteLine("[RoughSearchTool] Initializing searcher (this may take a few seconds)...");
            var searcher = new RoughSearcher(_defaultConfig);
            Console.Error.WriteLine("[RoughSearchTool] Searcher initialized successfully.");
            return searcher;
        });
    }

    public JsonElement GetInputSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "Natural language search query. Examples: 'pawn hunger system', 'crafting buildings', 'weapon damage calculation', 'colonist needs'",
                    minLength = 1,
                    maxLength = 500
                },
                kind = new
                {
                    type = "string",
                    @enum = new[] { "csharp", "cs", "def", "xml" },
                    description = "Optional filter: 'csharp' or 'cs' for C# code only, 'def' or 'xml' for XML definitions only. Omit to search everything."
                },
                max_results = new
                {
                    type = "integer",
                    @default = 20,
                    minimum = 1,
                    maximum = 100,
                    description = "Maximum number of results to return. Recommended range: 10-30."
                }
            },
            required = new[] { "query" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    public async Task<object> ExecuteAsync(JsonElement arguments)
    {
        // 解析参数
        if (!arguments.TryGetProperty("query", out var queryElement))
        {
            throw new ArgumentException("参数 'query' 是必需的");
        }

        // Debug: 打印接收到的原始参数，便于排查客户端传参问题
        try
        {
            Console.Error.WriteLine($"[RoughSearchTool] Raw arguments: {arguments.GetRawText()}");
        }
        catch
        {
            // 忽略任何日志写入错误
        }

        var query = queryElement.GetString();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("参数 'query' 不能为空");
        }

        var kind = arguments.TryGetProperty("kind", out var kindElem)
            ? kindElem.GetString()
            : null;

        var maxResults = arguments.TryGetProperty("max_results", out var maxElem)
            ? maxElem.GetInt32()
            : _defaultConfig.MaxResults;

        // 验证参数
        if (maxResults < 1 || maxResults > 100)
        {
            throw new ArgumentException("max_results 必须在 1-100 之间");
        }

        var startTime = DateTime.Now;

        // Per-request overrides: the searcher owns the loaded index (1.6 GB) and is reused for
        // every call, including concurrent ones with different kind/max. Rebuilding it per
        // parameter change used to re-parse the vector file and cost ~14 s per query.
        var results = await _searcher.Value.SearchAsync(query, new RoughSearchOptions
        {
            Kind = kind,
            MaxResults = maxResults
        });

        var elapsed = DateTime.Now - startTime;

        // 转换为 MCP响应格式
        var response = new
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
                title = ((r.Signature ?? r.Preview ?? r.SymbolId).Split('\n')[0] ?? string.Empty).Trim(),
                score = Math.Round(r.Score, 4),
                spanStart = r.SpanStart,
                spanEnd = r.SpanEnd,
                preview = r.Language == RimWorldCodeRag.Common.LanguageKind.CSharp ? string.Empty : r.Preview
            }).ToArray(),
            totalFound = results.Count,
            queryTime = $"{elapsed.TotalSeconds:F2}s"
        };

        return response;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_searcher.IsValueCreated)
        {
            (_searcher.Value as IDisposable)?.Dispose();
        }
        _disposed = true;
    }
}
