using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Indexer;

public sealed class IndexingPipeline
{
    private readonly IndexingConfig _config;
    private readonly MetadataStore _metadataStore;

    public IndexingPipeline(IndexingConfig config)
    {
        _config = config;
        _metadataStore = new MetadataStore(Path.Combine(config.MetadataPath, "mtimes.json"));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _metadataStore.EnsureLoaded();
        ResolveExclusionFilter();
        HandleForceRebuild();

        var chunker = new Chunker(_config, _metadataStore);
        var changedChunks = chunker.BuildChunks();

        var requiresRebuild = changedChunks.Count > 0 || !LuceneIndexExists() || !VectorIndexExists() || !GraphExists();

        if (!requiresRebuild)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[index] No changes detected. Existing artifacts remain current.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[index] Capturing full snapshot...");
        var fullChunks = chunker.BuildFullSnapshot();

        // Update metadata for all processed files
        foreach (var path in fullChunks.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _metadataStore.SetTimestamp(path, File.GetLastWriteTimeUtc(path));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[index] failed to update metadata for {path}: {ex.Message}");
            }
        }

        if (fullChunks.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[index] Writing {fullChunks.Count} chunks to Lucene index...");
            using (var lucene = new LuceneWriter(_config.LuceneIndexPath))
            {
                if (_config.ForceRebuildLucene) lucene.Reset();
                lucene.IndexDocuments(fullChunks);
                lucene.Commit();
            }
            Console.ResetColor();
        }

        // Resolve the embedding generator once: both the full and the incremental path need it.
        var embeddingGenerator = CreateEmbeddingGenerator();

        if (_config.ForceRebuildEmbeddings || !VectorIndexExists())
        {
            if (embeddingGenerator != null)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"[index] Generating embeddings for {fullChunks.Count} chunks...");
                await GenerateEmbeddingsAsync(fullChunks, embeddingGenerator, _config.VectorIndexPath, cancellationToken);
                Console.ResetColor();
            }
        }
        else if (embeddingGenerator != null)
        {
            // The old code skipped embedding entirely whenever a vector file existed, so an
            // incremental run gave every new/changed chunk a Lucene document and a graph node but
            // NO vector — invisible to the semantic leg. Reconcile instead.
            await EmbedIncrementallyAsync(fullChunks, embeddingGenerator, cancellationToken);
        }

        var graphBuilder = new GraphBuilder(_config.GraphPath, _config.MaxDegreeOfParallelism);
        graphBuilder.BuildGraph(fullChunks);

        BuildModCatalog(fullChunks);
        WriteSourceRootHint();

        _metadataStore.Save();
    }

    private IEmbeddingGenerator? CreateEmbeddingGenerator()
    {
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[index] Using remote embedding API at {_config.EmbeddingServerUrl}, model: {_config.ModelName}");
            Console.ResetColor();
            return new ApiEmbeddingGenerator(_config.EmbeddingServerUrl, _config.ApiKey, _config.ModelName);
        }

        if (!string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[index] Using local embedding server at {_config.EmbeddingServerUrl}");
            Console.ResetColor();
            return new ServerBatchEmbeddingGenerator(_config.EmbeddingServerUrl, _config.PythonBatchSize);
        }

        if (!string.IsNullOrWhiteSpace(_config.PythonScriptPath) && File.Exists(_config.PythonScriptPath))
        {
            if (string.IsNullOrWhiteSpace(_config.ModelPath))
            {
                throw new InvalidOperationException("Model path is required when using the Python embedding bridge.");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[index] Using local Python bridge with model at {_config.ModelPath}");
            Console.ResetColor();
            return new PythonEmbeddingGenerator(_config.PythonExecutablePath!, _config.PythonScriptPath, _config.ModelPath, _config.PythonBatchSize);
        }

        return null;
    }

    /// <summary>
    /// Bring the vector index in line with <paramref name="chunks"/>: embed only the rows that are
    /// missing, and drop the rows whose item id no longer exists.
    ///
    /// <para>
    /// This is what makes incremental indexing actually correct. Item ids embed a span hash, so any
    /// edit produces a new id; without this step the edited code keeps a Lucene document and a graph
    /// node but loses its vector, silently vanishing from semantic search.
    /// </para>
    /// </summary>
    private async Task EmbedIncrementallyAsync(
        IReadOnlyList<ChunkRecord> chunks,
        IEmbeddingGenerator generator,
        CancellationToken cancellationToken)
    {
        var directory = _config.VectorIndexPath;
        var manifest = VectorManifest.TryRead(directory);
        if (manifest is null)
        {
            Console.Error.WriteLine(
                "[index] WARNING: no packed vector index to update (legacy vectors.jsonl or missing file). " +
                "New chunks would have NO vector and be invisible to semantic search; run with --force embed.");
            return;
        }

        var currentIds = new HashSet<string>(chunks.Select(c => c.ItemId), StringComparer.OrdinalIgnoreCase);
        var toEmbed = chunks.Where(c => !manifest.Contains(c.ItemId)).ToList();
        var staleCount = manifest.Rows.Count(r => !currentIds.Contains(r.ItemId));

        if (toEmbed.Count == 0 && staleCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[index] Embeddings are up to date ({manifest.Count:N0} vectors).");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(
            $"[index] Incremental embeddings: {toEmbed.Count:N0} new chunk(s) to embed, " +
            $"{staleCount:N0} stale row(s) to drop (existing {manifest.Count:N0} rows).");
        Console.ResetColor();

        var batchSize = Math.Max(1, generator.PreferredBatchSize);

        // Probe the first batch before writing anything: if the model changed, the dimension differs
        // and mixing rows would silently corrupt the index.
        IReadOnlyList<float[]> firstVectors = Array.Empty<float[]>();
        if (toEmbed.Count > 0)
        {
            var firstBatch = toEmbed.Take(Math.Min(batchSize, toEmbed.Count)).ToList();
            firstVectors = await generator.GenerateEmbeddingsAsync(firstBatch, cancellationToken).ConfigureAwait(false);

            var probeDimension = firstVectors.FirstOrDefault(v => v.Length > 0)?.Length ?? 0;
            if (probeDimension > 0 && probeDimension != manifest.Dimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model changed dimension ({manifest.Dimensions} -> {probeDimension}); " +
                    "an incremental update cannot mix them. Rerun with --force embed to rebuild the vector index.");
            }
        }

        using var writer = new PackedVectorWriter(directory);
        var dropped = PackedVectorWriter.CopyKeptRows(writer, directory, manifest, currentIds);

        var processed = 0;
        for (var i = 0; i < toEmbed.Count; i += batchSize)
        {
            var batch = toEmbed.Skip(i).Take(batchSize).ToList();
            if (batch.Count == 0)
            {
                continue;
            }

            var vectors = i == 0 && firstVectors.Count > 0
                ? firstVectors
                : await generator.GenerateEmbeddingsAsync(batch, cancellationToken).ConfigureAwait(false);

            for (var j = 0; j < batch.Count && j < vectors.Count; j++)
            {
                if (vectors[j].Length == 0)
                {
                    Console.Error.WriteLine($"[index] skipping '{batch[j].ItemId}': empty embedding");
                    continue;
                }

                writer.Append(batch[j].ItemId, batch[j].SymbolId, batch[j].Path, vectors[j]);
            }

            processed += batch.Count;
            Console.Write($"\r[index] Embedded {processed}/{toEmbed.Count} new chunk(s)...");
        }

        writer.Finish();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"[index] Vector index updated: {writer.Count:N0} rows x {writer.Dimensions}d " +
            $"(added {toEmbed.Count:N0}, dropped {dropped:N0}).");
        Console.ResetColor();
    }

    /// <summary>
    /// Record the source root next to the index so tools that read the source tree directly
    /// (the <c>grep</c> tool) do not need their own configuration.
    /// </summary>
    private void WriteSourceRootHint()
    {
        try
        {
            Directory.CreateDirectory(_config.MetadataPath);
            File.WriteAllText(Path.Combine(_config.MetadataPath, SourceRootHint.FileName), _config.SourceRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[index] could not record the source root: {ex.Message}");
        }
    }

    /// <summary>
    /// Derive the mod alias/vocabulary table from the chunks themselves (exact, no re-parsing) plus
    /// the mod directory names. See <see cref="ModCatalog"/> for why this is needed.
    /// </summary>
    private void BuildModCatalog(IReadOnlyList<ChunkRecord> chunks)
    {
        var entries = ModCatalogBuilder.Build(_config.SourceRoot, chunks);
        var indexRoot = PathExclusionFilter.ResolveIndexRoot(_config.VectorIndexPath);
        ModCatalog.Save(indexRoot, entries);
        Console.WriteLine($"[index] Mod catalog: {entries.Count} mods -> {Path.Combine(indexRoot, ModCatalog.FileName)}");
    }

    private async Task GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, IEmbeddingGenerator generator, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var binPath = Path.Combine(directory, VectorBinaryFormat.BinFileName);
        var metaPath = Path.Combine(directory, VectorBinaryFormat.MetaFileName);

        // Write to temp files and swap them in at the end. A reader that opens a half-written
        // vectors.bin sees the placeholder header (dim = 0) and throws, so the index must never be
        // visible in a partial state — a long re-embed otherwise means hours of "restart the MCP
        // server and it dies".
        var binTemp = binPath + ".tmp";
        var metaTemp = metaPath + ".tmp";

        try
        {
            // Header is written as a placeholder and patched once dim/count are known, so the float
            // payload streams straight to disk.
            using var bin = new FileStream(binTemp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            VectorBinaryFormat.WriteHeader(bin, 0, 0);

            using var meta = new StreamWriter(metaTemp, append: false, new UTF8Encoding(false), 1 << 20);

            var processed = 0;
            var dimensions = 0;
            var metaBuffer = new StringBuilder(256);
            var batchSize = generator.PreferredBatchSize;

            for (var i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                if (batch.Count == 0) continue;

                var vectors = await generator.GenerateEmbeddingsAsync(batch, cancellationToken);

                for (var j = 0; j < batch.Count; j++)
                {
                    var chunk = batch[j];
                    var vector = vectors[j];

                    if (vector.Length == 0)
                    {
                        Console.Error.WriteLine($"[index] skipping '{chunk.ItemId}': empty embedding");
                        continue;
                    }

                    if (dimensions == 0)
                    {
                        dimensions = vector.Length;
                    }
                    else if (vector.Length != dimensions)
                    {
                        Console.Error.WriteLine($"[index] skipping '{chunk.ItemId}': dimension {vector.Length} != {dimensions}");
                        continue;
                    }

                    bin.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan()));

                    metaBuffer.Clear();
                    metaBuffer.Append("{\"itemId\":");
                    AppendJsonString(metaBuffer, chunk.ItemId);
                    metaBuffer.Append(",\"symbolId\":");
                    AppendJsonString(metaBuffer, chunk.SymbolId);
                    metaBuffer.Append(",\"path\":");
                    AppendJsonString(metaBuffer, chunk.Path);
                    metaBuffer.Append('}');
                    await meta.WriteLineAsync(metaBuffer.ToString()).ConfigureAwait(false);

                    processed++;
                }
                Console.Write($"\r[index] Generated {processed}/{chunks.Count} embeddings...");
            }

            await meta.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (dimensions > 0 && processed > 0)
            {
                bin.Position = 0;
                VectorBinaryFormat.WriteHeader(bin, dimensions, processed);
            }

            bin.Flush(flushToDisk: true);
            Console.WriteLine();
            Console.WriteLine($"[index] Wrote {VectorBinaryFormat.Describe(dimensions, processed)} to {binPath}");
        }
        catch
        {
            // Nothing half-written may survive a failure.
            TryDelete(binTemp);
            TryDelete(metaTemp);
            throw;
        }

        // Swap both files into place only after the payload is complete. The `using` streams above
        // are already closed here, which is required before moving them.
        File.Move(binTemp, binPath, overwrite: true);
        File.Move(metaTemp, metaPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort: the file is still open or already gone.
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

    /// <summary>
    /// Resolve the shared path-exclusion rules, materialize the default <c>exclude.json</c>
    /// when missing, and force a Lucene rebuild whenever the effective rules change.
    /// Lucene documents are updated in place by itemId, so chunks that are newly excluded
    /// would otherwise linger in the index forever.
    /// </summary>
    private void ResolveExclusionFilter()
    {
        var indexRoot = PathExclusionFilter.ResolveIndexRoot(_config.VectorIndexPath);

        var created = PathExclusionFilter.EnsureFile(indexRoot);
        if (created is not null)
        {
            Console.WriteLine($"[index] Created default exclusion file: {created}");
        }

        var filter = _config.ExclusionFilter ?? PathExclusionFilter.LoadForIndex(_config.VectorIndexPath);
        Console.WriteLine($"[index] {filter.Describe()} (signature {filter.Signature})");

        var signaturePath = Path.Combine(_config.MetadataPath, "exclude.sig");
        var previous = File.Exists(signaturePath) ? File.ReadAllText(signaturePath).Trim() : null;
        if (!string.Equals(previous, filter.Signature, StringComparison.Ordinal))
        {
            Console.WriteLine($"[index] Exclusion rules changed ({(previous ?? "<none>")} -> {filter.Signature}); forcing Lucene rebuild.");
            _config.ForceRebuildLucene = true;
            Directory.CreateDirectory(_config.MetadataPath);
            File.WriteAllText(signaturePath, filter.Signature);
        }
    }

    private void HandleForceRebuild()
    {
        if (_config.ForceRebuildLucene)
        {
            Console.WriteLine("[index] Forcing Lucene rebuild.");
            if (Directory.Exists(_config.LuceneIndexPath)) Directory.Delete(_config.LuceneIndexPath, true);
        }
        if (_config.ForceRebuildEmbeddings)
        {
            Console.WriteLine("[index] Forcing embeddings rebuild.");
            if (Directory.Exists(_config.VectorIndexPath)) Directory.Delete(_config.VectorIndexPath, true);
        }
        if (_config.ForceRebuildGraph)
        {
            Console.WriteLine("[index] Forcing graph rebuild.");
            var graphDir = Path.GetDirectoryName(_config.GraphPath);
            if (!string.IsNullOrEmpty(graphDir) && Directory.Exists(graphDir))
            {
                foreach (var file in Directory.GetFiles(graphDir, "graph.*"))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private bool LuceneIndexExists() => Directory.Exists(_config.LuceneIndexPath) && Directory.EnumerateFiles(_config.LuceneIndexPath).Any();

    private bool VectorIndexExists() => Directory.Exists(_config.VectorIndexPath) && VectorIndex.Exists(_config.VectorIndexPath);

    private bool GraphExists() => File.Exists(_config.GraphPath + ".nodes.tsv");
}
