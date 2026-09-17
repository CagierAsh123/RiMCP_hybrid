using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;

namespace RimWorldCodeRag.Retrieval;

public sealed class RoughSearcher : IDisposable
{
    private const float IdentifierBoost = 2.5f;

    private readonly RoughSearchConfig _config;
    private readonly FSDirectory _directory;
    private readonly DirectoryReader _reader;
    private readonly IndexSearcher _searcher;
    private readonly StandardAnalyzer _analyzer;

    // Lucene's QueryParser carries mutable parse state, so it is NOT safe to share across threads.
    // A searcher instance is now long-lived and may serve concurrent requests, so each thread
    // gets its own parser.
    private readonly ThreadLocal<QueryParser> _symbolIdParser;
    private readonly ThreadLocal<QueryParser> _textParser;
    private readonly VectorIndex _vectorIndex;
    private readonly IQueryEmbeddingGenerator _queryEmbeddingGenerator;
    private readonly PathExclusionFilter _exclusionFilter;
    private readonly ModCatalog _modCatalog;
    private readonly ResultCache _resultCache = new(200);
    private RerankerClient? _rerankerClient;

    public RoughSearcher(RoughSearchConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        _exclusionFilter = _config.ExclusionFilter ?? PathExclusionFilter.LoadForIndex(_config.VectorIndexPath);
        _modCatalog = _config.ModCatalog ?? ModCatalog.LoadForIndex(_config.VectorIndexPath);

        _directory = FSDirectory.Open(_config.LuceneIndexPath);
        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader);
        _analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);

        _symbolIdParser = new ThreadLocal<QueryParser>(() => new QueryParser(LuceneVersion.LUCENE_48, LuceneWriter.FieldSymbolId, _analyzer)
        {
            DefaultOperator = Operator.OR
        });

        _textParser = new ThreadLocal<QueryParser>(() => new QueryParser(LuceneVersion.LUCENE_48, LuceneWriter.FieldText, _analyzer)
        {
            DefaultOperator = Operator.OR
        });

        _vectorIndex = VectorIndex.Load(_config.VectorIndexPath, _exclusionFilter);
        _queryEmbeddingGenerator = CreateQueryEmbeddingGenerator();

        if (_vectorIndex.Entries.Count == 0)
        {
            Console.Error.WriteLine(
                "[search] WARNING: the vector index is empty — semantic retrieval is disabled. " +
                "Results will be lexical-only (and with UseSemanticScoringOnly the response falls back to lexical).");
        }
    }

    /// <summary>A snapshot of the searcher's defaults — used for diagnostics and for callers that pass no options.</summary>
    public RoughSearchOptions DefaultOptions => RoughSearchOptions.FromConfig(_config);

    public Task<IReadOnlyList<RoughSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        => SearchAsync(query, null, cancellationToken);

    /// <summary>
    /// Search with optional per-request overrides of <c>kind</c> / <c>max_results</c> / candidate counts.
    /// Null values inherit from the searcher's configuration, so the same instance can serve
    /// concurrent requests with different parameters.
    /// </summary>
    public async Task<IReadOnlyList<RoughSearchResult>> SearchAsync(string query, RoughSearchOptions? options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<RoughSearchResult>();
        }

        var opts = Resolve(options);

        // Result cache (task 1.5): keyed on everything that can change the output.
        var cacheKey = BuildCacheKey(query, opts);
        if (_resultCache.TryGet(cacheKey, out var cached))
        {
            Console.Error.WriteLine($"[debug] result-cache hit ({_resultCache.Count} entries)");
            return cached;
        }

        var (lexicalMatches, semanticMatches, final) = await SearchCoreAsync(query, opts, cancellationToken).ConfigureAwait(false);

        Console.Error.WriteLine($"[debug] Lexical: {lexicalMatches.Count}, Semantic (full-corpus): {semanticMatches.Count}");

        _resultCache.Add(cacheKey, final);
        return final;
    }

    /// <summary>
    /// The shared retrieval core: both legs, fusion, optional reranking, truncation.
    ///
    /// <para>
    /// <see cref="SearchAsync"/> and <see cref="SearchWithDiagnosticsAsync"/> must go through this,
    /// otherwise the diagnostic entry point silently reports a different ranking than the one the
    /// tools return (that inconsistency briefly made a rerank measurement look like a no-op).
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<LexicalMatch> Lexical, IReadOnlyList<VectorMatch> Semantic, IReadOnlyList<RoughSearchResult> Results)> SearchCoreAsync(
        string query,
        ResolvedOptions opts,
        CancellationToken cancellationToken)
    {
        var (lexicalMatches, semanticMatches) = await RetrieveAsync(query, opts, cancellationToken).ConfigureAwait(false);

        // When reranking, fuse a wider pool than we intend to return: the whole point is to promote a
        // good candidate that fusion ranked just outside the cut.
        var poolSize = opts.RerankCandidates > 0 ? Math.Max(opts.MaxResults, opts.RerankCandidates) : opts.MaxResults;
        IReadOnlyList<RoughSearchResult> merged = MergeResults(lexicalMatches, semanticMatches, opts, poolSize);

        if (opts.RerankCandidates > 0 && merged.Count > 1)
        {
            merged = await RerankAsync(query, merged, opts, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<RoughSearchResult> final = merged.Count > opts.MaxResults
            ? merged.Take(opts.MaxResults).ToList()
            : merged;

        return (lexicalMatches, semanticMatches, final);
    }

    /// <summary>
    /// Reorder candidates with the cross-encoder. Failure is never fatal: the fused order is returned
    /// unchanged, because a reranker is a refinement, not a gate.
    /// </summary>
    private async Task<List<RoughSearchResult>> RerankAsync(
        string query,
        IReadOnlyList<RoughSearchResult> candidates,
        ResolvedOptions opts,
        CancellationToken cancellationToken)
    {
        var client = _rerankerClient ??= new RerankerClient(opts.RerankServerUrl!);
        var pool = candidates.Take(opts.RerankCandidates).ToList();

        // Rerank on the enriched preview (semantic title + excerpt) rather than the whole chunk: it is
        // what the index already treats as the chunk's "headline", and it keeps the prompt within the
        // cross-encoder's window.
        var documents = pool.Select(ToRerankDocument).ToList();

        try
        {
            var scores = await client.ScoreAsync(query, documents, cancellationToken).ConfigureAwait(false);

            var reordered = pool
                .Select((result, index) => (Result: result, Score: index < scores.Length ? scores[index] : float.NegativeInfinity))
                .OrderByDescending(pair => pair.Score)
                .Select(pair => pair.Result)
                .ToList();

            // Keep anything beyond the reranked pool in its original relative order.
            if (candidates.Count > pool.Count)
            {
                reordered.AddRange(candidates.Skip(pool.Count));
            }

            Console.Error.WriteLine($"[debug] reranked {pool.Count} candidate(s) via {client.BaseUrl}");
            return reordered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[search] WARNING: reranking failed ({ex.Message}); returning the fused order.");
            return candidates.ToList();
        }
    }

    private static string ToRerankDocument(RoughSearchResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.Preview) ? result.Preview : result.Signature;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = result.SymbolId;
        }

        const int MaxChars = 4000;
        return text!.Length > MaxChars ? text[..MaxChars] : text;
    }

    /// <summary>True when the chunk's path lies inside one of the named mods' directories.</summary>
    private static bool IsUnderAnyMod(string path, IReadOnlyList<string> modDirs)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var dir in modDirs)
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private ModExpansion BuildExpansion(string query, ResolvedOptions opts)
    {
        if (_modCatalog.IsEmpty || (opts.ModExpansion == ModExpansionMode.None && opts.ModPathBoost <= 0))
        {
            return new ModExpansion(query, query, Array.Empty<string>());
        }

        var mods = _modCatalog.Detect(query, opts.MaxModMatches);
        if (mods.Count == 0)
        {
            return new ModExpansion(query, query, Array.Empty<string>());
        }

        var modDirs = mods.Select(m => m.Dir).ToList();

        if (opts.ModExpansion == ModExpansionMode.None)
        {
            Console.Error.WriteLine($"[debug] mod-boost: {string.Join(", ", modDirs)} (+{opts.ModPathBoost:F2})");
            return new ModExpansion(query, query, modDirs);
        }

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var term in mod.ExpansionTerms)
            {
                if (terms.Count >= opts.MaxExpansionTerms)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(term) && seen.Add(term))
                {
                    terms.Add(term);
                }
            }
        }

        if (terms.Count == 0)
        {
            return new ModExpansion(query, query, modDirs);
        }

        var expanded = $"{query} {string.Join(' ', terms)}";
        var embeddingQuery = opts.ModExpansion == ModExpansionMode.Both ? expanded : query;
        Console.Error.WriteLine($"[debug] mod-expansion: {string.Join(", ", modDirs)} (+{terms.Count} terms, mode={opts.ModExpansion})");

        return new ModExpansion(expanded, embeddingQuery, modDirs);
    }

    private readonly record struct ModExpansion(string LexicalQuery, string EmbeddingQuery, IReadOnlyList<string> Mods);

    private static string BuildCacheKey(string query, ResolvedOptions opts)
        => string.Join('\u0001',
            query,
            opts.Kind ?? string.Empty,
            opts.MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opts.LexicalCandidates.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opts.SemanticCandidates.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opts.DedupeBySymbolId.ToString(),
            opts.UseSemanticScoringOnly.ToString(),
            opts.Fusion.ToString(),
            opts.ModExpansion.ToString(),
            opts.ModPathBoost.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            opts.RerankCandidates.ToString(System.Globalization.CultureInfo.InvariantCulture),
            opts.LexicalWeight.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            opts.SemanticWeight.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Same as <see cref="SearchAsync(string, RoughSearchOptions?, CancellationToken)"/> but also exposes
    /// where each candidate came from. This separates "the candidate pool never contained the answer"
    /// (a recall/embedding problem) from "it was there but ranked badly" (a fusion/rerank problem) —
    /// without it, fusion tuning is guesswork.
    /// </summary>
    public async Task<SearchDiagnostics> SearchWithDiagnosticsAsync(string query, RoughSearchOptions? options, CancellationToken cancellationToken = default)
    {
        var opts = Resolve(options);
        var (lexicalMatches, semanticMatches, results) = await SearchCoreAsync(query, opts, cancellationToken).ConfigureAwait(false);

        var lexicalRanks = lexicalMatches
            .Select(m => new SearchCandidate(
                m.ItemId,
                m.Document.Get(LuceneWriter.FieldSymbolId) ?? m.ItemId,
                m.Document.Get(LuceneWriter.FieldPath) ?? string.Empty,
                m.Score))
            .ToList();

        var semanticRanks = semanticMatches
            .Select(m => new SearchCandidate(m.Entry.ItemId, m.Entry.SymbolId, m.Entry.Path, m.Score))
            .ToList();

        return new SearchDiagnostics(lexicalRanks, semanticRanks, results);
    }

    /// <summary>Run both retrieval legs in parallel and return their raw candidate lists.</summary>
    private async Task<(IReadOnlyList<LexicalMatch> Lexical, IReadOnlyList<VectorMatch> Semantic)> RetrieveAsync(
        string query,
        ResolvedOptions opts,
        CancellationToken cancellationToken)
    {
        var expansion = BuildExpansion(query, opts);

        // Dual-path retrieval: run lexical and full-corpus semantic search in parallel.
        // This ensures that items missed by BM25 can still be found by vector similarity.
        var lexicalTask = Task.Run(() => SearchLexical(expansion.LexicalQuery, opts.LexicalCandidates, opts.Kind), cancellationToken);

        // The embedding call is allowed to fail: losing the semantic leg costs recall, but failing
        // the whole query costs everything. The lexical leg still runs.
        float[] queryVector;
        try
        {
            queryVector = await _queryEmbeddingGenerator.EmbedAsync(expansion.EmbeddingQuery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[search] WARNING: could not embed the query ({ex.Message}); continuing with the lexical leg only.");
            queryVector = Array.Empty<float>();
        }

        var lexicalMatches = await lexicalTask.ConfigureAwait(false);

        // Full-corpus semantic search (not limited to lexical candidates)
        IReadOnlyList<VectorMatch> semanticMatches;
        if (_vectorIndex.Entries.Count == 0 || queryVector.Length == 0)
        {
            if (_vectorIndex.Entries.Count > 0 && queryVector.Length == 0)
            {
                Console.Error.WriteLine(
                    "[search] WARNING: the query embedding is empty (no embedding server/subprocess configured " +
                    "or it returned nothing) — the semantic leg is skipped.");
            }

            semanticMatches = Array.Empty<VectorMatch>();
        }
        else
        {
            // Apply kind filter to vector entries (xml: prefix = XML, otherwise = C#)
            var allEntries = _vectorIndex.Entries;
            var kindLower = opts.Kind?.ToLowerInvariant();
            var wantCSharp = kindLower == "csharp" || kindLower == "cs";
            var wantXml = kindLower == "def" || kindLower == "xml";

            // Build filtered index list to avoid scoring irrelevant entries
            List<int> candidateIndices;
            if (wantCSharp || wantXml)
            {
                candidateIndices = new List<int>(allEntries.Count);
                for (var i = 0; i < allEntries.Count; i++)
                {
                    var isXml = allEntries[i].SymbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase);
                    if ((wantXml && isXml) || (wantCSharp && !isXml))
                    {
                        candidateIndices.Add(i);
                    }
                }
            }
            else
            {
                candidateIndices = Enumerable.Range(0, allEntries.Count).ToList();
            }

            // Parallel dot-product computation across candidate vectors
            var scores = new float[allEntries.Count];
            var dim = queryVector.Length;

            // Metadata boost: when the query names a mod, its own chunks get a bonus *before* the
            // top-N cut, so a chunk whose cosine is mediocre but which is unambiguously inside the
            // named mod still reaches the candidate pool. This is the fix for the measured
            // "query says Humanoid Alien Races, code says AlienRace" miss — unlike lexical query
            // expansion it cannot dilute anything.
            var modDirs = expansion.Mods;
            var modBoost = modDirs.Count > 0 ? (float)opts.ModPathBoost : 0f;

            Parallel.ForEach(candidateIndices, i =>
            {
                var entry = allEntries[i];
                if (entry.Vector.Length != dim)
                {
                    return;
                }

                var score = DotProduct(queryVector, entry.Vector);
                if (modBoost > 0f && IsUnderAnyMod(entry.Path, modDirs))
                {
                    score += modBoost;
                }

                scores[i] = score;
            });

            // Take top candidates by score
            var semanticTake = Math.Max(opts.MaxResults * 3, opts.SemanticCandidates);
            var topIndices = candidateIndices
                .OrderByDescending(i => scores[i])
                .Take(semanticTake)
                .ToList();

            var matches = new List<VectorMatch>(topIndices.Count);
            foreach (var idx in topIndices)
            {
                if (scores[idx] > 0)
                {
                    matches.Add(new VectorMatch(allEntries[idx], scores[idx]));
                }
            }
            semanticMatches = matches;
        }

        return (lexicalMatches, semanticMatches);
    }

    private ResolvedOptions Resolve(RoughSearchOptions? options)
    {
        var o = options ?? RoughSearchOptions.FromConfig(_config);
        return new ResolvedOptions(
            o.Kind ?? _config.Kind,
            o.MaxResults ?? _config.MaxResults,
            o.LexicalCandidates ?? _config.LexicalCandidates,
            o.SemanticCandidates ?? _config.SemanticCandidates,
            o.DedupeBySymbolId ?? _config.DedupeBySymbolId,
            o.UseSemanticScoringOnly ?? _config.UseSemanticScoringOnly,
            o.LexicalWeight ?? _config.LexicalWeight,
            o.SemanticWeight ?? _config.SemanticWeight,
            o.Fusion ?? _config.Fusion,
            o.ModExpansion ?? _config.ModExpansion,
            o.ModPathBoost ?? _config.ModPathBoost,
            o.MaxModMatches ?? _config.MaxModMatches,
            o.MaxExpansionTerms ?? _config.MaxExpansionTerms,
            o.RerankCandidates ?? _config.RerankCandidates,
            o.RerankServerUrl ?? _config.RerankServerUrl);
    }

    private readonly record struct ResolvedOptions(
        string? Kind,
        int MaxResults,
        int LexicalCandidates,
        int SemanticCandidates,
        bool DedupeBySymbolId,
        bool UseSemanticScoringOnly,
        double LexicalWeight,
        double SemanticWeight,
        FusionMode Fusion,
        ModExpansionMode ModExpansion,
        double ModPathBoost,
        int MaxModMatches,
        int MaxExpansionTerms,
        int RerankCandidates,
        string? RerankServerUrl);

    private static float DotProduct(float[] a, ReadOnlyMemory<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0f;
        }

#if NET6_0_OR_GREATER
        // SIMD-accelerated; `b` is a zero-copy slice of the packed vectors.bin payload.
        return System.Numerics.Tensors.TensorPrimitives.Dot(a, b.Span);
#else
        var span = b.Span;
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * span[i];
        }

        return (float)sum;
#endif
    }

    /// <summary>
    /// Expand a user query by splitting camelCase/PascalCase tokens.
    /// e.g. "pawnHungerTick ThingDef" → "pawnHungerTick pawn hunger tick ThingDef thing def"
    /// This dramatically improves BM25 recall for identifier-style queries.
    /// </summary>
    private static string ExpandQuery(string query)
    {
        var tokens = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            expanded.Add(token);
            foreach (var sub in Common.TextUtilities.SplitIdentifier(token))
            {
                if (sub.Length > 1) // skip single-char noise
                {
                    expanded.Add(sub);
                }
            }
        }
        return string.Join(' ', expanded);
    }

    private IReadOnlyList<LexicalMatch> SearchLexical(string query, int take, string? kind)
    {
        var booleanQuery = new BooleanQuery();
        var expandedQuery = ExpandQuery(query);
        var escapedQuery = QueryParserBase.Escape(expandedQuery);

        try
        {
            var symbolIdQuery = _symbolIdParser.Value!.Parse(escapedQuery);
            symbolIdQuery.Boost = IdentifierBoost;
            booleanQuery.Add(symbolIdQuery, Occur.SHOULD);
        }
        catch
        {
            // Ignore parse failures for the symbol ID field.
        }

        try
        {
            var textQuery = _textParser.Value!.Parse(escapedQuery);
            booleanQuery.Add(textQuery, Occur.SHOULD);
        }
        catch
        {
            // If text query fails and we have no other clauses, we can't proceed.
            if (booleanQuery.Clauses.Count == 0)
            {
                return Array.Empty<LexicalMatch>();
            }
        }

        if (booleanQuery.Clauses.Count == 0)
        {
            return Array.Empty<LexicalMatch>();
        }

        Query luceneQuery;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var filterQuery = new BooleanQuery();
            filterQuery.Add(booleanQuery, Occur.MUST);

            var kindLower = kind.ToLowerInvariant();
            if (kindLower == "csharp" || kindLower == "cs")
            {
                var langTerm = new Term(LuceneWriter.FieldLang, "csharp");
                filterQuery.Add(new TermQuery(langTerm), Occur.MUST);
            }
            else if (kindLower == "def" || kindLower == "xml")
            {
                var langTerm = new Term(LuceneWriter.FieldLang, "xml");
                filterQuery.Add(new TermQuery(langTerm), Occur.MUST);
            }

            luceneQuery = filterQuery;
        }
        else
        {
            luceneQuery = booleanQuery;
        }

        var hits = _searcher.Search(luceneQuery, take);
        if (hits.ScoreDocs.Length == 0)
        {
            return Array.Empty<LexicalMatch>();
        }

        var results = new List<LexicalMatch>(hits.ScoreDocs.Length);
        foreach (var scoreDoc in hits.ScoreDocs)
        {
            var doc = _searcher.Doc(scoreDoc.Doc);
            var itemId = doc.Get(LuceneWriter.FieldItemId);
            if (string.IsNullOrEmpty(itemId))
            {
                continue;
            }

            if (_exclusionFilter.IsExcluded(doc.Get(LuceneWriter.FieldPath)))
            {
                continue;
            }

            results.Add(new LexicalMatch(itemId, doc, scoreDoc.Score));
        }

        return results;
    }

    private async Task<IReadOnlyList<VectorMatch>> SearchSemanticAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var queryVector = await _queryEmbeddingGenerator.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
            return _vectorIndex.FindNearest(queryVector, _config.SemanticCandidates);
        }
        catch
        {
            return Array.Empty<VectorMatch>();
        }
    }

    private IReadOnlyList<RoughSearchResult> MergeResults(IReadOnlyList<LexicalMatch> lexical, IReadOnlyList<VectorMatch> semantic, ResolvedOptions opts, int take)
    {
        if (opts.UseSemanticScoringOnly)
        {
            // "Semantic-only" must degrade to lexical, not to nothing. Returning an empty list
            // because the semantic leg produced no candidates looks identical to "the corpus has no
            // such thing" — that failure mode silently broke the live MCP tool once already.
            if (semantic.Count == 0)
            {
                if (lexical.Count == 0)
                {
                    return new List<RoughSearchResult>();
                }

                Console.Error.WriteLine(
                    $"[search] WARNING: semantic-only requested but the semantic leg returned nothing " +
                    $"({_vectorIndex.Entries.Count} vectors loaded); falling back to {lexical.Count} lexical candidate(s).");

                var maxLexical = lexical[0].Score > 0 ? lexical[0].Score : 1f;
                var lexicalOnly = lexical
                    .Select(match => ToResult(match.ItemId, match.Document, match.Score / maxLexical, "lexical"))
                    .Where(result => result is not null)
                    .Select(result => result!)
                    .ToList();

                return FinalizeResults(lexicalOnly, opts, take);
            }

            // Pure semantic ranking: ignore lexical scores completely.
            var maxScore = semantic[0].Score;

            var ranked = semantic
                .Select(match =>
                {
                    var document = FetchDocument(match.Entry.ItemId);
                    if (document is null) return null;

                    return ToResult(match.Entry.ItemId, document, match.Score / maxScore, "semantic");
                })
                .Where(result => result is not null)
                .Select(result => result!)
                .ToList();

            return FinalizeResults(ranked, opts, take);
        }

        return opts.Fusion == FusionMode.Rrf
            ? MergeRrf(lexical, semantic, opts, take)
            : MergeWeightedSum(lexical, semantic, opts, take);
    }

    /// <summary>
    /// Min-max normalize each leg to [0,1] and combine with the configured weights.
    /// The normalization is the whole point: raw BM25 and cosine scores are not comparable,
    /// so the previous "just add them" version ranked worse than semantic-only
    /// (docs/rag-upgrade-plan-2026-09.md §10.2).
    /// </summary>
    private IReadOnlyList<RoughSearchResult> MergeWeightedSum(IReadOnlyList<LexicalMatch> lexical, IReadOnlyList<VectorMatch> semantic, ResolvedOptions opts, int take)
    {
        var (lexMin, lexMax) = MinMax(lexical.Select(m => (double)m.Score));
        var (semMin, semMax) = MinMax(semantic.Select(m => (double)m.Score));

        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in lexical)
        {
            if (!candidates.TryGetValue(match.ItemId, out var candidate))
            {
                candidate = new Candidate(match.Document);
                candidates[match.ItemId] = candidate;
            }

            candidate.SetLexical(Normalize(match.Score, lexMin, lexMax));
        }

        foreach (var match in semantic)
        {
            if (!candidates.TryGetValue(match.Entry.ItemId, out var candidate))
            {
                var document = FetchDocument(match.Entry.ItemId);
                if (document is null)
                {
                    continue;
                }

                candidate = new Candidate(document);
                candidates[match.Entry.ItemId] = candidate;
            }

            candidate.SetSemantic(Normalize(match.Score, semMin, semMax));
        }

        foreach (var candidate in candidates.Values)
        {
            candidate.ComputeWeightedScore(opts.LexicalWeight, opts.SemanticWeight);
        }

        return FinalizeResults(
            candidates
                .OrderByDescending(kvp => kvp.Value.FinalScore)
                .Select(kvp => ToResultFromCandidate(kvp.Key, kvp.Value))
                .Where(result => result is not null)
                .Select(result => result!)
                .ToList(),
            opts,
            take);
    }

    /// <summary>
    /// Reciprocal rank fusion (k = 60): <c>sum(weight_i / (k + rank_i))</c>.
    /// Scale-free, so it needs no normalization; a useful cross-check against
    /// <see cref="MergeWeightedSum"/>.
    /// </summary>
    private IReadOnlyList<RoughSearchResult> MergeRrf(IReadOnlyList<LexicalMatch> lexical, IReadOnlyList<VectorMatch> semantic, ResolvedOptions opts, int take)
    {
        const int RrfK = 60;
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lexical.Count; i++)
        {
            var match = lexical[i];
            if (!candidates.TryGetValue(match.ItemId, out var candidate))
            {
                candidate = new Candidate(match.Document);
                candidates[match.ItemId] = candidate;
            }

            candidate.AddRrfScore(opts.LexicalWeight / (RrfK + i + 1.0), fromLexical: true);
        }

        for (var i = 0; i < semantic.Count; i++)
        {
            var match = semantic[i];
            if (!candidates.TryGetValue(match.Entry.ItemId, out var candidate))
            {
                var document = FetchDocument(match.Entry.ItemId);
                if (document is null)
                {
                    continue;
                }

                candidate = new Candidate(document);
                candidates[match.Entry.ItemId] = candidate;
            }

            candidate.AddRrfScore(opts.SemanticWeight / (RrfK + i + 1.0), fromLexical: false);
        }

        foreach (var candidate in candidates.Values)
        {
            candidate.ComputeRrfScore();
        }

        return FinalizeResults(
            candidates
                .OrderByDescending(kvp => kvp.Value.FinalScore)
                .Select(kvp => ToResultFromCandidate(kvp.Key, kvp.Value))
                .Where(result => result is not null)
                .Select(result => result!)
                .ToList(),
            opts,
            take);
    }

    private static (double Min, double Max) MinMax(IEnumerable<double> values)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        return double.IsInfinity(min) ? (0, 0) : (min, max);
    }

    private static double Normalize(double value, double min, double max)
    {
        var span = max - min;
        return span <= 1e-9 ? (max > 0 ? 1.0 : 0.0) : (value - min) / span;
    }

    /// <summary>
    /// Read-side post-processing shared by both ranking modes: drop results whose path is
    /// excluded, then collapse duplicate <c>SymbolId</c> entries (the same mod shipped in two
    /// folders, a <c>private/</c> copy next to the public one, …).
    /// </summary>
    private List<RoughSearchResult> FinalizeResults(List<RoughSearchResult> ranked, ResolvedOptions opts, int take)
    {
        var filtered = new List<RoughSearchResult>(ranked.Count);
        foreach (var result in ranked)
        {
            if (_exclusionFilter.IsExcluded(result.Path))
            {
                continue;
            }

            filtered.Add(result);
        }

        if (!opts.DedupeBySymbolId)
        {
            return filtered.Take(take).ToList();
        }

        var best = new Dictionary<string, RoughSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in filtered)
        {
            var key = string.IsNullOrEmpty(result.SymbolId) ? result.ItemId : result.SymbolId;
            if (!best.TryGetValue(key, out var current) || IsBetterDuplicate(result, current))
            {
                best[key] = result;
            }
        }

        return best.Values
            .OrderByDescending(result => result.Score)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Duplicate priority: non-<c>private/</c> path first, then higher score, then shorter path.
    /// </summary>
    private static bool IsBetterDuplicate(RoughSearchResult candidate, RoughSearchResult current)
    {
        var candidateRank = DuplicateRank(candidate);
        var currentRank = DuplicateRank(current);
        if (candidateRank != currentRank)
        {
            return candidateRank < currentRank;
        }

        if (Math.Abs(candidate.Score - current.Score) > 1e-6)
        {
            return candidate.Score > current.Score;
        }

        return (candidate.Path?.Length ?? int.MaxValue) < (current.Path?.Length ?? int.MaxValue);
    }

    private static int DuplicateRank(RoughSearchResult result)
    {
        var path = result.Path ?? string.Empty;
        return path.IndexOf("\\private\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/private/", StringComparison.OrdinalIgnoreCase) >= 0
            ? 1
            : 0;
    }

    private RoughSearchResult? ToResultFromCandidate(string itemId, Candidate candidate)
    {
        var document = candidate.Document;
        return ToResult(itemId, document, candidate.FinalScore, candidate switch
        {
            { HasLexical: true, HasSemantic: true } => "mixed",
            { HasLexical: true } => "lexical",
            { HasSemantic: true } => "semantic",
            _ => "unknown"
        });
    }

    private RoughSearchResult? ToResult(string itemId, Document document, double score, string source)
    {
        var path = document.Get(LuceneWriter.FieldPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var langValue = document.Get(LuceneWriter.FieldLang);
        var language = langValue?.Equals("xml", StringComparison.OrdinalIgnoreCase) == true ? LanguageKind.Xml : LanguageKind.CSharp;

        var symbolKindValue = document.Get(LuceneWriter.FieldSymbolKind);
        if (!Enum.TryParse(symbolKindValue, true, out SymbolKind symbolKind))
        {
            symbolKind = SymbolKind.Unknown;
        }

        var preview = document.Get(LuceneWriter.FieldPreview) ?? string.Empty;
        var signature = document.Get(LuceneWriter.FieldSignature);
        var ns = document.Get(LuceneWriter.FieldNamespace);
        var containingType = document.Get(LuceneWriter.FieldClass);
        var spanStart = document.GetField(LuceneWriter.FieldSpanStart)?.GetInt32Value() ?? 0;
        var spanEnd = document.GetField(LuceneWriter.FieldSpanEnd)?.GetInt32Value() ?? 0;

        var symbolId = document.Get(LuceneWriter.FieldSymbolId) ?? itemId;
        var storedItemId = document.Get(LuceneWriter.FieldItemId) ?? itemId;

        return new RoughSearchResult
        {
            ItemId = storedItemId,
            SymbolId = symbolId,
            Path = path,
            Language = language,
            SymbolKind = symbolKind,
            Signature = signature,
            Preview = preview,
            Source = source,
            Score = score,
            Namespace = ns,
            ContainingType = containingType,
            SpanStart = spanStart,
            SpanEnd = spanEnd
        };
    }

    private Document? FetchDocument(string itemId)
    {
        var query = new TermQuery(new Term(LuceneWriter.FieldItemId, itemId));
        var hits = _searcher.Search(query, 1);
        if (hits.TotalHits == 0)
        {
            return null;
        }

        return _searcher.Doc(hits.ScoreDocs[0].Doc);
    }

    private IQueryEmbeddingGenerator CreateQueryEmbeddingGenerator()
    {
        var serverConfigured = !string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl);
        //When apikey modelname exists, using api instead
        var apiConfigured =serverConfigured&& !string.IsNullOrWhiteSpace(_config.ApiKey) && !string.IsNullOrWhiteSpace(_config.ModelName);
        var pythonConfigured = !string.IsNullOrWhiteSpace(_config.PythonExecutablePath)
            && !string.IsNullOrWhiteSpace(_config.PythonScriptPath)
            && !string.IsNullOrWhiteSpace(_config.ModelPath);

        // Prefer server, fall back to subprocess
        if (serverConfigured || pythonConfigured)
        {
            try
            {
                return new AdaptiveEmbeddingGenerator(
                    serverUrl: _config.EmbeddingServerUrl,
                    pythonExecutable: _config.PythonExecutablePath,
                    scriptPath: _config.PythonScriptPath,
                    modelPath: _config.ModelPath);
            }
            catch
            {
                // Fall back to hash generator if configuration fails.
            }
        }

        var dimensions = _vectorIndex.VectorDimensions;
        if (dimensions <= 0)
        {
            dimensions = 768; // e5-base-v2 default
        }

    // If no embedding server or python subprocess is configured, prefer lexical-only behavior
    // and warn the user. Returning a generator that yields an empty vector causes the
    // semantic stage to be skipped and leaves lexical results intact.
    return new NoEmbeddingQueryEmbeddingGenerator();
    }

    public void Dispose()
    {
        (_queryEmbeddingGenerator as IDisposable)?.Dispose();
        _rerankerClient?.Dispose();
        _symbolIdParser.Dispose();
        _textParser.Dispose();
        _analyzer.Dispose();
        _reader.Dispose();
        _directory.Dispose();
    }

    private readonly record struct LexicalMatch(string ItemId, Document Document, float Score);

    private sealed class Candidate
    {
        private double _lexicalScore;
        private double _semanticScore;
        private double _rrfScore;

        public Candidate(Document document)
        {
            Document = document;
        }

        public Document Document { get; }
        public bool HasLexical { get; private set; }
        public bool HasSemantic { get; private set; }
        public double FinalScore { get; private set; }

        /// <summary>Already min-max normalized to [0,1] by the caller.</summary>
        public void SetLexical(double normalizedScore)
        {
            HasLexical = true;
            _lexicalScore = normalizedScore;
        }

        /// <summary>Already min-max normalized to [0,1] by the caller.</summary>
        public void SetSemantic(double normalizedScore)
        {
            HasSemantic = true;
            _semanticScore = normalizedScore;
        }

        public void AddRrfScore(double contribution, bool fromLexical)
        {
            _rrfScore += contribution;
            if (fromLexical)
            {
                HasLexical = true;
            }
            else
            {
                HasSemantic = true;
            }
        }

        public void ComputeWeightedScore(double lexicalWeight, double semanticWeight)
        {
            FinalScore = (HasLexical ? _lexicalScore * lexicalWeight : 0.0)
                       + (HasSemantic ? _semanticScore * semanticWeight : 0.0);
        }

        public void ComputeRrfScore()
        {
            FinalScore = _rrfScore;
        }
    }
}