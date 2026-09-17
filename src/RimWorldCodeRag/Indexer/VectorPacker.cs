using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Converts a legacy <c>vectors.jsonl</c> into the packed <c>vectors.bin</c> + <c>vectors.meta.jsonl</c>
/// pair (task 1.1) without re-running the embedding model.
///
/// <para>
/// This is what makes the format change affordable: re-embedding 144k chunks costs hours of GPU time,
/// while repacking the existing file costs a single linear pass.
/// </para>
/// </summary>
public static class VectorPacker
{
    public static PackResult Pack(string vectorDirectory, bool overwrite = false)
    {
        var jsonlPath = Path.Combine(vectorDirectory, VectorBinaryFormat.LegacyJsonlFileName);
        if (!File.Exists(jsonlPath))
        {
            throw new FileNotFoundException($"Legacy vector file not found: {jsonlPath}");
        }

        var binPath = Path.Combine(vectorDirectory, VectorBinaryFormat.BinFileName);
        var metaPath = Path.Combine(vectorDirectory, VectorBinaryFormat.MetaFileName);

        if (!overwrite && File.Exists(binPath) && File.Exists(metaPath))
        {
            throw new InvalidOperationException(
                $"'{binPath}' and '{metaPath}' already exist. Pass --overwrite to repack.");
        }

        // A placeholder header is written first and patched in place at the end, so the float
        // payload can be streamed straight to disk with no temp file and no full buffering.
        using var bin = new FileStream(binPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        VectorBinaryFormat.WriteHeader(bin, 0, 0);

        using var meta = new StreamWriter(metaPath, append: false, new UTF8Encoding(false), 1 << 20);

        var dimensions = 0;
        long count = 0;
        long skipped = 0;
        var metaBuffer = new StringBuilder(256);

        using (var stream = File.OpenRead(jsonlPath))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var itemId = root.TryGetProperty("itemId", out var itemIdProp) ? itemIdProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(itemId) ||
                        !root.TryGetProperty("vector", out var vectorProp) ||
                        vectorProp.ValueKind != JsonValueKind.Array)
                    {
                        skipped++;
                        continue;
                    }

                    var length = vectorProp.GetArrayLength();
                    if (length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    if (dimensions == 0)
                    {
                        dimensions = length;
                    }
                    else if (length != dimensions)
                    {
                        Console.Error.WriteLine($"[pack] skipping '{itemId}': dimension {length} != {dimensions}");
                        skipped++;
                        continue;
                    }

                    // Write row by row to avoid materialising a second full copy of the payload.
                    var row = new float[dimensions];
                    var i = 0;
                    foreach (var element in vectorProp.EnumerateArray())
                    {
                        row[i++] = element.ValueKind switch
                        {
                            JsonValueKind.Number when element.TryGetSingle(out var f) => f,
                            JsonValueKind.Number when element.TryGetDouble(out var d) => (float)d,
                            _ => 0f
                        };
                    }

                    bin.Write(MemoryMarshal.AsBytes(row.AsSpan()));

                    var symbolId = root.TryGetProperty("symbolId", out var symbolIdProp) ? symbolIdProp.GetString() ?? string.Empty : string.Empty;
                    var pathValue = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;

                    metaBuffer.Clear();
                    metaBuffer.Append("{\"itemId\":");
                    AppendJsonString(metaBuffer, itemId!);
                    metaBuffer.Append(",\"symbolId\":");
                    AppendJsonString(metaBuffer, symbolId);
                    metaBuffer.Append(",\"path\":");
                    AppendJsonString(metaBuffer, pathValue);
                    metaBuffer.Append('}');
                    meta.WriteLine(metaBuffer.ToString());

                    count++;
                    if (count % 20000 == 0)
                    {
                        Console.Write($"\r[pack] {count.ToString("N0", CultureInfo.InvariantCulture)} rows packed...");
                    }
                }
            }
        }

        if (dimensions == 0 || count == 0)
        {
            throw new InvalidDataException($"'{jsonlPath}' contained no usable vectors.");
        }

        bin.Position = 0;
        VectorBinaryFormat.WriteHeader(bin, dimensions, count);
        bin.Flush(flushToDisk: false);

        Console.WriteLine();
        return new PackResult(dimensions, count, skipped, bin.Length, new FileInfo(metaPath).Length);
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
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
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
}

public readonly record struct PackResult(int Dimensions, long Count, long Skipped, long BinBytes, long MetaBytes);
