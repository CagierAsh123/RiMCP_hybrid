namespace RimWorldCodeRag.Retrieval;

/// <summary>
/// Score-fusion strategy for combining the lexical (BM25) and semantic (cosine) legs.
/// </summary>
public enum FusionMode
{
    /// <summary>
    /// Min-max normalize each leg to [0,1], then <c>wLex * normLex + wSem * normSem</c>.
    /// The normalization step is the part the original "just add the scores" attempt was missing:
    /// BM25 and cosine live on different scales, so adding them raw scrambles the ranking
    /// (measured: MRR 0.3853 -> 0.2398, see docs/rag-upgrade-plan-2026-09.md §10.2).
    /// </summary>
    WeightedSum,

    /// <summary>Reciprocal rank fusion: <c>sum(w_i / (k + rank_i))</c>, k = 60. Scale-free.</summary>
    Rrf
}

/// <summary>
/// How a mod named in the query is turned into extra search terms (task 1.6).
/// </summary>
public enum ModExpansionMode
{
    /// <summary>Use the query verbatim.</summary>
    None,

    /// <summary>Expand only the lexical (BM25) leg.</summary>
    Lexical,

    /// <summary>Expand both legs — the embedding input also carries the mod's identifiers.</summary>
    Both
}

/// <summary>
/// Per-request search knobs.
///
/// <para>
/// Passing these per call — instead of mutating shared config or constructing a new
/// <see cref="RoughSearcher"/> — is what lets one loaded index serve concurrent requests that
/// differ in <c>kind</c> / <c>max</c>. Rebuilding the searcher re-parsed the 1.6 GB vector file
/// on every parameter change (measured 14.34 s).
/// </para>
///
/// <para>Null means "inherit from the searcher's <see cref="RoughSearchConfig"/>".</para>
/// </summary>
public sealed record RoughSearchOptions
{
    public string? Kind { get; init; }
    public int? MaxResults { get; init; }
    public int? LexicalCandidates { get; init; }
    public int? SemanticCandidates { get; init; }
    public bool? DedupeBySymbolId { get; init; }
    public bool? UseSemanticScoringOnly { get; init; }
    public double? LexicalWeight { get; init; }
    public double? SemanticWeight { get; init; }
    public FusionMode? Fusion { get; init; }
    public ModExpansionMode? ModExpansion { get; init; }
    public double? ModPathBoost { get; init; }
    public int? MaxModMatches { get; init; }
    public int? MaxExpansionTerms { get; init; }
    public int? RerankCandidates { get; init; }
    public string? RerankServerUrl { get; init; }

    public static RoughSearchOptions FromConfig(RoughSearchConfig config) => new()
    {
        Kind = config.Kind,
        MaxResults = config.MaxResults,
        LexicalCandidates = config.LexicalCandidates,
        SemanticCandidates = config.SemanticCandidates,
        DedupeBySymbolId = config.DedupeBySymbolId,
        UseSemanticScoringOnly = config.UseSemanticScoringOnly,
        LexicalWeight = config.LexicalWeight,
        SemanticWeight = config.SemanticWeight,
        Fusion = config.Fusion,
        ModExpansion = config.ModExpansion,
        ModPathBoost = config.ModPathBoost,
        MaxModMatches = config.MaxModMatches,
        MaxExpansionTerms = config.MaxExpansionTerms,
        RerankCandidates = config.RerankCandidates,
        RerankServerUrl = config.RerankServerUrl
    };
}
