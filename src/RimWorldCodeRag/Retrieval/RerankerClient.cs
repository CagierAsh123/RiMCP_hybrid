using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldCodeRag.Retrieval;

/// <summary>
/// Client for the cross-encoder reranker server (<c>python/rerank_server.py</c>, plan task 2.4).
///
/// <para>
/// A reranker is the right tool for the failure mode the benchmark actually measured: 10 of 45
/// expected items were present in a retrieval leg but never reached the top-N
/// (<c>docs/rag-upgrade-plan-2026-09.md</c> §11.3). Fusion cannot fix that; a cross-encoder can,
/// because it scores the query and the candidate <b>together</b> instead of comparing two vectors
/// produced in isolation.
/// </para>
///
/// <para>
/// It is a ranked-list refinement, never a gate: if the server is unreachable the caller keeps the
/// original order.
/// </para>
/// </summary>
internal sealed class RerankerClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RerankerClient(string baseUrl, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            // A top-50 rerank on a 0.6B cross-encoder is seconds on GPU, tens of seconds on CPU.
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
    }

    public string BaseUrl => _baseUrl;

    /// <summary>
    /// Score each document against the query. Returns one score per document, in input order.
    /// Throws on transport/protocol failure — the caller decides how to degrade.
    /// </summary>
    public async Task<float[]> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            return Array.Empty<float>();
        }

        var request = new RerankRequest { Query = query, Documents = documents };
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync($"{_baseUrl}/rerank", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<RerankResponse>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);

        if (result?.Scores is null || result.Scores.Count != documents.Count)
        {
            throw new InvalidOperationException(
                $"reranker returned {result?.Scores?.Count ?? 0} scores for {documents.Count} documents");
        }

        return result.Scores.ToArray();
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class RerankRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("documents")]
        public IReadOnlyList<string> Documents { get; set; } = Array.Empty<string>();
    }

    private sealed class RerankResponse
    {
        [JsonPropertyName("scores")]
        public List<float>? Scores { get; set; }
    }
}
