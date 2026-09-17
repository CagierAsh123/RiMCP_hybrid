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