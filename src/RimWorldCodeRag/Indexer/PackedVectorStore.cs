using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Indexer;

/// <summary>One metadata row of the packed vector index.</summary>
internal sealed record VectorMetaRow(string ItemId, string SymbolId, string Path);

/// <summary>
/// The metadata half of a packed vector index (header + <c>vectors.meta.jsonl</c>), without touching
/// the float payload. Reading this is what makes incremental embedding possible: it tells us which
/// item ids already have a vector.
/// </summary>
internal sealed record VectorManifest(int Dimensions, long Count, IReadOnlyList<VectorMetaRow> Rows)
{
    private HashSet<string>? _ids;

    public bool Contains(string itemId) => (_ids ??= new HashSet<string>(Rows.Select(r => r.ItemId), StringComparer.OrdinalIgnoreCase)).Contains(itemId);

    /// <summary>Returns null when the directory does not hold a packed index.</summary>
    public static VectorManifest? TryRead(string directory)
    {
        var binPath = Path.Combine(directory, VectorBinaryFormat.BinFileName);
        var metaPath = Path.Combine(directory, VectorBinaryFormat.MetaFileName);
        if (!File.Exists(binPath) || !File.Exists(metaPath))
        {
            return null;
        }

        int dimensions;
        long count;
        using (var bin = File.OpenRead(binPath))
        {
            (dimensions, count) = VectorBinaryFormat.ReadHeader(bin);
        }

        var rows = new List<VectorMetaRow>((int)Math.Min(count, int.MaxValue));
        foreach (var line in File.ReadLines(metaPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var itemId = root.TryGetProperty("itemId", out var itemIdProp) ? itemIdProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                rows.Add(new VectorMetaRow(
                    itemId!,
                    root.TryGetProperty("symbolId", out var symbolProp) ? symbolProp.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty));
            }
            catch (JsonException)
            {
                // Skip malformed rows; the row/offset arithmetic for copying uses Rows.Count, so a
                // skipped row would desynchronise the copy. Bail out instead of corrupting the file.
                throw new InvalidDataException($"'{metaPath}' contains a malformed metadata row; repack with --force embed.");
            }
        }

        return new VectorManifest(dimensions, count, rows);
    }
}

/// <summary>
/// Streaming writer for the packed vector index.
///
/// <para>
/// Writes to <c>.tmp</c> files and swaps them in on <see cref="Finish"/>, so a reader never sees a
/// partial index (a half-written file has a placeholder header with <c>dim = 0</c> and makes the MCP
/// server fail to start). Used for both a full re-embed and an incremental append.
/// </para>
/// </summary>
internal sealed class PackedVectorWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _binTemp;
    private readonly string _metaTemp;
    private readonly FileStream _bin;
    private readonly StreamWriter _meta;
    private readonly StringBuilder _buffer = new(256);
    private int _dimensions;
    private long _count;
    private bool _finished;

    public PackedVectorWriter(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _binTemp = Path.Combine(directory, VectorBinaryFormat.BinFileName + ".tmp");
        _metaTemp = Path.Combine(directory, VectorBinaryFormat.MetaFileName + ".tmp");

        _bin = new FileStream(_binTemp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        VectorBinaryFormat.WriteHeader(_bin, 0, 0);
        _meta = new StreamWriter(_metaTemp, append: false, new UTF8Encoding(false), 1 << 20);
    }

    public int Dimensions => _dimensions;

    public long Count => _count;

    /// <summary>Append one row. Throws when the dimension differs from the first row written.</summary>
    public void Append(string itemId, string symbolId, string path, ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
        {
            throw new ArgumentException("vector must not be empty", nameof(vector));
        }

        if (_dimensions == 0)
        {
            _dimensions = vector.Length;
        }
        else if (vector.Length != _dimensions)
        {
            throw new InvalidOperationException(
                $"vector dimension {vector.Length} != {_dimensions} already written to this file");
        }

        _bin.Write(MemoryMarshal.AsBytes(vector));

        _buffer.Clear();
        _buffer.Append("{\"itemId\":");
        AppendJsonString(_buffer, itemId);
        _buffer.Append(",\"symbolId\":");
        AppendJsonString(_buffer, symbolId);
        _buffer.Append(",\"path\":");
        AppendJsonString(_buffer, path);
        _buffer.Append('}');
        _meta.WriteLine(_buffer.ToString());

        _count++;
    }

    /// <summary>Patch the header and swap both temp files into place. Required for the write to count.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _meta.Flush();
        _bin.Position = 0;
        if (_dimensions > 0)
        {
            VectorBinaryFormat.WriteHeader(_bin, _dimensions, _count);
        }

        _bin.Flush(flushToDisk: true);
        _bin.Dispose();
        _meta.Dispose();

        File.Move(_binTemp, Path.Combine(_directory, VectorBinaryFormat.BinFileName), overwrite: true);
        File.Move(_metaTemp, Path.Combine(_directory, VectorBinaryFormat.MetaFileName), overwrite: true);
        _finished = true;
    }

    public void Dispose()
    {
        if (_finished)
        {
            return;
        }

        try { _bin.Dispose(); } catch (IOException) { /* already gone */ }
        try { _meta.Dispose(); } catch (IOException) { /* already gone */ }

        foreach (var temp in new[] { _binTemp, _metaTemp })
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Copy the rows of an existing packed index whose item id is still current, streaming the float
    /// payload in place (no full-file buffering).
    /// </summary>
    public static long CopyKeptRows(
        PackedVectorWriter writer,
        string sourceDirectory,
        VectorManifest manifest,
        IReadOnlyCollection<string> keepItemIds)
    {
        var binPath = Path.Combine(sourceDirectory, VectorBinaryFormat.BinFileName);
        var rowBytes = manifest.Dimensions * sizeof(float);
        var buffer = new float[manifest.Dimensions];
        long dropped = 0;

        using var bin = File.OpenRead(binPath);
        bin.Position = VectorBinaryFormat.HeaderSize;

        foreach (var row in manifest.Rows)
        {
            if (!keepItemIds.Contains(row.ItemId))
            {
                bin.Seek(rowBytes, SeekOrigin.Current);
                dropped++;
                continue;
            }

            bin.ReadExactly(MemoryMarshal.AsBytes(buffer.AsSpan()));
            writer.Append(row.ItemId, row.SymbolId, row.Path, buffer);
        }

        return dropped;
    }
}
