using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Builds the <see cref="ModCatalog"/> alias/vocabulary table from indexed chunks.
///
/// <para>
/// Derived from the chunks rather than by re-parsing the source tree, so the namespaces and DefTypes
/// recorded are exactly the ones present in the index.
/// </para>
///
/// <para>
/// Terms that appear across most mods (<c>Verse</c>, <c>RimWorld</c>, <c>System.*</c>, <c>ThingDef</c>)
/// are dropped: they carry no discriminating power and expanding a query with them would drag
/// unrelated content in. Filtering is by document frequency across the mod set, so it needs no
/// hand-maintained stoplist and adapts as mods are added.
/// </para>
/// </summary>
public static class ModCatalogBuilder
{
    /// <summary>Top-level directories under the source root that are not mods.</summary>
    private static readonly string[] NonModDirectories = { "CSharp", "XML", "_meta" };

    /// <summary>Framework namespaces that are never a mod's identity.</summary>
    private static readonly string[] GenericNamespacePrefixes =
    {
        "System", "Microsoft", "Mono", "UnityEngine", "Unity", "Verse", "RimWorld",
        "HarmonyLib", "JetBrains", "Ionic", "DelaunatorSharp", "Gilzoide", "net"
    };

    /// <summary>A term must be rarer than this share of mods to count as discriminating.</summary>
    private const double MaxDocumentFrequencyRatio = 0.25;

    public static IReadOnlyList<ModEntry> Build(string sourceRoot, IReadOnlyList<ChunkRecord> chunks)
    {
        var byMod = new Dictionary<string, ModAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            var relative = Path.GetRelativePath(sourceRoot, chunk.Path);
            var separator = relative.IndexOfAny(new[] { '\\', '/' });
            var modDir = separator > 0 ? relative[..separator] : null;
            if (string.IsNullOrEmpty(modDir) ||
                NonModDirectories.Any(d => d.Equals(modDir, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!byMod.TryGetValue(modDir, out var accumulator))
            {
                accumulator = new ModAccumulator();
                byMod[modDir] = accumulator;
            }

            accumulator.Add(chunk);
        }

        var modCount = Math.Max(1, byMod.Count);
        var namespaceDf = DocumentFrequency(byMod.Values, a => a.Namespaces.Keys);
        var defTypeDf = DocumentFrequency(byMod.Values, a => a.DefTypes.Keys);
        var namespaceCutoff = Math.Max(3, (int)Math.Ceiling(modCount * MaxDocumentFrequencyRatio));
        var defTypeCutoff = Math.Max(4, (int)Math.Ceiling(modCount * MaxDocumentFrequencyRatio));

        var entries = new List<ModEntry>(byMod.Count);
        foreach (var (modDir, accumulator) in byMod
                     .OrderByDescending(kvp => kvp.Value.Total)
                     .ThenBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            // Expansion vocabulary: this mod's own namespaces and DefTypes, rarest-of-the-common dropped.
            var namespaces = accumulator.Namespaces
                .Where(kvp => !IsGenericNamespace(kvp.Key) && namespaceDf.GetValueOrDefault(kvp.Key) <= namespaceCutoff)
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .Take(8)
                .ToList();

            var defTypes = accumulator.DefTypes
                .Where(kvp => defTypeDf.GetValueOrDefault(kvp.Key) <= defTypeCutoff)
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .Take(10)
                .ToList();

            // Aliases: how a human names the mod. Directory name (the corpus convention is
            // "中文名（English Name）") plus the mod's single-segment namespace, which is usually the
            // identifier people actually type (e.g. NewRatkin for NewRatkinPlus).
            // Only the top one is taken: decompiled Harmony patches live in odd single-segment
            // namespaces ("PatchOperationTryAdd"), which would be nonsense aliases.
            var namespaceAlias = accumulator.Namespaces
                .Where(kvp => IsPlausibleNamespaceAlias(kvp.Key))
                .Where(kvp => namespaceDf.GetValueOrDefault(kvp.Key) <= namespaceCutoff)
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();

            var aliases = ModCatalog.SplitDirectoryNameAliases(modDir)
                .Concat(namespaceAlias is null ? Enumerable.Empty<string>() : new[] { namespaceAlias })
                .Where(a => a.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            entries.Add(new ModEntry
            {
                Dir = modDir,
                Aliases = aliases,
                Namespaces = namespaces,
                DefTypes = defTypes,
                DefNamePrefixes = accumulator.TopPrefixes(),
                ChunkCount = accumulator.Total
            });
        }

        return entries;
    }

    private static Dictionary<string, int> DocumentFrequency(
        IEnumerable<ModAccumulator> mods,
        Func<ModAccumulator, IEnumerable<string>> selector)
    {
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var term in selector(mod).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                df[term] = df.GetValueOrDefault(term) + 1;
            }
        }

        return df;
    }

    private static bool IsGenericNamespace(string ns)
        => GenericNamespacePrefixes.Any(prefix =>
            ns.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            ns.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>Fragments that show up in decompiler-generated helper namespaces, not mod identities.</summary>
    private static readonly string[] ImplausibleAliasFragments =
    {
        "Patch", "Operation", "Compatibility", "Extensions", "Helper", "Utility",
        "Settings", "Manager", "Diagnostic", "Collection", "Pool", "Attribute", "Converter"
    };

    private static bool IsPlausibleNamespaceAlias(string ns)
    {
        if (ns.IndexOf('.') >= 0 || ns.Length < 4 || ns.Length > 20 || !char.IsUpper(ns[0]) || IsGenericNamespace(ns))
        {
            return false;
        }

        return !ImplausibleAliasFragments.Any(f => ns.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ModAccumulator
    {
        private readonly Dictionary<string, int> _namespaces = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _defTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _prefixes = new(StringComparer.OrdinalIgnoreCase);

        public int Total { get; private set; }

        /// <summary>XML chunks only. DefName prefixes must be measured against this, not against
        /// Total: C#-heavy mods would otherwise have every prefix filtered out.</summary>
        public int XmlTotal { get; private set; }

        public Dictionary<string, int> Namespaces => _namespaces;

        public Dictionary<string, int> DefTypes => _defTypes;

        public void Add(ChunkRecord chunk)
        {
            Total++;

            if (!string.IsNullOrWhiteSpace(chunk.Namespace))
            {
                _namespaces[chunk.Namespace] = _namespaces.GetValueOrDefault(chunk.Namespace) + 1;
            }

            if (!string.IsNullOrWhiteSpace(chunk.DefType))
            {
                _defTypes[chunk.DefType] = _defTypes.GetValueOrDefault(chunk.DefType) + 1;
            }

            var prefix = ExtractDefNamePrefix(chunk.SymbolId);
            if (prefix is not null)
            {
                XmlTotal++;
                _prefixes[prefix] = _prefixes.GetValueOrDefault(prefix) + 1;
            }
        }

        /// <summary>
        /// XML symbol ids look like <c>xml:&lt;DefType&gt;:&lt;defName&gt;</c>. The leading
        /// ALL-CAPS/digits run before the first underscore is the mod's namespace-ish prefix:
        /// <c>HAR_AlienCorpseCategory</c> -> <c>HAR_</c>, <c>WCE2_Cut</c> -> <c>WCE2_</c>.
        /// Mixed-case prefixes (<c>Milira_ApparelPolicy</c>) are not treated as prefixes — the
        /// namespace alias already covers those.
        /// </summary>
        internal static string? ExtractDefNamePrefix(string symbolId)
        {
            if (!symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var lastColon = symbolId.LastIndexOf(':');
            if (lastColon <= 0 || lastColon + 1 >= symbolId.Length)
            {
                return null;
            }

            var defName = symbolId[(lastColon + 1)..];
            var underscore = defName.IndexOf('_');
            if (underscore < 2 || underscore >= defName.Length - 1)
            {
                return null;
            }

            for (var i = 0; i < underscore; i++)
            {
                if (!char.IsUpper(defName[i]) && !char.IsDigit(defName[i]))
                {
                    return null;
                }
            }

            return defName[..(underscore + 1)];
        }

        /// <summary>Prefixes used by at least 3 defs and at least 8% of this mod's XML defs.</summary>
        public List<string> TopPrefixes()
            => _prefixes
                .Where(kvp => kvp.Value >= 3 && kvp.Value >= XmlTotal * 0.08)
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .Take(5)
                .ToList();
    }
}
