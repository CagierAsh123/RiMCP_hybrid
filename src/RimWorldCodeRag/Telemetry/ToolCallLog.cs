using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldCodeRag.Telemetry;

/// <summary>
/// Append-only JSONL log of MCP tool calls (plan task 0.5, layer L3).
///
/// <para>
/// The deterministic benchmark (L1) says whether retrieval *can* work; this says whether it works
/// in practice: latency percentiles, the zero-result rate, which tools get used, and how often a
/// <c>rough_search</c> is followed by a <c>get_item</c> for something the search actually returned
/// (the closest available proxy for "the agent found what it needed").
/// </para>
///
/// <para>
/// Telemetry must never break a tool call: every write is wrapped and failures are reported once to
/// stderr, then the logger disables itself.
/// </para>
/// </summary>
public sealed class ToolCallLog
{
    public const string FileName = "mcp-tool-calls.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly object _lock = new();
    private bool _disabled;

    private ToolCallLog(string path)
    {
        _path = path;
    }

    public string Path => _path;

    /// <summary>
    /// Build a logger for the index root, or <c>null</c> when telemetry is switched off.
    /// Disabled by <c>RIMWORLD_TELEMETRY=0</c>; the location can be overridden with
    /// <c>RIMWORLD_TELEMETRY_PATH</c>.
    /// </summary>
    public static ToolCallLog? Create(string indexRoot)
    {
        var toggle = Environment.GetEnvironmentVariable("RIMWORLD_TELEMETRY");
        if (toggle is "0" or "false" or "off")
        {
            Console.Error.WriteLine("[telemetry] disabled via RIMWORLD_TELEMETRY");
            return null;
        }

        var overridePath = Environment.GetEnvironmentVariable("RIMWORLD_TELEMETRY_PATH");
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? System.IO.Path.Combine(indexRoot, "logs", FileName)
            : overridePath;

        return new ToolCallLog(path);
    }

    public void Record(ToolCallRecord record)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(record, JsonOptions);
            lock (_lock)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_path, json + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _disabled = true;
            Console.Error.WriteLine($"[telemetry] disabled after a write failure: {ex.Message}");
        }
    }

    /// <summary>Project the tool arguments down to the few fields worth keeping.</summary>
    public static (string? Query, string? Symbol, string? Kind, int? MaxResults, int? MaxLines) ProjectArguments(string tool, JsonElement arguments)
    {
        string? GetString(string name)
            => arguments.ValueKind == JsonValueKind.Object &&
               arguments.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        int? GetInt(string name)
            => arguments.ValueKind == JsonValueKind.Object &&
               arguments.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var parsed)
                ? parsed
                : null;

        return (
            GetString("query"),
            GetString("symbol"),
            GetString("kind"),
            GetInt("max_results"),
            GetInt("max_lines"));
    }

    /// <summary>
    /// Pull a result count out of a tool result. <c>rough_search</c> reports <c>totalFound</c>,
    /// <c>get_uses</c>/<c>get_used_by</c> report <c>totalCount</c>, <c>get_item</c> reports a single
    /// item (count 1) or an <c>error</c> marker (count 0).
    /// </summary>
    public static int? ResultCountOf(JsonElement result, string tool)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "totalFound", "totalCount", "count" })
        {
            if (result.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }

        if (result.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            return results.GetArrayLength();
        }

        if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            return 0;
        }

        return tool switch
        {
            "get_item" => 1,
            _ => null
        };
    }
}

/// <summary>One logged tool call. Field names are kept short because the file grows unbounded.</summary>
public sealed record ToolCallRecord
{
    [JsonPropertyName("ts")]
    public string Timestamp { get; init; } = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("ms")]
    public required double LatencyMs { get; init; }

    [JsonPropertyName("n")]
    public int? ResultCount { get; init; }

    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("max")]
    public int? MaxResults { get; init; }

    [JsonPropertyName("lines")]
    public int? MaxLines { get; init; }

    [JsonPropertyName("err")]
    public string? Error { get; init; }
}
