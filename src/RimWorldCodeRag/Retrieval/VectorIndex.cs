using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

internal sealed class VectorIndex
{
    private readonly List<VectorIndexEntry> _entries;
    private readonly Dictionary<string, VectorIndexEntry> _byItemId;

    private VectorIndex(List<VectorIndexEntry> entries)
    {
        _entries = entries;
        _byItemId = new Dictionary<string, VectorIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            _byItemId[entry.ItemId] = entry;
        }
    }

    public static VectorIndex Load(string directory)
        => Load(directory, null);

    /// <summary>
    /// Load the vector index. <paramref name="exclusionFilter"/> drops entries whose source path
    /// is excluded; when null the rules are resolved from the index root
    /// (see <see cref="PathExclusionFilter"/>). Filtering here means newly excluded directories
    /// stop being retrieved without paying for a re-embedding.
    ///
    /// <para>
    /// The packed <c>vectors.bin</c> + <c>vectors.meta.jsonl</c> pair is preferred; the legacy
    /// <c>vectors.jsonl</c> is still readable so an old index keeps working.
    /// </para>
    /// </summary>
    public static VectorIndex Load(string directory, PathExclusionFilter? exclusionFilter)
    {
        var filter = exclusionFilter ?? PathExclusionFilter.LoadForIndex(directory);

        var binPath = Path.Combine(directory, VectorBinaryFormat.BinFileName);
        var metaPath = Path.Combine(directory, VectorBinaryFormat.MetaFileName);
        var legacyPath = Path.Combine(directory, VectorBinaryFormat.LegacyJsonlFileName);

        if (File.Exists(binPath) && File.Exists(metaPath))
        {
            return LoadPacked(binPath, metaPath, filter);
        }

        if (File.Exists(legacyPath))
        {
            return LoadLegacyJsonl(legacyPath, filter);
        }

        // Fail loudly. Silently returning an empty index turns a misconfigured or half-built index
        // into "every search returns nothing", which looks like a retrieval-quality problem and
        // wastes hours of debugging.
        throw new FileNotFoundException(
            $"No vector index in '{directory}'. Expected '{VectorBinaryFormat.BinFileName}' + " +
            $"'{VectorBinaryFormat.MetaFileName}', or the legacy '{VectorBinaryFormat.LegacyJsonlFileName}'. " +
            "Rebuild with: index --root <source> --vec <dir> --force embed");
    }

    /// <summary>True when either on-disk format is present.</summary>
    public static bool Exists(string directory)
        => (File.Exists(Path.Combine(directory, VectorBinaryFormat.BinFileName)) &&
            File.Exists(Path.Combine(directory, VectorBinaryFormat.MetaFileName))) ||
           File.Exists(Path.Combine(directory, VectorBinaryFormat.LegacyJsonlFileName));

    /// <summary>
    /// Read the packed format: one bulk read of the float payload (a single allocation, sliced
    /// per entry with zero copies) plus a small JSONL sidecar.
    /// </summary>
    private static VectorIndex LoadPacked(string binPath, string metaPath, PathExclusionFilter filter)
    {
        using var bin = File.OpenRead(binPath);
        var (dimensions, count) = VectorBinaryFormat.ReadHeader(bin);

        var expectedLength = VectorBinaryFormat.ExpectedLength(dimensions, count);
        if (bin.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"'{binPath}' is {bin.Length} bytes but the header implies {expectedLength} ({VectorBinaryFormat.Describe(dimensions, count)}).");
        }

        var floats = new float[count * dimensions];
        bin.ReadExactly(MemoryMarshal.AsBytes(floats.AsSpan()));

        var entries = new List<VectorIndexEntry>((int)count);
        var skipped = 0;
        var row = 0;
        var offset = 0;

        foreach (var line in File.ReadLines(metaPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (row >= count)
            {
                Console.Error.WriteLine($"[vector-index] '{metaPath}' has more rows than the {count} declared in the header; ignoring the extras.");
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var itemId = root.TryGetProperty("itemId", out var itemIdProp) ? itemIdProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    row++;
                    offset += dimensions;
                    continue;
                }

                var symbolId = root.TryGetProperty("symbolId", out var symbolIdProp) ? symbolIdProp.GetString() ?? string.Empty : string.Empty;
                var pathValue = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;

                if (filter.IsExcluded(pathValue))
                {
                    skipped++;
                }
                else
                {
                    entries.Add(new VectorIndexEntry
                    {
                        ItemId = itemId!,
                        SymbolId = symbolId,
                        Path = pathValue,
                        Vector = new ReadOnlyMemory<float>(floats, offset, dimensions)
                    });
                }
            }
            catch
            {
                // Skip malformed rows but keep the row/offset arithmetic in sync with the bin.
            }

            row++;
            offset += dimensions;
        }

        ReportLoad(entries.Count, skipped, row, count, dimensions, filter, binPath);
        return new VectorIndex(entries);
    }

    /// <summary>Legacy reader for the original one-JSON-object-per-line format.</summary>
    private static VectorIndex LoadLegacyJsonl(string path, PathExclusionFilter filter)
    {
        if (!File.Exists(path))
        {
            return new VectorIndex(new List<VectorIndexEntry>());
        }

        var entries = new List<VectorIndexEntry>();
        var skipped = 0;

        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("itemId", out var itemIdProp))
                {
                    continue;
                }

                var itemId = itemIdProp.GetString();
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                var symbolId = root.TryGetProperty("symbolId", out var symbolIdProp) ? symbolIdProp.GetString() ?? string.Empty : string.Empty;
                var pathValue = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;

                if (filter.IsExcluded(pathValue))
                {
                    skipped++;
                    continue;
                }

                var vector = Array.Empty<float>();
                if (root.TryGetProperty("vector", out var vectorProp) && vectorProp.ValueKind == JsonValueKind.Array)
                {
                    vector = vectorProp
                        .EnumerateArray()
                        .Select(e => e.ValueKind switch
                        {
                            JsonValueKind.Number when e.TryGetSingle(out var f) => f,
                            JsonValueKind.Number when e.TryGetDouble(out var d) => (float)d,
                            _ => 0f
                        })
                        .ToArray();
                }

                entries.Add(new VectorIndexEntry
                {
                    ItemId = itemId!,
                    SymbolId = symbolId,
                    Path = pathValue,
                    Vector = vector
                });
            }
            catch
            {
                // Skip malformed entries but continue loading the remainder of the index.
            }
        }

        ReportLoad(entries.Count, skipped, entries.Count + skipped, entries.Count + skipped, 0, filter, path);
        return new VectorIndex(entries);
    }

    private static void ReportLoad(
        int loaded,
        int skipped,
        long rowsRead,
        long declaredCount,
        int dimensions,
        PathExclusionFilter filter,
        string source)
    {
        var shape = dimensions > 0 ? VectorBinaryFormat.Describe(dimensions, declaredCount) : string.Empty;
        Console.Error.WriteLine(
            $"[vector-index] loaded {loaded} vectors{(shape.Length > 0 ? $" ({shape})" : string.Empty)} from {Path.GetFileName(source)}, " +
            $"skipped {skipped} excluded by path rules ({filter.Source}); rows read {rowsRead}/{declaredCount}");

        if (declaredCount > 0 && rowsRead < declaredCount)
        {
            Console.Error.WriteLine($"[vector-index] WARNING: '{source}' is missing {declaredCount - rowsRead} metadata row(s).");
        }
    }

    public IReadOnlyList<VectorIndexEntry> Entries => _entries;

    public int VectorDimensions => _entries.Count == 0 ? 0 : _entries[0].Vector.Length;

    public VectorIndexEntry? GetById(string itemId)
    {
        return _byItemId.TryGetValue(itemId, out var entry) ? entry : null;
    }

    public IReadOnlyList<VectorMatch> FindNearest(float[] queryVector, int k)
    {
        if (queryVector.Length == 0 || _entries.Count == 0)
        {
            return Array.Empty<VectorMatch>();
        }

        var matches = new List<VectorMatch>(_entries.Count);
        foreach (var entry in _entries)
        {
            if (entry.Vector.Length != queryVector.Length)
            {
                continue;
            }

            var score = DotProduct(queryVector, entry.Vector);
            matches.Add(new VectorMatch(entry, score));
        }

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (matches.Count > k)
        {
            matches.RemoveRange(k, matches.Count - k);
        }

        return matches;
    }

    private static float DotProduct(float[] a, ReadOnlyMemory<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0f;
        }

#if NET6_0_OR_GREATER
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
}

internal sealed record VectorIndexEntry
{
    public required string ItemId { get; init; }
    public required string SymbolId { get; init; }
    public required string Path { get; init; }

    /// <summary>
    /// A slice of the single backing float array loaded from <c>vectors.bin</c> (zero copy), or a
    /// freshly allocated array for the legacy JSONL path.
    /// </summary>
    public required ReadOnlyMemory<float> Vector { get; init; }
}

internal readonly record struct VectorMatch(VectorIndexEntry Entry, float Score);
