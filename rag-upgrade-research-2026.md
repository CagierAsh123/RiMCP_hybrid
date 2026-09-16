# Local Code-Search RAG Upgrade Research (verified Feb 2026 snapshots)

Scope: your C#/.NET 8, all-local stack (167k chunks, Lucene.NET BM25 + e5-base-v2 768-d + brute-force 1.4 GB JSONL, 27 s/query, RTX 4060 Laptop 8 GB). Every version below was read off the vendor registry/doc page; items I could not directly confirm are marked **[UNCERTAIN]**.

**Headline finding:** your 27 s is a *storage/scan* problem, not a ranking problem. Brute-forcing 167k × 768-d floats per query is ~200 MB of memory traffic; an ANN index (HNSW) turns that into single-digit ms. Fix that *first* — reranking a 27 s query is polishing the wrong end.

---

## 1. Retrieval techniques that beat BM25 + one dense vector + brute force

| Technique | What it buys | Cost |
|---|---|---|
| **ANN index (HNSW/IVF) instead of brute force** | 27 s → ~1–20 ms recall@k. The single highest-ROI change. | One build pass; ~1.4× index size. See §4. |
| **Hybrid fusion RRF / weighted** | Standard 2026 default; robust when one retriever misses. RRF is rank-based, no score calibration. | ~0 extra latency; you already have both legs. |
| **Cross-encoder reranking** | Real gains only on *narrow* cases: buried answers in long chunks, large candidate pools. Independent testing finds rerankers *match or lose* to good embeddings on 4 of 5 query shapes — treat as a fallback, not a default ([towardsdatascience](https://towardsdatascience.com/rerankers-arent-magic-either-when-the-cross-encoder-layer-is-worth-the-cost-enterprise-document-intelligence-vol-1-2bis/)). | 50–150 ms per 50–100 candidates on GPU; no precompute possible. |
| **Late interaction / multi-vector (ColBERT)** | Token-level MaxSim quality at bi-encoder-ish search cost; PLAID gives 2.5–45× speedup over naive ColBERTv2 ([PLAID](https://www.alphaxiv.org/abs/2205.09707)). | Storage blowup: one vector per token, not per chunk → tens of GB for your corpus. Not justified at 167k chunks. |
| **Contextual retrieval / late chunking** | Anthropic's contextual retrieval cut retrieval failure ~35% (with reranking+BM25 up to ~67%); late chunking pools *before* splitting so each chunk embedding sees document context ([Anthropic](https://www.anthropic.com/engineering/contextual-retrieval), [arXiv 2504.19754](https://arxiv.org/html/2504.19754v1)). | One-time LLM pass per chunk (expensive) for contextual retrieval; late chunking is cheap but needs a long-context encoder (BGE-M3 8K, Qwen3 32K). |
| **AST-aware / symbol-aware chunking** | Highest structural fit for decompiled C#: chunk on type/method boundaries, keep the signature + containing type in the text. CoIR (code-IR benchmark) is the right yardstick. | Custom chunker work; you already have the symbol graph, so it is mostly free. |
| **HyDE / multi-query / query expansion** | Helps short/underspecified queries; multi-query raises recall then RRF-fuse. | 1–N extra LLM calls per query — breaks your local-only latency budget unless the LLM is local. |
| **GraphRAG / LazyGraphRAG** | Microsoft's LazyGraphRAG matches full GraphRAG quality at ~0.1% of indexing cost and vector-RAG-level indexing cost, at +2–8 s/query ([Microsoft](https://www.microsoft.com/en-us/research/blog/lazygraphrag-setting-a-new-standard-for-quality-and-cost/)). | You already have a 580k-edge graph + PageRank — this is the cheapest differentiator you own. **Invest here over reranking.** |
| **Agentic / multi-step retrieval** | Best for multi-hop "who calls X which writes Y" questions; turns your 4 MCP tools into a plan. | Latency and token cost; wrap in a step budget. |
| **Memory layers** | Session/query cache keyed by embedding; caches hot queries. | Trivial to add; helps the 27 s case most. |

---

## 2. Embedding models for mixed C# + Chinese + English (replacing e5-base-v2)

| Model | Params | Dim | Max tok | License | 8 GB VRAM |
|---|---|---|---|---|---|
| **Qwen3-Embedding-0.6B** | 0.6B | 1024 (MRL, 32→1024) | 32K | Apache-2.0 | Yes, comfortable; ONNX-exportable |
| **Qwen3-Embedding-4B** | 4B | 2560 (MRL) | 32K | Apache-2.0 | Tight in fp16 (~8 GB); use int8/4-bit |
| **Qwen3-Embedding-8B** | 8B | 4096 (MRL) | 32K | Qwen custom (commercial OK with notice) **[UNCERTAIN]** | No — needs quantization/offload |
| **BGE-M3** | 568M | 1024 | 8192 | MIT | Yes; dense+sparse+multi-vector from one model |
| **EmbeddingGemma-300M** | 300M | 768 | — | Gemma terms | Yes, CPU-friendly |
| **Nomic Embed Code** | 7B | 3584 **[UNCERTAIN]** | — | **[UNCERTAIN]** (Nomic open weights) | No (7B) — but SOTA on CodeSearchNet |
| **jina-code-embeddings-0.5b / 1.5b** | 0.5B/1.5B | — | — | **[UNCERTAIN]** — jina non-commercial restrictions apply to some releases | 0.5B yes |
| **jina-embeddings-v4** | 3.8B | 2048 | — | **CC-BY-NC / Qwen Research — no commercial use** | Tight |

Qwen3-Embedding scores: 0.6B MTEB-Multilingual 64.33 vs your e5-base baseline; 4B 69.45; 8B 70.58 ([Qwen blog](https://qwenlm.github.io/blog/qwen3-embedding/), [HF model card](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B)). Multilingual coverage ~119 languages, strong Chinese. **Recommendation: Qwen3-Embedding-0.6B** (MRL lets you truncate to 512-d and halve your index) — clear upgrade over e5-base-v2 at the same VRAM class. **BGE-M3** is the fallback if you want MIT licensing plus native sparse vectors to replace hand-tuned Lucene boosts.

---

## 3. Local rerankers

| Model | Size | License | Notes |
|---|---|---|---|
| **bge-reranker-v2-m3** | 0.6B | MIT | Best multilingual value; MIRACL 69.32 vs jina-reranker-v2 63.65 ([jina-reranker-v3 paper](https://arxiv.org/html/2509.25085v2)). Fits 8 GB fp16. |
| **Qwen3-Reranker-0.6B** | 0.6B | Apache-2.0 | MTEB-R 65.80, better on code (MTEB-Code 73.42) and long context (32K) |
| **Qwen3-Reranker-4B** | 4B | Apache-2.0 | MTEB-Code 81.20 — best code reranker at this size; needs int8 on 8 GB |
| **jina-reranker-v2-base-multilingual** | 278M | **[UNCERTAIN]** | 24–108 languages, CodeSearchNet MRR@10 71.36 |
| **jina-reranker-v3** | 0.6B | **[UNCERTAIN]** | CoIR 63.28; newest |

**Latency expectation:** a 0.6B cross-encoder over 50–100 candidates on a laptop 4060 is roughly **80–200 ms** total (batch the pairs; it is one forward pass per pair, ~1–3 ms each). A third-party benchmark citing ~80–120 ms for 100 candidates on a 4060 Ti is consistent, but that page now blocks scraping — treat the exact number as **[UNCERTAIN]**. At 8 GB, run the *reranker* and the *embedder* in the same process but never concurrently.

---

## 4. .NET / C# ecosystem

| Component | Current | Verdict |
|---|---|---|
| **Lucene.NET** | 4.8.0-beta00018 (latest; beta00019 at ~93%) — [status](https://github.com/apache/lucenenet/discussions/1401), [notes](https://lucenenet.apache.org/release-notes/version-4.8.0-beta00018.html) | **No KNN/vector support.** It ports Java Lucene 4.8; HNSW/KnnFloatVectorField arrived in Java Lucene 9. Not a vector store — keep it as your BM25 leg only. |
| **ONNX Runtime for .NET** | `Microsoft.ML.OnnxRuntime` **1.30.0**; GPU/CUDA via `Microsoft.ML.OnnxRuntime.Gpu`; DirectML provider still **1.24.4** | Production-ready. Lets you drop the Flask/PyTorch server and embed/rerank in-process. DirectML lags upstream — prefer the CUDA/GPU package on an RTX 4060. |
| **Microsoft.Extensions.AI** | **10.10.0** (`IEmbeddingGenerator<TInput,TEmbedding>`) | The .NET abstraction to target; no cloud dependency needed. |
| **Microsoft.Extensions.VectorData** | Abstractions 10.7.0 | The real standard now. `Microsoft.SemanticKernel.Connectors.Qdrant` is **deprecated** → use `CommunityToolkit.VectorData.Qdrant` 1.0.0 (stable, MIT, Jul 2026). |
| **Qdrant .NET** | `Qdrant.Client` **1.19.0** (Aug 2026), official | Best-supported server option; embedded search + payload filtering, replaces your JSONL entirely. |
| **LanceDB .NET** | `LanceDB` **2.5.0** (Jul 2026), P/Invoke over Rust crate; vector + FTS + hybrid | **Best fit if you want zero-server.** Embedded, columnar, hybrid search in one library. |
| **pgvector .NET** | `Pgvector` 0.3.2 (Npgsql/Dapper/EF Core; HNSW, half & sparse vectors) | Production-usable but requires a Postgres server — heavier than you need locally. |
| **sqlite-vec** | `sqlite-vec` **0.1.7-alpha.2.1** (last update May 2025) | Simple and embedded, but stale; only use if you want SQLite as your single file. |
| **Milvus .NET** | `Milvus.Client` **2.3.0-preview.1** — last updated **March 2024** | **Not production-usable.** Avoid. |
| **FAISS from .NET** | No first-party binding | Use LanceDB or Qdrant instead. |
| **MCP C# SDK** | `ModelContextProtocol` **2.2.0** (Aug 2026), `...AspNetCore` for HTTP servers | Official, maintained with Microsoft; use it instead of a hand-rolled MCP layer. |

**Recommended swap:** keep Lucene.NET for BM25, delete `vectors.jsonl`, and move dense vectors to **LanceDB 2.5.0** (embedded, no server, native hybrid) or **Qdrant** (if you would rather run a small local service). Either replaces the 27 s scan with an ANN lookup. Export Qwen3-Embedding-0.6B to ONNX and serve it with `Microsoft.ML.OnnxRuntime` so the whole pipeline is one .NET process.

---

## 5. MCP ecosystem notes worth copying

- **`zilliztech/claude-context`** — [repo](https://github.com/zilliztech/claude-context): the reference semantic-code-search MCP; useful for tool-surface and indexing UX patterns.
- **`luuuc/sense`** — [repo](https://github.com/luuuc/sense): closest to your design — tree-sitter symbol graph + blast radius + semantic search, explicitly local-only. Copy its *blast radius* tool; it is a natural fifth tool next to `get_uses`/`get_used_by`.
- **RAG-MCP** ([arXiv 2505.03275](https://arxiv.org/html/2505.03275v1)): with many tools in one server, retrieve the relevant *tool descriptions* first to cut prompt bloat. Relevant if your 4-tool server grows.
- **`ModelContextProtocol.Extensions.Tasks`** (in the 2.2.0 SDK): run long tool invocations asynchronously with status polling — the right wrapper for any remaining slow path so a 27 s query stops blocking the agent turn.
- Your existing `rough_search → get_item → get_uses/get_used_by` funnel is already a good agentic pattern; the graph tools are your differentiated asset, and LazyGraphRAG-style query-time summarization is the highest-leverage next feature.

---

### Verification notes
- Verified live: Lucene.NET beta00018 notes, Java Lucene 10.5.1, NuGet pages for Microsoft.Extensions.AI, Qdrant.Client, CommunityToolkit.VectorData.Qdrant, Pgvector, sqlite-vec, Milvus.Client, LanceDB, ModelContextProtocol, ONNX Runtime.
- Marked **[UNCERTAIN]**: Qwen3-8B license, nomic-embed-code dim/license, all jina licenses, reranker latency benchmark (source blocked scraping). Re-check on Hugging Face model cards before committing.
- Web-search engines were rate-limited during this session; several claims (Qwen3 MTEB numbers, MRL/binary quantization compression table, LazyGraphRAG costs) come from vendor/paper pages I could read but not independently cross-check.
