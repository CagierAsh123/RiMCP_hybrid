namespace RimWorldCodeRag.McpServer.Tools;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldCodeRag.Retrieval;

//get_item MCP工具暴露接口
public sealed class GetItemTool : ITool, IDisposable
{
    private readonly Lazy<ExactRetriever> _retriever;
    private readonly string _indexRoot;
    private bool _disposed;

    public string Name => "get_item";

    public string Description =>
        @"Retrieve complete source code for a specific symbol by its exact ID. Use this after rough_search, get_uses, or get_used_by to read the actual code.

INPUT: The symbolId field from search results (copy it exactly).
- C# symbols: 'RimWorld.Need_Food', 'Verse.Thing.public virtual void Tick()'
- XML Defs: 'xml:ThingDef:Steel', 'xml:RecipeDef:Make_ComponentIndustrial'

OUTPUT: Full source code, file path, namespace, class hierarchy, and metadata.

If the symbol is not found, use rough_search to find the correct symbolId first.";

    public GetItemTool(string indexRoot)
    {
        _indexRoot = indexRoot;
        _retriever = new Lazy<ExactRetriever>(() =>
        {
            Console.Error.WriteLine("[GetItemTool] Loading Lucene index...");
            var lucenePath = Path.Combine(_indexRoot, "lucene");
            var retriever = new ExactRetriever(lucenePath);
            Console.Error.WriteLine("[GetItemTool] Lucene index loaded successfully.");
            return retriever;
        });
    }

    public JsonElement GetInputSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                symbol = new
                {
                    type = "string",
                    description = "Symbol ID to retrieve. Examples: 'RimWorld.Pawn' (C# class), 'RimWorld.Thing.Tick' (C# method), 'xml:Steel' (XML definition)",
                    pattern = "^([A-Za-z0-9_\\.]+|xml:[A-Za-z0-9_]+)$"
                },
                max_lines = new
                {
                    type = "integer",
                    @default = 0,
                    minimum = 0,
                    description = "Maximum lines to display. 0=show all lines. Use to limit output for very large code blocks."
                }
            },
            required = new[] { "symbol" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    public async Task<object> ExecuteAsync(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("symbol", out var symbolElement))
        {
            throw new ArgumentException("参数 'symbol' 是必需的");
        }

        var symbol = symbolElement.GetString();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("参数 'symbol' 不能为空");
        }

        var maxLines = arguments.TryGetProperty("max_lines", out var maxElem)
            ? maxElem.GetInt32()
            : 0;

        if (maxLines < 0)
        {
            throw new ArgumentException("max_lines 不能为负数");
        }

        var result = await Task.Run(() => _retriever.Value.GetItem(symbol, maxLines));

        if (result == null)
        {
            // Instead of throwing, return a helpful response that guides the AI
            return new
            {
                error = true,
                symbolId = symbol,
                message = $"Symbol '{symbol}' not found in the index. This usually means the symbolId is incorrect or incomplete.",
                suggestions = new[]
                {
                    $"Use rough_search with query '{symbol.Replace(".", " ").Replace("xml:", "")}' to find the correct symbolId.",
                    "Copy the exact symbolId from rough_search results — do not modify it.",
                    "C# symbols look like: 'RimWorld.Need_Food' or 'Verse.Thing.public virtual void Tick()'",
                    "XML symbols look like: 'xml:ThingDef:Steel' or 'xml:RecipeDef:Make_ComponentIndustrial'"
                }
            };
        }

        // 转换为MCP响应格式
        var response = new
        {
            symbolId = result.SymbolId,
            language = result.Language.ToString().ToLowerInvariant(),
            symbolKind = result.SymbolKind.ToString(),
            path = result.Path,
            metadata = new
            {
                @namespace = result.Namespace,
                containingType = result.ContainingType,
                signature = result.Signature,
                defType = result.DefType,
                totalLines = result.TotalLines,
                displayedLines = result.DisplayedLines,
                truncated = result.Truncated
            },
            sourceCode = result.SourceCode
        };

        return response;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_retriever.IsValueCreated)
        {
            (_retriever.Value as IDisposable)?.Dispose();
        }
        _disposed = true;
    }
}
