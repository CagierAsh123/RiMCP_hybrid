using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Search;

/// <summary>What to look for, and where.</summary>
public sealed class GrepOptions
{
    public required string SourceRoot { get; init; }

    /// <summary>Literal text, or a .NET regex when <see cref="IsRegex"/> is set.</summary>
    public required string Pattern { get; init; }

    public bool IsRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;

    /// <summary>File-name glob such as <c>*.cs</c>. Null searches the default set.</summary>
    public string? Glob { get; init; }

    /// <summary>Substring that must appear in the path — the cheap way to scope to one mod.</summary>
    public string? PathFilter { get; init; }

    public int MaxResults { get; init; } = 50;
    public int ContextLines { get; init; } = 2;

    /// <summary>Per-file match cap, so one huge generated file cannot eat the whole budget.</summary>
    public int MaxMatchesPerFile { get; init; } = 20;

    public long MaxFileBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// Search the translation trees too. Off by default: <c>Languages/</c> and <c>DefInjected/</c>
    /// hold thousands of localisation files that dwarf the source and are never what a code search
    /// wants. <c>About/</c> and <c>Patches/</c> stay in, since mod metadata and patch XML are real
    /// answers.
    /// </summary>
    public bool IncludeTranslations { get; init; }

    public PathExclusionFilter? Exclusion { get; init; }
}

public sealed record GrepMatch(
    string Path,
    int Line,
    int Column,
    string Text,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After);

public sealed record GrepResult(
    IReadOnlyList<GrepMatch> Matches,
    int FilesScanned,
    int FilesMatched,
    bool Truncated,
    double ElapsedMs,
    string? Error);

/// <summary>
/// Literal / regex search over the decompiled source tree (plan task 3.1).
///
/// <para>
/// Why this exists next to a vector index: embeddings are good at <i>meaning</i> and bad at
/// <i>exact strings</i>. "Where is the literal <c>rjw.JobDriver_Sex</c> referenced", "which XML
/// files set &lt;workerClass&gt;", "find every <c>[HarmonyPatch]</c> on a method" are all cheap and
/// exact with a text scan, and unreliable through cosine similarity.
/// </para>
///
/// <para>
/// It is implemented in managed code rather than by shelling out to ripgrep so the tool has no
/// external dependency and reuses the very same <see cref="PathExclusionFilter"/> the index uses —
/// a grep that returns excluded mods would contradict the index.
/// </para>
/// </summary>
public static class GrepSearcher
{
    private static readonly string[] DefaultGlobs = { "*.cs", "*.xml" };

    /// <summary>Directories that only hold localisation data.</summary>
    private static readonly string[] TranslationDirectories = { "Languages", "DefInjected" };

    public static GrepResult Search(GrepOptions options)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(options.Pattern))
        {
            return new GrepResult(Array.Empty<GrepMatch>(), 0, 0, false, 0, "pattern must not be empty");
        }

        if (!Directory.Exists(options.SourceRoot))
        {
            return new GrepResult(
                Array.Empty<GrepMatch>(), 0, 0, false, stopwatch.Elapsed.TotalMilliseconds,
                $"source root '{options.SourceRoot}' does not exist");
        }

        Regex? regex;
        try
        {
            regex = BuildRegex(options);
        }
        catch (ArgumentException ex)
        {
            return new GrepResult(Array.Empty<GrepMatch>(), 0, 0, false, stopwatch.Elapsed.TotalMilliseconds, $"invalid regex: {ex.Message}");
        }

        var exclusion = options.Exclusion ?? PathExclusionFilter.Empty;
        var matches = new ConcurrentBag<GrepMatch>();
        var filesScanned = 0;
        var filesMatched = 0;
        var truncated = false;

        var files = EnumerateFiles(options, exclusion).ToArray();

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (file, state) =>
            {
                if (matches.Count >= options.MaxResults)
                {
                    state.Stop();
                    return;
                }

                Interlocked.Increment(ref filesScanned);

                List<GrepMatch>? fileMatches;
                try
                {
                    fileMatches = ScanFile(file, regex, options);
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }

                if (fileMatches is null || fileMatches.Count == 0)
                {
                    return;
                }

                Interlocked.Increment(ref filesMatched);
                foreach (var match in fileMatches)
                {
                    if (matches.Count >= options.MaxResults)
                    {
                        truncated = true;
                        state.Stop();
                        return;
                    }

                    matches.Add(match);
                }
            });

        stopwatch.Stop();

        return new GrepResult(
            matches.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Line).ToList(),
            filesScanned,
            filesMatched,
            truncated,
            stopwatch.Elapsed.TotalMilliseconds,
            null);
    }

    private static Regex BuildRegex(GrepOptions options)
    {
        var pattern = options.IsRegex ? options.Pattern : Regex.Escape(options.Pattern);
        var flags = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (options.IgnoreCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, flags, TimeSpan.FromSeconds(5));
    }

    private static IEnumerable<string> EnumerateFiles(GrepOptions options, PathExclusionFilter exclusion)
    {
        var globs = string.IsNullOrWhiteSpace(options.Glob) ? DefaultGlobs : new[] { options.Glob! };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Fast path: when the path filter names a directory under the root, walk only that subtree.
        // Otherwise a scoped search still pays for enumerating ~25k files across every mod.
        var scopeRoots = new List<string> { options.SourceRoot };
        if (!string.IsNullOrWhiteSpace(options.PathFilter))
        {
            var candidate = Path.Combine(options.SourceRoot, options.PathFilter!);
            if (Directory.Exists(candidate))
            {
                scopeRoots.Clear();
                scopeRoots.Add(candidate);
            }
        }

        foreach (var glob in globs)
        {
            foreach (var scopeRoot in scopeRoots)
            {
                IEnumerable<string> found;
                try
                {
                    found = Directory.EnumerateFiles(scopeRoot, glob, SearchOption.AllDirectories);
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var file in found)
                {
                    if (!seen.Add(file))
                    {
                        continue;
                    }

                    if (exclusion.IsExcluded(file))
                    {
                        continue;
                    }

                    if (!options.IncludeTranslations && IsUnderTranslations(file))
                    {
                        continue;
                    }

                    // With a narrow scope the directory already matched; the filter still has to
                    // hold so nested hits stay consistent with the unscoped case.
                    if (!string.IsNullOrWhiteSpace(options.PathFilter) &&
                        file.IndexOf(options.PathFilter!, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }
    }

    private static bool IsUnderTranslations(string path)
        => TranslationDirectories.Any(directory =>
            path.Contains($"{Path.DirectorySeparatorChar}{directory}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static List<GrepMatch>? ScanFile(string path, Regex regex, GrepOptions options)
    {
        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > options.MaxFileBytes)
        {
            return null;
        }

        var text = File.ReadAllText(path);
        if (text.Length == 0)
        {
            return null;
        }

        var lineStarts = BuildLineStarts(text);
        var results = new List<GrepMatch>();

        foreach (Match match in regex.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            var lineIndex = LineIndexOf(lineStarts, match.Index);
            var lineStart = lineStarts[lineIndex];
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var lineText = text[lineStart..lineEnd].TrimEnd('\r');

            results.Add(new GrepMatch(
                path,
                lineIndex + 1,
                match.Index - lineStart + 1,
                lineText,
                ContextBefore(text, lineStarts, lineIndex, options.ContextLines),
                ContextAfter(text, lineStarts, lineIndex, options.ContextLines)));

            if (results.Count >= options.MaxMatchesPerFile)
            {
                break;
            }
        }

        return results.Count == 0 ? null : results;
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static int LineIndexOf(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static string LineText(string text, int[] lineStarts, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lineStarts.Length)
        {
            return string.Empty;
        }

        var start = lineStarts[lineIndex];
        var end = lineIndex + 1 < lineStarts.Length ? lineStarts[lineIndex + 1] : text.Length;
        return text[start..end].TrimEnd('\r', '\n');
    }

    private static IReadOnlyList<string> ContextBefore(string text, int[] lineStarts, int lineIndex, int count)
    {
        var lines = new List<string>();
        for (var i = Math.Max(0, lineIndex - count); i < lineIndex; i++)
        {
            lines.Add(LineText(text, lineStarts, i));
        }

        return lines;
    }

    private static IReadOnlyList<string> ContextAfter(string text, int[] lineStarts, int lineIndex, int count)
    {
        var lines = new List<string>();
        for (var i = lineIndex + 1; i <= Math.Min(lineStarts.Length - 1, lineIndex + count); i++)
        {
            lines.Add(LineText(text, lineStarts, i));
        }

        return lines;
    }
}
