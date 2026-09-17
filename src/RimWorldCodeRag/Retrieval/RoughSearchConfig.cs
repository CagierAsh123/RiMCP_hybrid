using System;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

public sealed class RoughSearchConfig
{
    public const int DefaultSemanticCandidates = 5;

    public required string LuceneIndexPath { get; init; }
    public required string VectorIndexPath { get; init; }
    public int MaxResults { get; init; } = 20;
    public int LexicalCandidates { get; init; } = 1000;
    public int SemanticCandidates { get; init; } = DefaultSemanticCandidates;
    public bool UseSemanticScoringOnly { get; init; } = true;

    /// <summary>
    /// Path exclusion rules applied on the read side (vector load + final results).
    /// When null the rules are resolved from <c>&lt;index root&gt;\exclude.json</c>.
    /// </summary>
    public PathExclusionFilter? ExclusionFilter { get; init; }

    /// <summary>
    /// Collapse results that share a <c>SymbolId</c> (e.g. the same mod shipped in two folders)
    /// down to a single, best-ranked entry.
    /// </summary>
    public bool DedupeBySymbolId { get; init; } = true;

    /// <summary>Weight of the min-max normalized lexical score in the fused ranking.</summary>
    public double LexicalWeight { get; init; } = 0.5;

    /// <summary>Weight of the min-max normalized semantic score in the fused ranking.</summary>
    public double SemanticWeight { get; init; } = 0.5;

    /// <summary>
    /// Score fusion: <c>WeightedSum</c> (min-max normalize each leg, then weighted sum) or
    /// <c>Rrf</c> (reciprocal rank fusion, scale-free). <c>UseSemanticScoringOnly</c> overrides both.
    /// </summary>
    public FusionMode Fusion { get; init; } = FusionMode.WeightedSum;

    /// <summary>
    /// Expand a query that names a mod with that mod's identifiers (task 1.6).
    ///
    /// <para>
    /// Measured: this <b>does not help</b> with lexical candidates. BM25 combines terms with OR, so
    /// appending ~30 terms dilutes the original terms' share of the score and the target chunk's rank
    /// drops (absent items 12 -> 13, MRR 0.4042 -> 0.4021). Kept as an off-by-default experiment;
    /// prefer <see cref="ModPathBoost"/>.
    /// </para>
    /// </summary>
    public ModExpansionMode ModExpansion { get; init; } = ModExpansionMode.None;

    /// <summary>
    /// Bonus added to the semantic score of candidates whose path lies inside a mod named in the
    /// query (metadata filtering without a second scan). This is the variant that helps:
    /// <c>Blend(framework, neutral)</c>.
    /// </summary>
    public double ModPathBoost { get; init; } = 0.0;

    /// <summary>Maximum number of mods detected in one query.</summary>
    public int MaxModMatches { get; init; } = 2;

    /// <summary>Cap on the number of expansion terms appended to a query.</summary>
    public int MaxExpansionTerms { get; init; } = 24;

    /// <summary>Mod alias table; when null it is loaded from the index root.</summary>
    public ModCatalog? ModCatalog { get; init; }

    /// <summary>
    /// Cross-encoder reranker endpoint (<c>python/rerank_server.py</c>). Null disables reranking.
    /// </summary>
    public string? RerankServerUrl { get; init; }

    /// <summary>
    /// How many fused candidates to rerank. <b>0 disables reranking</b> — the default, because the
    /// plan is explicit that a reranker only graduates to default-on once the benchmark shows a gain.
    /// </summary>
    public int RerankCandidates { get; init; }

    public string? Kind { get; init; }

    public string? EmbeddingServerUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }

    public string? PythonExecutablePath { get; init; }
    public string? PythonScriptPath { get; init; }
    public string? ModelPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LuceneIndexPath))
        {
            throw new ArgumentException("LuceneIndexPath is required", nameof(LuceneIndexPath));
        }

        if (string.IsNullOrWhiteSpace(VectorIndexPath))
        {
            throw new ArgumentException("VectorIndexPath is required", nameof(VectorIndexPath));
        }

        if (MaxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResults), MaxResults, "MaxResults must be greater than zero.");
        }

        if (LexicalCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LexicalCandidates), LexicalCandidates, "LexicalCandidates must be greater than zero.");
        }

        if (SemanticCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticCandidates), SemanticCandidates, "SemanticCandidates must be greater than zero.");
        }
    }
}