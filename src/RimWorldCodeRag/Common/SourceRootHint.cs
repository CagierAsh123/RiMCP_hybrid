using System;
using System.IO;

namespace RimWorldCodeRag.Common;

/// <summary>
/// The source-tree location, recorded next to the index by the indexer.
///
/// <para>
/// Tools that read the source tree directly (the <c>grep</c> tool) need the root, but the MCP
/// server configuration only carries the index root. Persisting it here means the tool is
/// self-configuring and cannot drift from the index it belongs to.
/// </para>
/// </summary>
public static class SourceRootHint
{
    public const string FileName = "source-root.txt";

    /// <summary>
    /// Resolve the source root: explicit <c>RIMWORLD_SOURCE_ROOT</c> wins, then the recorded hint,
    /// then a conventional default.
    /// </summary>
    public static string Resolve(string indexRoot, string fallback = @"B:\rimworld-code\_SourceCode")
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("RIMWORLD_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var hintPath = Path.Combine(indexRoot, "meta", FileName);
        if (File.Exists(hintPath))
        {
            try
            {
                var recorded = File.ReadAllText(hintPath).Trim();
                if (!string.IsNullOrWhiteSpace(recorded))
                {
                    return recorded;
                }
            }
            catch (IOException)
            {
                // fall through to the default
            }
        }

        return fallback;
    }
}
