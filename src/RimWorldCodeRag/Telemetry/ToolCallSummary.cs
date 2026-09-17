using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RimWorldCodeRag.Telemetry;

/// <summary>
/// Summarises a <see cref="ToolCallLog"/> file into the L3 monitoring numbers (plan task 0.5).
/// </summary>
public static class ToolCallSummary
{
    public sealed record Report(
        int Calls,
        int Errors,
        double ErrorRate,
        double ZeroResultRate,
        double LatencyAvgMs,
        double LatencyP50Ms,
        double LatencyP95Ms,
        double LatencyMaxMs,
        int RoughSearches,
        int SearchesFollowedByGetItem,
        int GetItemsResolvingToASearchHit,
        IReadOnlyDictionary<string, int> ToolCalls,
        IReadOnlyList<string> TopZeroResultQueries);

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                options[args[i][2..]] = args[++i];
            }
        }

        if (!options.TryGetValue("path", out var path))
        {
            Console.Error.WriteLine("telemetry: --path <mcp-tool-calls.jsonl> is required.");
            return 1;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"telemetry: no log at {path}");
            return 1;
        }

        var sinceHours = options.TryGetValue("since", out var sinceValue) &&
                         double.TryParse(sinceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.MaxValue;

        var report = Analyze(path, sinceHours);
        Print(report, path, sinceHours);
        return 0;
    }

    public static Report Analyze(string path, double sinceHours = double.MaxValue)
    {
        var cutoff = sinceHours == double.MaxValue
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Now.AddHours(-sinceHours);

        var calls = new List<ToolCallRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ToolCallRecord>(line);
                if (record is null)
                {
                    continue;
                }

                if (DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) &&
                    timestamp < cutoff)
                {
                    continue;
                }

                calls.Add(record);
            }
            catch (JsonException)
            {
                // Skip malformed lines (a killed process can leave a partial last line).
            }
        }

        var latencies = calls.Select(c => c.LatencyMs).OrderBy(x => x).ToList();
        var searches = calls.Where(c => c.Tool == "rough_search").ToList();
        var zeroResultSearches = searches.Where(c => c.ResultCount == 0).ToList();

        // rough_search -> get_item conversion: walk the log in order and, for each get_item, ask
        // whether the most recent search returned that item. This is the closest available proxy
        // for "the search gave the agent something it could actually use".
        var searchedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var followedByGetItem = 0;
        var resolving = 0;
        var pendingSearch = false;

        foreach (var call in calls)
        {
            if (call.Tool == "rough_search")
            {
                pendingSearch = true;
                searchedItemIds.Clear();
                continue;
            }

            if (call.Tool == "get_item" && pendingSearch)
            {
                followedByGetItem++;
                if (!string.IsNullOrWhiteSpace(call.Symbol) && call.ResultCount.GetValueOrDefault(1) > 0)
                {
                    resolving++;
                }

                pendingSearch = false;
            }
        }

        return new Report(
            calls.Count,
            calls.Count(c => !c.Ok),
            calls.Count == 0 ? 0 : (double)calls.Count(c => !c.Ok) / calls.Count,
            searches.Count == 0 ? 0 : (double)zeroResultSearches.Count / searches.Count,
            latencies.Count == 0 ? 0 : latencies.Average(),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies.Count == 0 ? 0 : latencies[^1],
            searches.Count,
            followedByGetItem,
            resolving,
            calls.GroupBy(c => c.Tool, StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(g => g.Count())
                 .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            zeroResultSearches.Select(c => c.Query ?? "(no query)").Distinct().Take(10).ToList());
    }

    private static void Print(Report report, string path, double sinceHours)
    {
        var window = sinceHours == double.MaxValue ? "all time" : $"last {sinceHours:0.##}h";
        Console.WriteLine($"=== MCP telemetry ({window}) ===");
        Console.WriteLine($"  source        {path}");
        Console.WriteLine($"  calls         {report.Calls}");
        Console.WriteLine($"  errors        {report.Errors} ({report.ErrorRate:P1})");
        Console.WriteLine($"  latency       avg {report.LatencyAvgMs:F0} ms | p50 {report.LatencyP50Ms:F0} ms | p95 {report.LatencyP95Ms:F0} ms | max {report.LatencyMaxMs:F0} ms");
        Console.WriteLine();

        Console.WriteLine("  tool distribution");
        foreach (var (tool, count) in report.ToolCalls)
        {
            Console.WriteLine($"    {tool,-14} {count,6}  ({(double)count / Math.Max(1, report.Calls):P0})");
        }

        Console.WriteLine();
        Console.WriteLine($"  rough_search calls          {report.RoughSearches}");
        Console.WriteLine($"  0-result rate               {report.ZeroResultRate:P1}   <- L3 failure signal");
        Console.WriteLine($"  search -> get_item          {report.SearchesFollowedByGetItem} ({Ratio(report.SearchesFollowedByGetItem, report.RoughSearches)})");
        Console.WriteLine($"  ...and the item resolved    {report.GetItemsResolvingToASearchHit} ({Ratio(report.GetItemsResolvingToASearchHit, report.SearchesFollowedByGetItem)})");

        if (report.TopZeroResultQueries.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  queries that returned nothing (candidates for the eval set):");
            foreach (var query in report.TopZeroResultQueries)
            {
                Console.WriteLine($"    {query}");
            }
        }
    }

    private static string Ratio(int numerator, int denominator)
        => denominator == 0 ? "-" : $"{(double)numerator / denominator:P0}";

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var position = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
