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
        @"Retrieve complete source code for a specific indexed item. Use this after rough_search, get_uses, or get_used_by to read the actual code.

INPUT: Prefer the itemId field from search results. rough_search also returns symbolId for display.
- itemId examples: 'RimWorld.Pawn@A1B2C3', 'xml:ThingDef:Steel@4D5E6F'
- symbolId examples: 'RimWorld.Need_Food', 'xml:ThingDef:Steel'

OUTPUT: Full source code, file path, namespace, class hierarchy, and metadata.

If the item is not found, use rough_search to find the correct itemId first.";


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
                    description = "Item ID to retrieve. Prefer the itemId field returned by rough_search/get_uses/get_used_by. Examples: 'RimWorld.Pawn@A1B2C3', 'xml:ThingDef:Steel@4D5E6F'",
                    minLength = 1
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
                itemId = symbol,
                message = $"Item '{symbol}' not found in the index. This usually means the itemId is incorrect or stale.",
                suggestions = new[]
                {
                    "Use rough_search first and copy the exact itemId field from its results.",
                    "Do not pass the display symbolId when multiple mods share the same symbol.",
                    "C# itemIds look like: 'RimWorld.Need_Food@A1B2C3'",
                    "XML itemIds look like: 'xml:ThingDef:Steel@4D5E6F'"
                }
            };
        }

        // 转换为MCP响应格式
        var response = new
        {
            itemId = result.ItemId,
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
