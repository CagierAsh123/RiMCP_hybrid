using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Evaluation;

/// <summary>
/// Deterministic retrieval-only evaluation harness (plan stage 0, layer L1).
///
/// <para>
/// It runs a labeled query set through <see cref="RoughSearcher"/> and computes IR metrics that
/// need no LLM: Recall@k, MRR, nDCG@k and Context Precision@k (the retrieval-side half of RAGAS),
/// plus latency percentiles and the zero-result rate.
/// </para>
///
/// <para>
/// Expected items are matched against <c>itemId</c> or <c>symbolId</c>. A bare symbol name also
/// matches as a suffix of the full symbol id (<c>Need_Food</c> matches <c>RimWorld.Need_Food</c>).
/// </para>
/// </summary>
public static class RetrievalBenchmark
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed class BenchQuery
    {
        public string Id { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public string? Category { get; set; }
        public string? Note { get; set; }
        public List<string> Expected { get; set; } = new();
        public bool Holdout { get; set; }
    }

    private sealed record QueryOutcome(
        BenchQuery Source,
        List<ResultRow> Results,
        List<int> HitRanks,
        List<int> RelaxedHitRanks,
        List<string> MissingExpected,
        List<ExpectedProvenance> Provenance,
        double LatencyMs);

    private sealed record ResultRow(int Rank, string ItemId, string SymbolId, double Score, string Path, string? Signature);

    /// <summary>Per-expected-item provenance: which leg (if any) carried it, and where it landed.</summary>
    private sealed record ExpectedProvenance(string Expected, int? LexicalRank, int? SemanticRank, int? FusedRank)
    {
        public string Leg => (LexicalRank, SemanticRank) switch
        {
            (not null, not null) => "both",
            (not null, null) => "lexical-only",
            (null, not null) => "semantic-only",
            _ => "absent"
        };
    }

    public static async Task<int> RunAsync(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("queries", out var queriesPath) || !File.Exists(queriesPath))
        {
            Console.Error.WriteLine("bench: --queries <file.json> is required and must exist.");
            return 1;
        }

        var lucene = GetOrDefault(options, "lucene", Path.Combine("index", "lucene"));
        var vec = GetOrDefault(options, "vec", Path.Combine("index", "vec"));
        var embeddingServerUrl = GetOrDefault(options, "embedding-server", "http://127.0.0.1:5000");
        var maxResults = ParseInt(options, "max-results", 20);
        var lexicalCandidates = ParseInt(options, "lexical-k", 1000);
        var semanticCandidates = ParseInt(options, "semantic-k", RoughSearchConfig.DefaultSemanticCandidates);
        var label = GetOrDefault(options, "label", "unlabeled");
        var outPath = options.TryGetValue("out", out var outValue) ? outValue : null;
        var comparePath = options.TryGetValue("compare", out var compareValue) ? compareValue : null;
        var warmup = ParseInt(options, "warmup", 1);
        var useHybrid = options.ContainsKey("hybrid");
        var noExclude = options.ContainsKey("no-exclude");
        var noDedupe = options.ContainsKey("no-dedupe");
        var diagnose = options.ContainsKey("diagnose");
        var fusion = GetOrDefault(options, "fusion", "weighted").Equals("rrf", StringComparison.OrdinalIgnoreCase)
            ? FusionMode.Rrf
            : FusionMode.WeightedSum;
        var (lexicalWeight, semanticWeight) = ParseWeights(GetOrDefault(options, "weights", "0.5,0.5"));
        var modExpansion = ParseModExpansion(GetOrDefault(options, "mod-expand", "none"));
        var modPathBoost = ParseDouble(options, "mod-boost", 0.0);

        var queries = LoadQueries(queriesPath);
        if (queries.Count == 0)
        {
            Console.Error.WriteLine("bench: query file is empty.");
            return 1;
        }

        var exclusion = noExclude ? PathExclusionFilter.Empty : PathExclusionFilter.LoadForIndex(vec);
        Console.WriteLine($"[bench] {queries.Count} queries | lucene={lucene} | {exclusion.Describe()} | dedupe={!noDedupe} | hybrid={useHybrid}");

        // Label hygiene: an `expected` id that is not in the index would silently deflate every
        // metric, so verify them up front and report separately from retrieval misses.
        var labelProblems = VerifyLabels(queries, lucene);
        if (labelProblems.Count > 0)
        {
            Console.WriteLine($"[bench] ⚠ {labelProblems.Count} expected value(s) do not exist in the index — fix the labels:");
            foreach (var problem in labelProblems)
            {
                Console.WriteLine($"        {problem}");
            }
        }

        // One searcher for the whole run: `kind` is a per-request override, so the index is
        // loaded exactly once (this is also a direct check that task 1.2 holds).
        var config = new RoughSearchConfig
        {
            LuceneIndexPath = Path.GetFullPath(lucene),
            VectorIndexPath = Path.GetFullPath(vec),
            EmbeddingServerUrl = string.IsNullOrWhiteSpace(embeddingServerUrl) ? null : embeddingServerUrl,
            MaxResults = maxResults,
            LexicalCandidates = lexicalCandidates,
            SemanticCandidates = semanticCandidates,
            ExclusionFilter = exclusion,
            DedupeBySymbolId = !noDedupe,
            UseSemanticScoringOnly = !useHybrid,
            LexicalWeight = lexicalWeight,
            SemanticWeight = semanticWeight,
            Fusion = fusion,
            ModExpansion = modExpansion,
            ModPathBoost = modPathBoost
        };

        var outcomes = new List<QueryOutcome>();

        var loadWatch = Stopwatch.StartNew();
        RoughSearcher searcher;
        try
        {
            searcher = new RoughSearcher(config);
        }
        catch (Exception ex)
        {
            // Report the cause without a stack trace: the common failures here (missing index,
            // unreadable vector file) have actionable messages of their own.
            Console.Error.WriteLine($"[bench] could not load the index: {ex.Message}");
            return 1;
        }

        loadWatch.Stop();
        using (searcher)
        {
        Console.WriteLine($"[bench] index loaded once in {loadWatch.Elapsed.TotalSeconds:F2}s (kind is now a per-request override)");

        for (var i = 0; i < warmup; i++)
        {
            await searcher.SearchAsync("warmup CompPowerTrader").ConfigureAwait(false);
        }

        foreach (var query in queries)
        {
            var requestOptions = new RoughSearchOptions
            {
                Kind = query.Kind,
                MaxResults = maxResults
            };

            var watch = Stopwatch.StartNew();
            IReadOnlyList<RoughSearchResult> results;
            SearchDiagnostics? diagnostics = null;
            try
            {
                if (diagnose)
                {
                    diagnostics = await searcher.SearchWithDiagnosticsAsync(query.Query, requestOptions).ConfigureAwait(false);
                    results = diagnostics.Results;
                }
                else
                {
                    results = await searcher.SearchAsync(query.Query, requestOptions).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bench] query '{query.Id}' failed: {ex.Message}");
                results = Array.Empty<RoughSearchResult>();
            }

            watch.Stop();

            var rows = results
                .Take(maxResults)
                .Select((r, index) => new ResultRow(index + 1, r.ItemId, r.SymbolId, r.Score, r.Path, r.Signature))
                .ToList();

            var hitRanks = new List<int>();
            var relaxedHitRanks = new List<int>();
            var missing = new List<string>();
            var provenance = new List<ExpectedProvenance>();
            foreach (var expected in query.Expected)
            {
                var rank = rows.FindIndex(row => Matches(row, expected));
                if (rank < 0)
                {
                    missing.Add(expected);
                }
                else
                {
                    hitRanks.Add(rank + 1);
                }

                var relaxedRank = rows.FindIndex(row => Matches(row, expected) || IsMemberOf(row, expected));
                if (relaxedRank >= 0)
                {
                    relaxedHitRanks.Add(relaxedRank + 1);
                }

                if (diagnostics is not null)
                {
                    provenance.Add(new ExpectedProvenance(
                        expected,
                        RankIn(diagnostics.Lexical, expected),
                        RankIn(diagnostics.Semantic, expected),
                        rank >= 0 ? rank + 1 : null));
                }
            }

            outcomes.Add(new QueryOutcome(query, rows, hitRanks, relaxedHitRanks, missing, provenance, watch.Elapsed.TotalMilliseconds));
        }
        }

        var report = BuildReport(label, queries, outcomes, labelProblems, new
        {
            lucene = Path.GetFullPath(lucene),
            vec = Path.GetFullPath(vec),
            exclusion = exclusion.Rules,
            exclusionSource = exclusion.Source,
            exclusionDisabled = noExclude,
            dedupeBySymbolId = !noDedupe,
            useSemanticScoringOnly = !useHybrid,
            fusion = useHybrid ? fusion.ToString() : "semantic-only",
            modExpansion = modExpansion.ToString(),
            modPathBoost,
            lexicalWeight = useHybrid ? lexicalWeight : 0,
            semanticWeight = useHybrid ? semanticWeight : 1,
            maxResults,
            lexicalCandidates,
            semanticCandidates,
            embeddingServer = embeddingServerUrl
        });

        var json = JsonSerializer.Serialize(report, WriteOptions);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json);
            Console.WriteLine($"[bench] wrote {outPath}");
        }
        else
        {
            Console.WriteLine(json);
        }

        PrintSummary(label, outcomes);

        if (diagnose)
        {
            PrintProvenanceSummary(outcomes);
        }

        if (!string.IsNullOrWhiteSpace(comparePath))
        {
            if (!File.Exists(comparePath))
            {
                Console.Error.WriteLine($"[bench] --compare file not found: {comparePath}");
                return 1;
            }

            PrintComparison(outcomes, comparePath);
        }

        return 0;
    }

    /// <summary>
    /// Check that every <c>expected</c> value resolves to a live document. Returns "<![CDATA[queryId: id]]>"
    /// entries for the ones that do not.
    /// </summary>
    private static List<string> VerifyLabels(List<BenchQuery> queries, string lucenePath)
    {
        var problems = new List<string>();
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        using var retriever = new ExactRetriever(Path.GetFullPath(lucenePath));
        foreach (var query in queries)
        {
            foreach (var expected in query.Expected)
            {
                if (!cache.TryGetValue(expected, out var exists))
                {
                    exists = retriever.Exists(expected);
                    cache[expected] = exists;
                }

                if (!exists)
                {
                    problems.Add($"{query.Id}: {expected}");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Accept either a bare JSON array or an envelope object ({ "_README": [...], "queries": [...] }).
    /// </summary>
    private static List<BenchQuery> LoadQueries(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queries", out var nested) && nested.ValueKind == JsonValueKind.Array)
        {
            array = nested;
        }
        else
        {
            throw new InvalidOperationException($"{path}: expected a JSON array or an object with a 'queries' array.");
        }

        var queries = array.Deserialize<List<BenchQuery>>(ReadOptions) ?? new List<BenchQuery>();

        var duplicateIds = queries
            .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException($"{path}: duplicate query ids: {string.Join(", ", duplicateIds)}");
        }

        return queries;
    }

    private static object BuildReport(string label, List<BenchQuery> queries, List<QueryOutcome> outcomes, List<string> labelProblems, object config)
    {
        var all = Aggregate(outcomes);
        var byCategory = outcomes
            .GroupBy(o => o.Source.Category ?? "uncategorized", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Aggregate(g.ToList()), StringComparer.OrdinalIgnoreCase);

        var holdout = outcomes.Where(o => o.Source.Holdout).ToList();

        return new
        {
            label,
            generatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            queryCount = queries.Count,
            config,
            aggregate = all,
            holdoutCount = holdout.Count,
            holdoutAggregate = holdout.Count > 0 ? Aggregate(holdout) : null,
            labelProblems = labelProblems,
            perCategory = byCategory,
            queries = outcomes.Select(o => new
            {
                id = o.Source.Id,
                query = o.Source.Query,
                kind = o.Source.Kind,
                category = o.Source.Category,
                holdout = o.Source.Holdout,
                note = o.Source.Note,
                expected = o.Source.Expected,
                missingExpected = o.MissingExpected,
                provenance = o.Provenance.Count == 0 ? null : o.Provenance.Select(p => new
                {
                    p.Expected,
                    lexicalRank = p.LexicalRank,
                    semanticRank = p.SemanticRank,
                    fusedRank = p.FusedRank,
                    leg = p.Leg
                }),
                hitRanks = o.HitRanks,
                latencyMs = Math.Round(o.LatencyMs, 1),
                recall5 = Recall(o, 5),
                recall10 = Recall(o, 10),
                relaxedRecall5 = RelaxedRecall(o, 5),
                relaxedRecall10 = RelaxedRecall(o, 10),
                mrr = ReciprocalRank(o),
                ndcg10 = Ndcg(o, 10),
                contextPrecision10 = ContextPrecision(o, 10),
                results = o.Results.Select(r => new
                {
                    r.Rank,
                    r.ItemId,
                    r.SymbolId,
                    score = Math.Round(r.Score, 4),
                    r.Path,
                    r.Signature
                })
            })
        };
    }

    private static object Aggregate(List<QueryOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return new { queries = 0 };
        }

        var latencies = outcomes.Select(o => o.LatencyMs).OrderBy(x => x).ToList();
        var zero = outcomes.Count(o => o.Results.Count == 0);

        return new
        {
            queries = outcomes.Count,
            recall5 = Math.Round(outcomes.Average(o => Recall(o, 5)), 4),
            recall10 = Math.Round(outcomes.Average(o => Recall(o, 10)), 4),
            relaxedRecall5 = Math.Round(outcomes.Average(o => RelaxedRecall(o, 5)), 4),
            relaxedRecall10 = Math.Round(outcomes.Average(o => RelaxedRecall(o, 10)), 4),
            mrr = Math.Round(outcomes.Average(ReciprocalRank), 4),
            ndcg10 = Math.Round(outcomes.Average(o => Ndcg(o, 10)), 4),
            contextPrecision10 = Math.Round(outcomes.Average(o => ContextPrecision(o, 10)), 4),
            hitsAt1 = outcomes.Count(o => o.HitRanks.Contains(1)),
            zeroResultRate = Math.Round((double)zero / outcomes.Count, 4),
            missingExpectedTotal = outcomes.Sum(o => o.MissingExpected.Count),
            latencyMsAvg = Math.Round(latencies.Average(), 1),
            latencyMsP50 = Math.Round(Percentile(latencies, 0.50), 1),
            latencyMsP95 = Math.Round(Percentile(latencies, 0.95), 1)
        };
    }

    private static double Recall(QueryOutcome outcome, int k)
        => Recall(outcome.HitRanks, outcome.Source.Expected.Count, k);

    /// <summary>Recall where a member of the expected type also counts as a hit.</summary>
    private static double RelaxedRecall(QueryOutcome outcome, int k)
        => Recall(outcome.RelaxedHitRanks, outcome.Source.Expected.Count, k);

    private static double Recall(List<int> ranks, int expectedCount, int k)
        => expectedCount == 0 ? 0 : (double)ranks.Count(rank => rank <= k) / expectedCount;

    private static double ReciprocalRank(QueryOutcome outcome)
    {
        if (outcome.HitRanks.Count == 0)
        {
            return 0;
        }

        return 1.0 / outcome.HitRanks.Min();
    }

    private static double Ndcg(QueryOutcome outcome, int k)
    {
        var gains = new double[k];
        foreach (var rank in outcome.HitRanks.Where(rank => rank <= k))
        {
            gains[rank - 1] = 1.0;
        }

        var dcg = 0.0;
        for (var i = 0; i < k; i++)
        {
            if (gains[i] > 0)
            {
                dcg += gains[i] / Math.Log2(i + 2);
            }
        }

        var idealCount = Math.Min(outcome.Source.Expected.Count, k);
        var idcg = 0.0;
        for (var i = 0; i < idealCount; i++)
        {
            idcg += 1.0 / Math.Log2(i + 2);
        }

        return idcg <= 0 ? 0 : dcg / idcg;
    }

    /// <summary>
    /// RAGAS-style Context Precision: the rank-weighted average of precision@k over the relevant
    /// items that were actually retrieved (equivalent to average precision).
    /// </summary>
    private static double ContextPrecision(QueryOutcome outcome, int k)
    {
        if (outcome.HitRanks.Count == 0)
        {
            return 0;
        }

        var relevantInTopK = outcome.HitRanks.Count(rank => rank <= k);
        if (relevantInTopK == 0)
        {
            return 0;
        }

        var sum = 0.0;
        foreach (var rank in outcome.HitRanks.Where(rank => rank <= k))
        {
            var precisionAtRank = 1.0 / rank; // binary relevance: 1 relevant item in the first `rank`
            sum += precisionAtRank;
        }

        return sum / relevantInTopK;
    }

    private static bool Matches(ResultRow row, string expected)
        => MatchesId(row.ItemId, row.SymbolId, expected);

    private static bool MatchesId(string itemId, string symbolId, string expected)
    {
        if (string.Equals(itemId, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(symbolId, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (itemId.StartsWith(expected + "@", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return symbolId.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>1-based rank of the expected value inside a raw leg's candidate list, or null when absent.</summary>
    private static int? RankIn(IReadOnlyList<SearchCandidate> candidates, string expected)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (MatchesId(candidates[i].ItemId, candidates[i].SymbolId, expected))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Relaxed hit: the expected symbol was not returned verbatim, but one of its members was
    /// (e.g. <c>RimWorld.ThoughtHandler</c> vs <c>RimWorld.ThoughtHandler.public ThoughtHandler(Pawn)</c>).
    /// A retriever that surfaces the right class as a member has still found it, so this is tracked
    /// separately from the strict metric instead of being blended into it.
    /// </summary>
    private static bool IsMemberOf(ResultRow row, string expected)
        => row.SymbolId.StartsWith(expected + ".", StringComparison.OrdinalIgnoreCase);

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

    private static void PrintSummary(string label, List<QueryOutcome> outcomes)
    {
        var aggregate = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(Aggregate(outcomes)))!;

        Console.WriteLine();
        Console.WriteLine($"=== {label} ({outcomes.Count} queries) ===");
        Console.WriteLine($"  Recall@5   {aggregate["recall5"].GetDouble():F4}");
        Console.WriteLine($"  Recall@10  {aggregate["recall10"].GetDouble():F4}");
        Console.WriteLine($"  R-Recall@5 {aggregate["relaxedRecall5"].GetDouble():F4}   (member of the expected type counts)");
        Console.WriteLine($"  R-Recall@10 {aggregate["relaxedRecall10"].GetDouble():F4}");
        Console.WriteLine($"  MRR        {aggregate["mrr"].GetDouble():F4}");
        Console.WriteLine($"  nDCG@10    {aggregate["ndcg10"].GetDouble():F4}");
        Console.WriteLine($"  CP@10      {aggregate["contextPrecision10"].GetDouble():F4}");
        Console.WriteLine($"  Hits@1     {aggregate["hitsAt1"].GetInt32()}/{outcomes.Count}");
        Console.WriteLine($"  0-result   {aggregate["zeroResultRate"].GetDouble():P1}");
        Console.WriteLine($"  latency    avg {aggregate["latencyMsAvg"].GetDouble():F0} ms | p50 {aggregate["latencyMsP50"].GetDouble():F0} ms | p95 {aggregate["latencyMsP95"].GetDouble():F0} ms");
        if (aggregate["missingExpectedTotal"].GetInt32() > 0)
        {
            Console.WriteLine($"  ⚠ {aggregate["missingExpectedTotal"].GetInt32()} expected item(s) were never retrieved — check labeling");

            foreach (var outcome in outcomes.Where(o => o.MissingExpected.Count > 0))
            {
                Console.WriteLine($"      {outcome.Source.Id}: missing {string.Join(", ", outcome.MissingExpected)}");
            }
        }
    }

    /// <summary>
    /// Split the expected items by where they were available, which is what tells apart a
    /// candidate-generation problem from a ranking problem.
    /// </summary>
    private static void PrintProvenanceSummary(List<QueryOutcome> outcomes)
    {
        var all = outcomes.SelectMany(o => o.Provenance).ToList();
        if (all.Count == 0)
        {
            return;
        }

        var both = all.Count(p => p.Leg == "both");
        var lexicalOnly = all.Count(p => p.Leg == "lexical-only");
        var semanticOnly = all.Count(p => p.Leg == "semantic-only");
        var absent = all.Count(p => p.Leg == "absent");
        var inFused = all.Count(p => p.FusedRank is not null);
        var presentButLost = both + lexicalOnly + semanticOnly - inFused;

        Console.WriteLine();
        Console.WriteLine("=== candidate provenance (per expected item) ===");
        Console.WriteLine($"  total expected        {all.Count}");
        Console.WriteLine($"  in lexical leg only   {lexicalOnly}");
        Console.WriteLine($"  in semantic leg only  {semanticOnly}");
        Console.WriteLine($"  in both legs          {both}");
        Console.WriteLine($"  ABSENT from both legs {absent}   <- candidate generation problem; fusion/rerank cannot fix");
        Console.WriteLine($"  present but not in top-N {presentButLost}   <- ranking problem; fusion/rerank CAN fix");

        var missingByCategory = all
            .Where(p => p.Leg == "absent")
            .GroupBy(p => p.Expected)
            .Count();

        Console.WriteLine();
        Console.WriteLine($"  absent list ({missingByCategory} item(s), by query):");
        foreach (var outcome in outcomes.Where(o => o.Provenance.Any(p => p.Leg == "absent")))
        {
            var items = outcome.Provenance.Where(p => p.Leg == "absent").Select(p => p.Expected);
            Console.WriteLine($"    {outcome.Source.Id,-6} [{outcome.Source.Category}] {string.Join(", ", items)}");
        }
    }

    private static void PrintComparison(List<QueryOutcome> outcomes, string comparePath)
    {
        var previous = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(comparePath));
        var previousQueries = previous.GetProperty("queries")
            .EnumerateArray()
            .ToDictionary(q => q.GetProperty("id").GetString() ?? string.Empty, q => q, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine($"=== vs {Path.GetFileName(comparePath)} ===");

        var currentAggregate = JsonSerializer.Serialize(Aggregate(outcomes));
        var current = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(currentAggregate)!;
        var previousAggregate = previous.GetProperty("aggregate").Deserialize<Dictionary<string, JsonElement>>()!;

        foreach (var metric in new[] { "recall5", "recall10", "mrr", "ndcg10", "contextPrecision10" })
        {
            var before = previousAggregate[metric].GetDouble();
            var after = current[metric].GetDouble();
            var delta = after - before;
            var marker = Math.Abs(delta) < 0.0005 ? " " : delta > 0 ? "▲" : "▼";
            Console.WriteLine($"  {metric,-20} {before:F4} -> {after:F4}  {marker} {delta:+0.0000;-0.0000;0.0000}");
        }

        var regressions = new List<string>();
        var gains = new List<string>();
        foreach (var outcome in outcomes)
        {
            if (!previousQueries.TryGetValue(outcome.Source.Id, out var before))
            {
                continue;
            }

            var beforeRecall = before.GetProperty("recall10").GetDouble();
            var beforeNdcg = before.TryGetProperty("ndcg10", out var ndcgElement) ? ndcgElement.GetDouble() : 0;
            var afterRecall = Recall(outcome, 10);
            var afterNdcg = Ndcg(outcome, 10);

            var recallDelta = afterRecall - beforeRecall;
            var ndcgDelta = afterNdcg - beforeNdcg;

            // Recall first; when recall is unchanged, fall back to nDCG so that a pure *ranking*
            // improvement (which is what fusion and reranking produce) is still visible per query.
            var improved = recallDelta > 1e-9 || (Math.Abs(recallDelta) <= 1e-9 && ndcgDelta > 1e-3);
            var worsened = recallDelta < -1e-9 || (Math.Abs(recallDelta) <= 1e-9 && ndcgDelta < -1e-3);
            var detail = $"R@10 {beforeRecall:F2}->{afterRecall:F2}, nDCG {beforeNdcg:F3}->{afterNdcg:F3}";

            if (worsened)
            {
                regressions.Add($"{outcome.Source.Id} ({detail})");
            }
            else if (improved)
            {
                gains.Add($"{outcome.Source.Id} ({detail})");
            }
        }

        Console.WriteLine($"  regressions: {(regressions.Count == 0 ? "none" : string.Join(", ", regressions))}");
        Console.WriteLine($"  gains:       {(gains.Count == 0 ? "none" : string.Join(", ", gains))}");
    }

    private static int ParseInt(Dictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double ParseDouble(Dictionary<string, string> options, string key, double fallback)
        => options.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>Parse "--mod-expand none|lexical|both".</summary>
    private static ModExpansionMode ParseModExpansion(string value) => value.ToLowerInvariant() switch
    {
        "none" or "off" or "false" => ModExpansionMode.None,
        "lexical" or "lex" => ModExpansionMode.Lexical,
        "both" or "on" or "true" => ModExpansionMode.Both,
        _ => throw new InvalidOperationException($"--mod-expand expects none|lexical|both but got '{value}'.")
    };

    /// <summary>Parse "--weights 0.5,0.5" into (lexical, semantic).</summary>
    private static (double Lexical, double Semantic) ParseWeights(string value)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lexical) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var semantic))
        {
            throw new InvalidOperationException($"--weights expects 'lex,sem' (e.g. 0.5,0.5) but got '{value}'.");
        }

        return (lexical, semantic);
    }

    private static string GetOrDefault(Dictionary<string, string> options, string key, string fallback)
        => options.TryGetValue(key, out var value) ? value : fallback;
}
