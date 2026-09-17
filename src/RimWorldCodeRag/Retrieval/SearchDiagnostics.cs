using System.Collections.Generic;

namespace RimWorldCodeRag.Retrieval;

/// <summary>One raw candidate from a single retrieval leg, before fusion.</summary>
public sealed record SearchCandidate(string ItemId, string SymbolId, string Path, double Score);

/// <summary>
/// Where the candidates for one query came from.
///
/// <para>
/// The two legs are returned in rank order. Comparing their contents against the fused results
/// answers the question that decides the whole optimisation strategy:
/// </para>
/// <list type="bullet">
///   <item>item absent from both legs → a candidate-generation problem (embedding model / query phrasing); no amount of fusion or reranking will help.</item>
///   <item>item present in a leg but missing downstream → a ranking/fusion problem (weights, reranker).</item>
/// </list>
/// </summary>
public sealed record SearchDiagnostics(
    IReadOnlyList<SearchCandidate> Lexical,
    IReadOnlyList<SearchCandidate> Semantic,
    IReadOnlyList<RoughSearchResult> Results);
