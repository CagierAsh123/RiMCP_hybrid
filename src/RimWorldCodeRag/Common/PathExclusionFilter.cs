using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimWorldCodeRag.Common;

/// <summary>
/// Path-based exclusion rules shared by the indexer (chunk production) and the retrieval
/// path (vector-index load + final result filtering).
///
/// <para>
/// Rules live in <c>exclude.json</c> at the <b>index root</b> (the directory that contains
/// the <c>lucene/</c> and <c>vec/</c> subdirectories), so the indexer and the searcher always
/// read the same list.
/// </para>
///
/// <para>
/// Matching is case-insensitive and performed against a normalized path
/// (<c>/</c> → <c>\</c>, leading <c>\</c> enforced). A rule containing <c>*</c> or <c>?</c>
/// is treated as a wildcard pattern; any other rule is a plain substring test. Rules without
/// a path separator are wrapped as a directory name, so <c>"rjw"</c> and <c>"\rjw\"</c> are
/// equivalent and neither matches <c>\rjwx\</c>.
/// </para>
/// </summary>
public sealed class PathExclusionFilter
{
    public const string FileName = "exclude.json";

    /// <summary>Rules applied when no <c>exclude.json</c> is present.</summary>
    public static readonly string[] DefaultRules = { @"\Develop\", @"\rjw\" };

    /// <summary>A filter that excludes nothing.</summary>
    public static PathExclusionFilter Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<CompiledPattern>(),
        Array.Empty<string>(),
        "none");

    private readonly string[] _needles;
    private readonly CompiledPattern[] _patterns;

    private PathExclusionFilter(string[] needles, CompiledPattern[] patterns, string[] rules, string source)
    {
        _needles = needles;
        _patterns = patterns;
        Rules = rules;
        Source = source;
        Signature = ComputeSignature(rules);
    }

    /// <summary>Where the effective rules came from (file path or <c>"default"</c>) — for diagnostics.</summary>
    public string Source { get; }

    /// <summary>The effective rules, after normalization.</summary>
    public IReadOnlyList<string> Rules { get; }

    /// <summary>Stable short hash of the effective rules; used to detect configuration drift.</summary>
    public string Signature { get; }

    public bool IsEmpty => _needles.Length == 0 && _patterns.Length == 0;

    public bool IsExcluded(string? path)
    {
        if (IsEmpty || string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Fast path: chunk paths are already Windows-style absolute paths with a leading
        // separator, so no string allocation is needed for the common case.
        var candidate = path;
        if (candidate.IndexOf('/') >= 0)
        {
            candidate = candidate.Replace('/', '\\');
        }

        if (candidate[0] != '\\')
        {
            candidate = "\\" + candidate;
        }

        foreach (var needle in _needles)
        {
            if (candidate.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        if (_patterns.Length > 0)
        {
            var lowered = candidate.ToLowerInvariant();
            foreach (var pattern in _patterns)
            {
                if (pattern.Pattern.IsMatch(lowered))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Human-readable one-liner for logs.</summary>
    public string Describe() =>
        IsEmpty
            ? $"exclusion: none ({Source})"
            : $"exclusion: [{string.Join(", ", Rules)}] from {Source}";

    /// <summary>
    /// Resolve the index root from any index artifact path. The convention is that the root
    /// contains <c>exclude.json</c> plus the <c>lucene/</c> / <c>vec/</c> / <c>meta/</c> directories,
    /// so <c>…\index\vec</c> resolves to <c>…\index</c>.
    /// </summary>
    public static string ResolveIndexRoot(string indexOrSubdirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(indexOrSubdirectoryPath))
        {
            return indexOrSubdirectoryPath;
        }

        var full = Path.GetFullPath(indexOrSubdirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var name = Path.GetFileName(full);
        if (name.Equals("vec", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("lucene", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("meta", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                return parent;
            }
        }

        return full;
    }

    /// <summary>Load the rules that apply to the index owning <paramref name="vectorIndexPath"/>.</summary>
    public static PathExclusionFilter LoadForIndex(string vectorIndexPath)
        => Load(ResolveIndexRoot(vectorIndexPath));

    /// <summary>Load rules from <c>&lt;indexRoot&gt;\exclude.json</c>, falling back to <see cref="DefaultRules"/>.</summary>
    public static PathExclusionFilter Load(string indexRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(indexRootDirectory))
        {
            return FromRules(DefaultRules, "default");
        }

        var path = Path.Combine(indexRootDirectory, FileName);
        if (!File.Exists(path))
        {
            return FromRules(DefaultRules, $"default (no {FileName})");
        }

        try
        {
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return FromRules(ReadStrings(root), path);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                {
                    return new PathExclusionFilter(Array.Empty<string>(), Array.Empty<CompiledPattern>(), Array.Empty<string>(), path + " (disabled)");
                }

                if (root.TryGetProperty("exclude", out var exclude) && exclude.ValueKind == JsonValueKind.Array)
                {
                    return FromRules(ReadStrings(exclude), path);
                }

                Console.Error.WriteLine($"[exclude] {path} has no 'exclude' array; falling back to defaults.");
                return FromRules(DefaultRules, path);
            }

            Console.Error.WriteLine($"[exclude] {path} has an unexpected JSON root ({root.ValueKind}); falling back to defaults.");
            return FromRules(DefaultRules, path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[exclude] failed to read {path}: {ex.Message}; falling back to defaults.");
            return FromRules(DefaultRules, path);
        }
    }

    /// <summary>
    /// Write a commented template to <c>&lt;indexRoot&gt;\exclude.json</c> if it does not exist.
    /// Returns the path when a file was created, otherwise <c>null</c>.
    /// </summary>
    public static string? EnsureFile(string indexRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(indexRootDirectory))
        {
            return null;
        }

        var path = Path.Combine(indexRootDirectory, FileName);
        if (File.Exists(path))
        {
            return null;
        }

        Directory.CreateDirectory(indexRootDirectory);
        const string template = """
        {
          "_comment": "Paths matching any rule are excluded from index building AND retrieval. Rules are matched case-insensitively against the normalized path (e.g. \\c:\\src\\mod\\file.cs). A rule without path separators is treated as a directory name (\"rjw\" == \"\\rjw\\\"); a rule containing * or ? is a wildcard pattern. Set \"enabled\": false to disable exclusion entirely.",
          "enabled": true,
          "exclude": [
            "\\Develop\\",
            "\\rjw\\"
          ]
        }
        """;

        File.WriteAllText(path, template + Environment.NewLine, new UTF8Encoding(false));
        return path;
    }

    private static IEnumerable<string> ReadStrings(JsonElement array)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static PathExclusionFilter FromRules(IEnumerable<string> rules, string source)
    {
        var normalized = rules
            .Select(NormalizeRule)
            .Where(rule => rule.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(rule => rule, StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            return new PathExclusionFilter(Array.Empty<string>(), Array.Empty<CompiledPattern>(), Array.Empty<string>(), source);
        }

        var wildcards = normalized.Where(IsWildcard).ToArray();
        var needles = normalized.Where(rule => !IsWildcard(rule)).ToArray();

        var patterns = wildcards
            .Select(rule => new CompiledPattern(rule, new Regex(WildcardToRegex(rule), RegexOptions.Compiled | RegexOptions.CultureInvariant)))
            .ToArray();

        return new PathExclusionFilter(needles, patterns, normalized, source);
    }

    private static bool IsWildcard(string rule) => rule.IndexOf('*') >= 0 || rule.IndexOf('?') >= 0;

    private static string NormalizeRule(string rule)
    {
        var normalized = rule.Replace('/', '\\').Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.IndexOf('\\') >= 0)
        {
            return normalized[0] == '\\' ? normalized : "\\" + normalized;
        }

        return "\\" + normalized + "\\";
    }

    private static string WildcardToRegex(string rule)
    {
        var builder = new StringBuilder(rule.Length + 8);
        foreach (var ch in rule)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString())
            });
        }

        return builder.ToString();
    }

    private static string ComputeSignature(IReadOnlyList<string> rules)
    {
        if (rules.Count == 0)
        {
            return "none";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rules)));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private sealed record CompiledPattern(string Rule, System.Text.RegularExpressions.Regex Pattern);
}
