using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimWorldCodeRag.Common;

/// <summary>
/// Alias / vocabulary table for the mods under the source root.
///
/// <para>
/// Why this exists: users and agents name a mod by its <b>display name</b> ("Humanoid Alien Races",
/// "外星人框架"), while the code and XML use completely different identifiers
/// (<c>AlienRace</c>, <c>HAR_</c>). Measured on <c>tests/retrieval-baseline.json</c>, 6 of the
/// 12 expected items that <b>neither retrieval leg</b> could produce were exactly this
/// vocabulary mismatch (<c>docs/rag-upgrade-plan-2026-09.md</c> §11.3).
/// </para>
///
/// <para>
/// The catalog maps a mod directory to the identifiers that actually appear in it, so the query can
/// be expanded with terms that exist in the index.
/// </para>
/// </summary>
public sealed class ModCatalog
{
    public const string FileName = "mods.json";

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

    private readonly List<ModEntry> _mods;

    private ModCatalog(List<ModEntry> mods, string source)
    {
        _mods = mods;
        Source = source;
    }

    public static ModCatalog Empty { get; } = new(new List<ModEntry>(), "none");

    public string Source { get; }

    public IReadOnlyList<ModEntry> Mods => _mods;

    public bool IsEmpty => _mods.Count == 0;

    /// <summary>
    /// Mods whose alias appears in the query, longest alias first (so "RimTalk - Expand Memory"
    /// beats "RimTalk").
    /// </summary>
    public IReadOnlyList<ModEntry> Detect(string query, int maxMatches = 2)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ModEntry>();
        }

        var hits = new List<(ModEntry Mod, int AliasLength)>();
        foreach (var mod in _mods)
        {
            var best = 0;
            foreach (var alias in mod.Aliases)
            {
                if (alias.Length >= 3 && query.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, alias.Length);
                }
            }

            if (best > 0)
            {
                hits.Add((mod, best));
            }
        }

        return hits
            .OrderByDescending(h => h.AliasLength)
            .Take(Math.Max(1, maxMatches))
            .Select(h => h.Mod)
            .ToList();
    }

    public static ModCatalog LoadForIndex(string vectorIndexPath)
        => Load(PathExclusionFilter.ResolveIndexRoot(vectorIndexPath));

    public static ModCatalog Load(string indexRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(indexRootDirectory))
        {
            return Empty;
        }

        var path = Path.Combine(indexRootDirectory, FileName);
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ModCatalogDocument>(File.ReadAllText(path), ReadOptions);
            return document?.Mods is null
                ? Empty
                : new ModCatalog(document.Mods, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mod-catalog] failed to read {path}: {ex.Message}");
            return Empty;
        }
    }

    public static void Save(string indexRootDirectory, IEnumerable<ModEntry> mods)
    {
        Directory.CreateDirectory(indexRootDirectory);
        var path = Path.Combine(indexRootDirectory, FileName);
        var document = new ModCatalogDocument { Version = 1, Mods = mods.ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(document, WriteOptions));
    }

    /// <summary>
    /// Split a mod directory name into aliases. The convention in this corpus is
    /// <c>中文名（English Name）</c>, so both halves are usable, plus the English half with the
    /// trailing parenthetical of its own.
    /// </summary>
    public static IEnumerable<string> SplitDirectoryNameAliases(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            yield break;
        }

        yield return directoryName.Trim();

        var halves = directoryName.Split('（', '(');
        foreach (var half in halves)
        {
            foreach (var piece in half.Split('）', ')', '|', '/'))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length >= 3)
                {
                    yield return trimmed;
                }
            }
        }
    }

    private sealed class ModCatalogDocument
    {
        public int Version { get; set; }
        public List<ModEntry> Mods { get; set; } = new();
    }
}

/// <summary>One mod: how it can be named, and the identifiers its chunks actually use.</summary>
public sealed class ModEntry
{
    /// <summary>Top-level directory name under the source root (the path key).</summary>
    public string Dir { get; set; } = string.Empty;

    /// <summary>Strings that identify this mod inside a query.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>C# namespaces used by this mod's chunks.</summary>
    public List<string> Namespaces { get; set; } = new();

    /// <summary>XML DefTypes (element names) this mod declares.</summary>
    public List<string> DefTypes { get; set; } = new();

    /// <summary>Common defName prefixes (e.g. <c>HAR_</c>, <c>WCE2_</c>), most frequent first.</summary>
    public List<string> DefNamePrefixes { get; set; } = new();

    public int ChunkCount { get; set; }

    /// <summary>
    /// Terms appended to a query when this mod is named. DefTypes and namespaces are the useful
    /// hooks: they are what the indexed text and identifiers actually contain. The defName prefixes
    /// (HAR_, RK_, VF_) catch XML defs whose text does not mention the mod name at all.
    /// </summary>
    public IEnumerable<string> ExpansionTerms
    {
        get
        {
            foreach (var ns in Namespaces)
            {
                yield return ns;
            }

            foreach (var defType in DefTypes)
            {
                yield return defType;
            }

            foreach (var prefix in DefNamePrefixes)
            {
                yield return prefix.TrimEnd('_');
            }
        }
    }
}
