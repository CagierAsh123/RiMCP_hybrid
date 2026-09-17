namespace RimWorldCodeRag.McpServer.Tools;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Search;

/// <summary>
/// <c>grep</c> MCP tool (plan task 3.1): literal / regex text scan over the decompiled source tree.
///
/// <para>
/// Complements <c>rough_search</c>: embeddings handle meaning, this handles exact strings —
/// a literal <c>"rjw.JobDriver_Sex"</c> reference, every <c>[HarmonyPatch]</c> attribute, an XML
/// attribute value. Same exclusion rules as the index, so it can never surface an excluded mod.
/// </para>
/// </summary>
public sealed class GrepTool : ITool
{
    private readonly string _indexRoot;
    private readonly Lazy<PathExclusionFilter> _exclusion;

    public string Name => "grep";

    public string Description =>
        @"Exact text search over the RimWorld source tree (C# and XML). Zero GPU, no embedding — use it whenever you know the literal string.

WHEN TO USE THIS INSTEAD OF rough_search:
- You know an exact identifier, string literal, attribute name or value
- You want every occurrence (rough_search returns ranked candidates, not a complete list)
- You are checking whether something is referenced at all

WHEN TO USE rough_search INSTEAD:
- You are describing a mechanism in natural language and do not know the identifier
- You want the best few places to start reading

OUTPUT: path:line:column plus the matching line and surrounding context lines.
- regex=false (default) treats the pattern as literal text — no escaping needed
- regex=true uses .NET regex syntax
- Scope with path='Vehicle Framework' to search a single mod, or glob='*.xml' to search only Defs
- Results are capped; if truncated=true, narrow the pattern or scope it with path/glob";

    public GrepTool(string indexRoot)
    {
        _indexRoot = indexRoot;
        _exclusion = new Lazy<PathExclusionFilter>(() => PathExclusionFilter.Load(indexRoot));
    }

    public JsonElement GetInputSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                pattern = new
                {
                    type = "string",
                    description = "Text to find. Literal by default; a .NET regex when regex=true.",
                    minLength = 1,
                    maxLength = 500
                },
                regex = new
                {
                    type = "boolean",
                    @default = false,
                    description = "Treat pattern as a .NET regex instead of literal text."
                },
                glob = new
                {
                    type = "string",
                    description = "File-name glob, e.g. '*.cs' or '*.xml'. Omit to search both."
                },
                path = new
                {
                    type = "string",
                    description = "Substring of the file path — the cheap way to scope to one mod, e.g. 'Vehicle Framework'."
                },
                max_results = new
                {
                    type = "integer",
                    @default = 50,
                    minimum = 1,
                    maximum = 500,
                    description = "Maximum number of matches to return."
                },
                context_lines = new
                {
                    type = "integer",
                    @default = 2,
                    minimum = 0,
                    maximum = 10,
                    description = "Lines of context before and after each match."
                },
                case_sensitive = new
                {
                    type = "boolean",
                    @default = false,
                    description = "Match case-sensitively. Default is case-insensitive."
                }
            },
            required = new[] { "pattern" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    public Task<object> ExecuteAsync(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("pattern", out var patternElement))
        {
            throw new ArgumentException("参数 'pattern' 是必需的");
        }

        var pattern = patternElement.GetString();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("参数 'pattern' 不能为空");
        }

        var glob = arguments.TryGetProperty("glob", out var globElement) ? globElement.GetString() : null;
        var pathFilter = arguments.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var maxResults = arguments.TryGetProperty("max_results", out var maxElement) ? maxElement.GetInt32() : 50;
        var contextLines = arguments.TryGetProperty("context_lines", out var contextElement) ? contextElement.GetInt32() : 2;
        var regex = arguments.TryGetProperty("regex", out var regexElement) && regexElement.ValueKind == JsonValueKind.True;
        var caseSensitive = arguments.TryGetProperty("case_sensitive", out var caseElement) && caseElement.ValueKind == JsonValueKind.True;

        if (maxResults is < 1 or > 500)
        {
            throw new ArgumentException("max_results 必须在 1-500 之间");
        }

        if (contextLines is < 0 or > 10)
        {
            throw new ArgumentException("context_lines 必须在 0-10 之间");
        }

        var sourceRoot = SourceRootHint.Resolve(_indexRoot);
        var result = GrepSearcher.Search(new GrepOptions
        {
            SourceRoot = sourceRoot,
            Pattern = pattern,
            IsRegex = regex,
            IgnoreCase = !caseSensitive,
            Glob = glob,
            PathFilter = pathFilter,
            MaxResults = maxResults,
            ContextLines = contextLines,
            Exclusion = _exclusion.Value
        });

        if (result.Error is not null)
        {
            return Task.FromResult<object>(new
            {
                error = true,
                message = result.Error,
                suggestions = new[]
                {
                    "Check the pattern; set regex=true only if you actually wrote a regex.",
                    "Use path='<mod name>' to narrow the search if it is too slow."
                }
            });
        }

        if (result.Matches.Count == 0)
        {
            return Task.FromResult<object>(new
            {
                error = false,
                sourceRoot,
                pattern,
                matches = Array.Empty<object>(),
                totalFound = 0,
                filesScanned = result.FilesScanned,
                message = "No literal match. If you were guessing at an identifier, try rough_search with a natural-language description instead.",
                elapsedMs = Math.Round(result.ElapsedMs, 1)
            });
        }

        var matches = result.Matches.Select(m => new
        {
            path = m.Path,
            line = m.Line,
            column = m.Column,
            text = m.Text,
            context = m.Before.Concat(new[] { m.Text }).Concat(m.After).ToArray(),
            contextStartLine = Math.Max(1, m.Line - m.Before.Count)
        }).ToArray();

        return Task.FromResult<object>(new
        {
            error = false,
            sourceRoot,
            pattern,
            regex,
            matches,
            totalFound = matches.Length,
            filesScanned = result.FilesScanned,
            filesMatched = result.FilesMatched,
            truncated = result.Truncated,
            elapsedMs = Math.Round(result.ElapsedMs, 1)
        });
    }

    public void Dispose()
    {
    }
}
