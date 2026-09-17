using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace RimWorldCodeRag.Retrieval;

/// <summary>
/// On-disk layout for the packed vector index (task 1.1).
///
/// <para>
/// Two files replace the 1.6 GB <c>vectors.jsonl</c>:
/// </para>
/// <list type="bullet">
///   <item><c>vectors.bin</c> — a 24-byte header followed by <c>count * dim</c> little-endian float32 values, row major.</item>
///   <item><c>vectors.meta.jsonl</c> — one JSON object per line, in the same order as the bin rows, carrying only the fields the retrieval path actually needs (<c>itemId</c>, <c>symbolId</c>, <c>path</c>).</item>
/// </list>
///
/// <para>
/// <c>preview</c> / <c>signature</c> / <c>identifiers</c> are deliberately NOT stored: the search path
/// re-reads those from the Lucene document, and <c>preview</c> alone was the bulk of the old file
/// (XML defs embed their whole semantic title plus a text excerpt).
/// </para>
/// </summary>
public static class VectorBinaryFormat
{
    public const string BinFileName = "vectors.bin";
    public const string MetaFileName = "vectors.meta.jsonl";
    public const string LegacyJsonlFileName = "vectors.jsonl";

    public const int HeaderSize = 24;
    private const int FormatVersion = 1;

    private static ReadOnlySpan<byte> Magic => "RWVEC001"u8;

    public static void WriteHeader(Stream stream, int dimensions, long count)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], dimensions);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], count);
        stream.Write(header);
    }

    public static (int Dimensions, long Count) ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);

        if (!header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                $"'{BinFileName}' has an unrecognised magic number; not a RiMCP packed vector file.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported packed vector format version {version} (expected {FormatVersion}).");
        }

        var dimensions = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var count = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);

        if (dimensions <= 0 || count < 0)
        {
            throw new InvalidDataException($"Corrupt packed vector header: dimensions={dimensions}, count={count}.");
        }

        return (dimensions, count);
    }

    /// <summary>Header plus the byte size of the float payload — used to validate the file length.</summary>
    public static long ExpectedLength(int dimensions, long count)
        => HeaderSize + (count * dimensions * sizeof(float));

    public static string Describe(int dimensions, long count)
        => $"{count} vectors x {dimensions}d = {(ExpectedLength(dimensions, count) / (1024.0 * 1024.0)):F0} MB";
}
