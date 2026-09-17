# RiMCP 代码 RAG —— 现状评估与升级建议

- 评估日期：2026-09-16
- 范围：`B:\RiMCP_hybrid-master\RiMCP_hybrid-master`（仅评估，**未改动任何代码**）
- 结论来源：本地源码逐文件阅读 + 实测延迟 + 外部技术调研（§4/§5）

---

## 0. 一句话结论

这套管线在一年前是**超前的**：Roslyn 符号级分块、稀疏矩阵图存储、MCP 工具面、Web 可视化，都是后来才流行的做法。
但现在有三处硬伤把它拖住了：

| # | 问题 | 后果 | 修起来难吗 |
|---|---|---|---|
| 1 | 向量存成 1.4 GB 文本 JSONL，且**换个参数就重建 searcher 全量重解析** | `rough_search` 带 `kind` 筛选时 **27 秒**一次 | 易（几小时） |
| 2 | `UseSemanticScoringOnly = true` —— **混合检索被关掉了** | 精确标识符/Def 名查询丧失 BM25 精确优势 | 易 |
| 3 | 嵌入模型 e5-base-v2（2022 年，512 token） | 中文 + 代码标识符召回弱 | 中（要重嵌入一次） |

另外：联网搜索**不属于本项目范围**（已确认移交 DeepSeek Harness 侧，见 §5）。

---

## 1. 代码基线事实

| 项 | 事实 | 证据 |
|---|---|---|
| 项目组成 | 3 个 .NET 8 项目：`RimWorldCodeRag`(索引+检索 CLI)、`RimWorldCodeRag.McpServer`(stdio JSON-RPC)、`RimWorldCodeRag.WebApp`(Minimal API + 前端) | `RiMCP_hybrid.sln` |
| 关键依赖 | `Lucene.Net 4.8.0-beta00016`、`Microsoft.CodeAnalysis.CSharp 4.10.0`(Roslyn)、`System.Numerics.Tensors 9.0.10`(SIMD)、`F23.StringSimilarity 7.0.0` | 3 个 `.csproj` |
| 版本管理 | **仅 2 次提交，最后提交 2026-02-23**（`Update .gitignore`） | `git log` |
| 语料 | `B:\rimworld-code\_SourceCode`：原版 ILSpy 版 + 47 个 mod 转储；本次重建 **167,213 chunks** | 索引运行日志 |
| 索引产物 | `lucene\`(BM25 倒排)、`vec\vectors.jsonl`(1.4 GB →重建中)、`graph.*`(csr/csc/nodes/pagerank，约 400 MB 级) | `src\RimWorldCodeRag\index\` |
| MCP 工具面 | 4 个：`rough_search`、`get_item`、`get_uses`、`get_used_by`；协议版本 **2024-11-05**，仅 stdio | `McpServer.cs`、`Tools\*.cs` |
| WebApp 能力 | 搜索、代码查看(Prism)、关系图(cytoscape)、索引管理、MCP 配置片段生成、模型/嵌入服务开关 | `wwwroot\index.html` |
| 历史包袱 | 仓库内 `RimWorldData\` **7.8 GB**（含 3.67 GB 贴图，是旧语料副本）、仓库根 `index\` 163 MB（旧索引副本） | 目录扫描 |
| 工程化 | 无测试、无 CI、无 benchmark；`SourceWatcher` 是**死代码**（见 §2.7） | 全仓库 grep |

---

## 2. 检索链路逐段诊断

### 2.1 分块 ✅ 现代做法（无需改）

`Chunker.cs` 用 Roslyn AST 按 **type / method / ctor / property / const field** 切块，XML 按整个 Def 节点切块，
并且 `BuildContextPrefix()` 会把「所属类型 + 签名」拼进检索文本。

> 这正是 2025 年被反复宣传的 *AST-aware / symbol-aware chunking* + *context header*，作者提前一年做了，属于加分项。

### 2.2 嵌入 ⚠️ 落后两代

- 模型：`e5-base-v2`，768 维，max 512 token，mean pooling（`python\embedding_server.py`）
- 查询侧用 `query: ` 前缀、文档侧 `passage: ` 前缀（e5 规范，做对了）
- 但语料里混着**大量中文**（mod 名、注释、汉化 XML）与**代码标识符**，e5-base-v2 对这两类都偏弱
- 升级方案见 §4；**代价**：必须重跑一次全量嵌入（本轮重建实测约 1.7 小时）

### 2.3 向量存储与加载 ❌ **27 秒延迟的真凶**

```csharp
// VectorIndex.Load(): 逐行 JsonDocument.Parse
while ((line = reader.ReadLine()) != null) { using var doc = JsonDocument.Parse(line); ... }
```

```csharp
// RoughSearchTool.ExecuteAsync(): 参数不等于默认值 → 新建 searcher
else {
    var perReq = new RoughSearchConfig { ... Kind = kind, MaxResults = maxResults };
    using var tmpSearcher = new RoughSearcher(perReq);   // ← 重新 Load 整个 1.4 GB
    results = await tmpSearcher.SearchAsync(query);
}
```

- 1.4 GB JSONL ≈ **167k × 768 ≈ 1.28 亿个十进制数字**，每一个都要文本解析
- 默认参数路径有 `Lazy<RoughSearcher>` 缓存（首次慢、后续快）；**但只要客户端带上 `kind` 或改 `max_results`，每次请求都会掉进 27 秒坑**
- 实测：`rough_search(query, kind='cs', max_results=3)` → **27.35 s / 27.26 s**（两次）
- 解析后常驻内存 ≈ 167k × 768 × 4 B = **490 MB** float 数组（外加字符串与 `Dictionary`），每请求一份 → GC 压力
- 真正的打分（`Parallel.ForEach` + `TensorPrimitives.Dot` SIMD）只需**几十毫秒**
  → **瓶颈 100% 在序列化/解析，与算法和 GPU 都无关**

### 2.4 混合检索 ❌ 名存实亡

- `RoughSearchConfig.UseSemanticScoringOnly = true`，且 `RoughSearchTool` 的默认配置就是 `true`
- `MergeResults()` 在这个开关下**直接丢弃全部词法结果**，只按语义分数排序
- 后果：`Pawn_FlightTracker`、`CompExplosive`、`ThingDef` 这类**精确符号名查询**，失去 BM25 的精确命中优势，全靠嵌入模型「猜」
- README 里作者解释了放弃混合的理由：实验发现「Lucene 预筛会漏掉正确答案」——这个诊断是对的，但**结论下错了**：
  - 错的不是「混合」，而是「先用 Lucene 砍到 1000 再语义精排」这个**级联**结构；
  - 正确做法是**并行召回 + RRF 融合**（Reciprocal Rank Fusion）：两路各自出 top-k，按排名倒数加权合并，不做任何预筛、也不做分数相加（原始分数不可比，这正是作者当年"相加反而变差"的原因）
- 附带问题：`SemanticCandidates=5` 实际未生效（全量扫描后取 `max(MaxResults*3, 5)`）；`ExpandQuery()`（camelCase 拆词）只在词法分支用；无 rerank、无查询改写

### 2.5 知识图 ⚠️ 语法级 + 人工权重

- `GraphBuilder.cs` 用 `CSharpSyntaxTree.ParseText(chunk.Text)`：**只解析单个 chunk 的文本**，没有 `SemanticModel`/`GetSymbolInfo`
  → 静态成员、反射、别名、泛型约束等引用抓不到（README 1.2.1 已承认；边数从「近 5000 万 → 23 万」修过一轮，现在 58.7 万）
- 排序是人工公式：`PageRank×1e7 × 边类型权重 × √(重复数) × 词法加成`（`GraphQuerier.cs`），作者自述「担心是拍脑袋」
- 缺：社区检测/聚类摘要（GraphRAG 那一层）、边置信度、多跳剪枝策略
- ✅ 优点很突出：`csr/csc` 稀疏矩阵存储，双向 O(1) 查边；整个图 400 MB 级；结果分页防止上下文爆炸（README 里"一次返回 2.6 万条"的教训是真踩过）

### 2.6 MCP 层 ⚠️ 停在 2024-11

- 协议声明 `2024-11-05`；之后 MCP 已迭代多个版本（新增 resources / prompts / sampling / progress 通知 / tool annotations 等）
- 只有 `StdioTransport`（SSE/HTTP 未实现，作者也列在 TODO）
- 工具描述质量很高（把「先粗搜→再 get_item」的工作流写进了 description），这一点是正确且现代的
- 兜底逻辑写死导致「换参数重建 searcher」——作者自己在注释里吐槽了

### 2.7 工程化 ❌

- `Indexer\Watcher.cs` 的 `SourceWatcher`(FileSystemWatcher) **全项目无人实例化** → 源码改动不会自动增量索引（死代码）
- 无单元测试 / 无 CI / 无回归 benchmark（唯一"测试"是手工跑 CLI）
- `GetItemTool` 每次调用 `new ExactRetriever()`；`GetUses/GetUsedBy` 用 `Lazy<GraphQuerier>` 缓存（这个是对的）

---

## 3. 联网搜索 / GitHub 搜索 —— 核查结论（**不在本项目范围，见 §5**）

**结论：两者都不存在。** 不是配置缺失、不是坏了、也不是被开关关掉——**代码里从来没有过**。

证据：

```
全仓库 grep  github | GitHub | tavily | brave | serp | duckduckgo | web_search | WebFetch | searx
命中：仅 src\RimWorldCodeRag\models\e5-base-v2\tokenizer.json 里的词表 "brave"（无关）
```

- MCP 只暴露 4 个**本地**检索工具，无任何出网工具
- WebApp 的 16 个端点全部是本地检索/索引/配置/模型管理（`/api/search`、`/api/item`、`/api/graph/*`、`/api/index/*`、`/api/models/*`）
- 唯一的出网能力是**可选的远程嵌入 API**（`--api-key` + `--model-name`，v1.1.0 加的），与搜索无关

→ **该能力已确认由 DeepSeek Harness 侧提供，不在 RiMCP 范围内（见 §5）**，故本文不再讨论实现方案。

---

## 6. 不建议做的事（避免跟风）

1. **不要马上升级到向量数据库**（Qdrant/pgvector/Milvus…）：瓶颈不是检索算法，而是「文本 JSONL 解析」。改成二进制 mmap + 单例缓存后，167k 规模的暴力 SIMD 点积只需几十毫秒，**引入外部服务只增加部署复杂度**。等语料涨到千万级再说。
2. **不要为了 KNN 而迁到 Lucene 9+ / Lucene.NET 新版**：图省事的收益不抵迁移成本（分析器、查询语法、API 全变），且当前 chunk 规模远未到需要 ANN 的程度。
3. **不要一上来做 GraphRAG 全量摘要**：社区摘要需要 LLM 跑几十万 chunk，成本高、收益不确定；先做 §4 的 P0/P1，再看图检索的实际使用率。
4. **不要把「分数相加」当成混合检索**：语义余弦与 BM25 分数不可比，正是作者当年实验失败的根因；用 RRF（只看排名）或加权 + 归一到同一分布。
5. **不要为省时间跳过重嵌入**：换模型必须全量重嵌入（本轮 167k chunks ≈ 1.7 小时 GPU），否则旧向量与新查询向量空间不一致，检索会静默变差。

---

## 7. 实测数据附录

### 7.1 本机硬件与嵌入吞吐（RTX 4060 Laptop 8 GB，e5-base-v2 / 768 维）

| 单条长度 | 吞吐 | 167k chunks 预计 |
|---|--:|--:|
| ~150 token | 76.6 items/s | 40 分钟 |
| ~300 token | 46.4 items/s | 66 分钟 |
| ~600/512 token（截断到 512） | 32.9 items/s | 92 分钟 |
| **流水线实测**（含 JSON 序列化 + 落盘） | **27.2 items/s** | **≈102 分钟** |

> **重要推论**：流水线吞吐（27.2/s）与**512 token 满长度**的服务端吞吐（32.9/s）只差 ~20%，
> 而比 150 token 的吞吐（76.6/s）低得多 → 说明**绝大多数 chunk 都是长文本、被 512 上限截断了**。
> 这给 §4.2「换 8K/32K 上下文模型」提供了硬理由：现在不只是模型弱，**输入还在被截断丢信息**。
> （精确的截断比例可以在重建完成后抽样统计 token 长度分布来量化。）

### 7.2 索引重建各阶段实测（2026-09-16，`--force all`）

| 阶段 | 耗时 | 是否用 GPU |
|---|---|---|
| 快照 + 分块（167,213 chunks） | 约 1–2 分钟 | ❌ |
| Lucene 倒排全量写入 | 约 2–3 分钟 | ❌ |
| 嵌入 167,213 chunks | 约 100 分钟 | ✅ |
| 图重建（15 万 chunk / 58.7 万边） | 约 11 分钟 | ❌ |

### 7.3 查询延迟基线

**重建前（旧索引，1.4 GB）**

| 调用 | 延迟 | 说明 |
|---|--:|---|
| `rough_search(q, kind='cs', max_results=3)` | **27.35 s** | 触发「换参数→重建 searcher」路径 |
| `rough_search(q, kind='cs', max_results=5)` | **27.26 s** | 同上 |
| `get_uses(symbol)` | ~2–4 s | 图查询器有 Lazy 缓存 |

### 7.4 【关键实验】冷 / 热对比：27 秒的真凶是解析，不是扫描

**重建后（新索引，1.63 GB / 167,213 chunks，实体 GPU 4090 环境为 RTX 4060）**

| 调用 | 延迟 | 含义 |
|---|--:|---|
| ① 默认参数，首次（冷） | **15.34 s** | = `VectorIndex.Load()` 解析 1.63 GB JSONL + 首次检索 |
| ② **同一查询，同参数，再来一次（热）** | **0.26 s** | = 纯检索：并行 SIMD 点积遍历 16.7 万 × 768 维 + Lucene 回查 + 融合 |
| ③ 带 `kind='cs'`（每次都 `new RoughSearcher`） | **14.34 s** | = 又走一遍全量重载 |

**结论（与外部调研相反）**

- 真实检索只要 **0.26 s**，其中 167k × 768 = 1.28 亿次乘加的并行 SIMD 点积只占其中一小部分
- 冷启动 15.34 s 与热启动 0.26 s 相差 **59 倍** → **98% 的时间花在「把 1.63 GB 文本 JSON 解析成 float 数组」上**
- 带 `kind` 的 14.34 s 与冷启动同量级 → 证明「换参数重建 searcher」是纯粹的重复劳动
- ∴ **P0 修复（二进制/mmap + 单例缓存）预期把带筛选查询从 14 s 打到 ~0.3 s；ANN/HNSW 在 167k 规模上是多余的**（HNSW 也就在 10 ms 量级，却要引入新依赖与索引格式）

### 7.5 顺带发现的两个质量问题（实测结果暴露）

1. **`Develop` 目录被一起索引了**，而且同一 mod 的 `private/` 与公开副本**各存一份**，产生同分重复条目
   （例：`DigitalStorage.HarmonyPatches.Patch_GetFood` 两条，`itemId` 不同、分数完全相同）
   → 要么把 `Develop` 加进索引排除表，要么在检索时按 `path` 前缀去重
2. **纯语义排序（`UseSemanticScoringOnly=true`）的代价现场可见**：查询 `pawn hunger tick`，
   前 3 名中有 2 名是 RJW 色情 mod 的方法（语义近但主题离谱）；而真正的候选 `RimWorld.FoodUtility.Starving`
   被排到第 19 位 → 这正是「词法/标识符精确匹配被丢弃」的直接后果，也是 §4.1 恢复混合检索的最强证据

---

## 4. 新技术引进建议（P0 / P1 / P2）

### 4.0 先纠正一个流行误判：27 秒不是"暴力扫描"的问题

外部调研（独立子代理）把 27 秒归因为「暴力扫描 167k × 768 维 ≈ 200 MB 内存带宽」。**按本地代码实测，这个结论不成立**：

- 打分本身是 SIMD（`TensorPrimitives.Dot`）+ `Parallel.ForEach`；167k × 768 ≈ **1.28 亿次乘加**，在现代 CPU 上是**几十毫秒**量级
- 真正贵的是 `VectorIndex.Load()` 逐行 `JsonDocument.Parse` —— 要解析 **1.28 亿个十进制数字**（1.4 GB 纯文本）
- 直接旁证：默认参数路径有 `Lazy` 缓存（只在首次加载），而两次**带 `kind`** 的查询都是 27.3 s —— 因为每次都新建 searcher 重新加载
- 索引重建完成后会补冷/热对比实验（§7.4）把结论钉死

→ **P0 不是上 ANN，而是「向量二进制化/内存映射 + searcher 单例」**。167k 规模上 ANN 属于过度设计（等语料涨到千万级再说）。

### 4.1 P0：小时级改动，收益最大

| 动作 | 做法 | 预期效果 |
|---|---|---|
| **向量二进制化** | `vectors.bin` 定长记录（768×float32），或直接 `MemoryMappedFile` 映射成 `ReadOnlySpan<float>` | 加载 27 s → **< 1 s**（mmap 近乎 0） |
| **searcher 单例** | `RoughSearchTool` 按 `(kind, maxResults)` 缓存实例，而不是 `new RoughSearcher()`；或让 config 可变 | 带筛选查询 27 s → **~0.3 s** |
| **恢复真正的混合检索** | 两路**并行**各取 top-50，用 **RRF** 融合（`score = Σ 1/(60+rank)`）；**不做预筛、不做分数相加** | 精确符号名/Def 名召回显著回升 |
| **查询结果缓存** | 已有 `EmbeddingCache`(100 条)——扩展成 query→结果级缓存 | 重复查询零成本 |
| 顺手 | ONNX 量化（int8）可把 490 MB 常驻内存压到 ~130 MB | 内存/带宽 4× |

> 工作量 1~2 天，风险低，**不需要动语料和分块**。

### 4.2 P1：1~2 周，质量台阶

| 方向 | 具体选择 | 说明 |
|---|---|---|
| **换嵌入模型** | **Qwen3-Embedding-0.6B**（1024 维，MRL 可截到 512，32K 上下文，Apache-2.0，8 GB 显存舒适）<br>备选 **BGE-M3**（568M，1024 维，8K，MIT，且自带 sparse 向量可替代人肉 BM25 boost） | MTEB-Multilingual 64.33（e5-base 明显更低），中文强，119 语种；代价=全量重嵌入一次（**本轮实测 ≈ 1.7 小时**） |
| **上下文增强** | late chunking（分块前池化，需长上下文编码器）/ contextual retrieval（每 chunk 加 LLM 摘要，Anthropic 报告检索失败率降 ~35%） | 便宜的版本你已经有了（`BuildContextPrefix`）；升级版要 LLM 成本 |
| **重排器** | **Qwen3-Reranker-0.6B**（Apache-2.0，MTEB-Code 73.42）或 **bge-reranker-v2-m3**（MIT，多语最强），对 top-50 重排 | 80~200 ms/100 候选；**注意**：独立测试显示重排在 5 类查询里有 4 类「打平或输给好嵌入」→ 做成**可选开关**，别当默认 |
| **嵌入/重排进进程** | `Microsoft.ML.OnnxRuntime` **1.30.0** + `Microsoft.ML.OnnxRuntime.Gpu`（DirectML provider 还停在 1.24.4，别用） | 删掉 Flask+PyTorch 服务，顺带消灭"服务器没起→退化子进程→每次半分钟"的老毛病 |
| **MCP 现代化** | 官方 **`ModelContextProtocol` 2.2.0** C# SDK 替换手写 JSON-RPC | 白拿 resources / prompts / progress 通知 / tool annotations + HTTP(SSE) 传输；`...Extensions.Tasks` 可把慢工具改成异步任务 |

### 4.3 P2：差异化（别人抄不走的）

1. **LazyGraphRAG 式查询期图摘要**：你已经有 **58.7 万条边 + PageRank**，这是最便宜的差异化（微软称能以全 GraphRAG **约 0.1% 的索引成本**达到同级质量，查询额外 +2~8 s）。**比上重排器更值得投。**
2. **图重建改用 Roslyn 语义模型**：`Microsoft.CodeAnalysis` 已引入，且本机有游戏 `Managed\*.dll` 可作引用 → 可做真正的 `SemanticModel`/`GetSymbolInfo`，抓到静态成员、别名、泛型约束、`nameof` 等现在漏掉的引用。
3. **抄 `luuuc/sense` 的 blast-radius 工具**（tree-sitter 符号图 + 影响面），作为第 5 个 MCP 工具，和 `get_uses/get_used_by` 天然互补。
4. **索引自动化**：`SourceWatcher` 现在是死代码 → 接上 `FileSystemWatcher` 或加 `index --watch`，否则每次改源码都要手动全量跑。
5. **规模真涨到千万级**再考虑 ANN/向量库：**LanceDB 2.5.0**（嵌入式、P/Invoke、自带 FTS+hybrid，最省事）或 **Qdrant**（`Qdrant.Client` 1.19.0，需常驻服务）。
   - 坑：`Microsoft.SemanticKernel.Connectors.Qdrant` **已废弃** → 改用 `CommunityToolkit.VectorData.Qdrant`；`Milvus.Client` 自 2024-03 未更新，**别用**；`.NET` 没有官方 FAISS 绑定。
   - 另外要认命：**Lucene.NET 永远不会有 KNN/HNSW**（它移植的是 Java Lucene 4.8，而 HNSW 是 Java 9 才有的）→ Lucene 只当 BM25 那条腿用。

### 4.4 技术引进的优先级总表

| 优先级 | 事项 | 预估工作量 | 预期收益 |
|---|---|---|---|
| **P0** | 向量二进制化 + searcher 单例 | 0.5 天 | 查询 27 s → < 1 s |
| **P0** | RRF 恢复混合检索 | 0.5 天 | 精确符号/Def 名召回 ↑ |
| **P1** | 换 Qwen3-Embedding-0.6B + 重嵌入 | 1~2 天（含 1.7 h 机器时间） | 语义召回台阶 |
| **P1** | ONNX 进进程（干掉 Python 服务） | 2~3 天 | 部署简化、冷启动消失 |
| **P1** | MCP SDK 2.2.0 + SSE | 2~3 天 | 协议现代化、可远程 |
| **P2** | 图语义化重建（Roslyn SemanticModel） | 3~5 天 | 图检索准确性 |
| **P2** | LazyGraphRAG 查询期摘要 | 1~2 周 | 差异化 |

---

## 5. 联网 / GitHub 搜索 —— **不在本项目范围**

> **结论：本项能力由 DeepSeek Harness（DSH）侧提供，RiMCP 不做。**（2026-09-16 经用户确认后调整范围）

**核查结论（保留为事实记录）**：RiMCP 全仓库 grep `github|tavily|brave|serp|duckduckgo|web_search` 零命中
（唯一 match 是 e5 词表里的 "brave" 词元）；MCP 只暴露 4 个本地检索工具，WebApp 16 个端点全是本地检索/索引/配置。
即 **"没有联网搜索"不是故障，是这个工具的定位**。

**为什么不做**：
1. RiMCP 的定位是「**本地代码检索/导航**」——出网能力属于 agent 宿主（DSH）的职责，塞进检索器会让它偏离定位
2. 联网搜索涉及 key 管理、限流、ToS、许可、隐私（查询出网），这些应由宿主统一治理，而不是每个检索器各做一套
3. RAGFlow 那样的"内置 web 搜索"是**平台**形态才需要的功能（它要自己生成答案）

**调研存档（供 DSH 侧使用，不构成本项目任务）**：
- `rag-websearch-research-2026.md` —— GitHub 代码搜索（官方 REST `10 req/min`、1000 条上限、无批量接口；
  grep.app 已被 Vercel 安全网关拦、Sourcegraph 403、RimWorld Wiki 直连被 Cloudflare 403）、
  web 搜索 API 价格（Brave $5/1k、Tavily 1k credits/月免费…）、Bing Search API 已于 2025-08-11 退役、
  以及**免 key 的 Steam 创意工坊查询** `ISteamRemoteStorage/GetPublishedFileDetails/v1`（实测 200）
- 该文件顶部已标注归属：**目标为 DeepSeek Harness，不是 RiMCP**

---
## 8. 对标 RAGFlow（社区最火的开源 RAG 平台）

> 考察日期 2026-09-16，依据：`main` 分支 README_zh.md + **实际拉取源码树与关键文件**（不是只看宣传）。

### 8.1 它是什么

- **定位**：面向**非结构化文档**的端到端 RAG 引擎 + Agent 上下文引擎（Apache-2.0，当前 `v0.27.2`）
- **部署代价**：Docker Compose + **Elasticsearch（或 Infinity）+ MinIO + Redis + MySQL**，官方要求 **CPU ≥4 核 / RAM ≥16 GB / 磁盘 ≥50 GB / Python ≥3.13**
- **能力**：DeepDoc 深度文档理解（PDF/表格/影印件 OCR + 视觉模型）、基于模板的可视化切片、多路召回 + 融合重排序、引用快照溯源、数据源同步（Confluence/S3/Notion/GDrive/Discord）、Agentic Workflow、MCP、记忆、代码执行沙箱、聊天渠道

### 8.2 技术内核（源码实测）

| 模块 | 实际实现 | 对我们的意义 |
|---|---|---|
| **融合检索** (`rag/nlp/search.py`) | `FusionExpr("weighted_sum", knn_top_k, weights="{term},{vector}")`，**默认 文本 0.7 / 向量 0.3**；空结果时**降级 dense-only**；另有 `rank_feature` 字段加权 | ⚠ **它用的是加权和，不是 RRF**！关键在于它的 doc store 能把两路分数**归一化到可比区间**——这正是 RiMCP 当年"分数相加失败"缺的那一步 |
| **重排** (`rag/llm/rerank_model.py`) | 25+ provider 抽象（Jina / Cohere / Qwen / HuggingFace / Voyage / Bedrock / Nvidia / LM Studio / XInference / OpenAI 兼容 / SiliconFlow / Gitee…） | 重排被当成**可插拔阶段**，而不是硬编码——值得抄这个抽象 |
| **GraphRAG** (`rag/graphrag/`) | 完整微软式：`graph_extractor` + `entity_resolution`（实体消解）+ `community_report_prompt`/`community_reports_extractor`（社区报告）+ `entity_embedding` | 印证了 §4.3 的方向；但它靠 **LLM 全量抽取**，167k chunk 成本高 → 我们该走 **代码结构图 + LazyGraphRAG** 而不是 LLM 抽取 |
| **Agentic RAG** (`rag/advanced_rag/`) | `agentic_rag.py` / `agentic_rag_graph.py` + harness：**`grep_sed_narrow.py`**（grep/sed 式渐进收窄）、`chunk_utils`、`keywords`、`memory`、`orchestrator` | 🔥 **最有价值的一条**：对代码类语料，**渐进式 grep 收窄比向量检索更准**。RiMCP 完全可以加一个 `grep` 工具（对 `_SourceCode` 跑 ripgrep） |
| **MCP** (`mcp/server`, `mcp/client`) | **双向**：既是 MCP server（把检索暴露给 agent），也是 MCP client（含 `streamable_http_client`，在 agent 内部调外部工具） | RiMCP 只做了 server 半边；作为 client 能挂搜索/其它数据源（正好接 §5） |
| **记忆** (`memory/`) | query/message 两级记忆，落 ES/Infinity/GaussDB/OceanBase | 对应 §4.1 的"查询缓存"升级版 |
| **内置联网搜索** (`rag/utils/web_search_conn.py`) | Provider 抽象：You.com（免 key）、Tavily、Querit、Serply | 仅作事实记录：RAGFlow 把联网搜索做进平台；**RiMCP 不做**（见 §5，已移交 DSH） |

### 8.3 该抄什么 / 不该抄什么

**该抄（按性价比）**

1. **融合前先归一化**：RAGFlow 用加权和能work，是因为两路分数可比。RiMCP 现在 BM25 与余弦**直接相加**——**先 min-max/z-score 归一化再加权**，比切换成 RRF 改动更小、效果同源（也和作者当年的实验结论不冲突）。
2. **空结果降级**：hybrid 无命中时自动转 dense-only（RAGFlow 显式做了）。RiMCP 现在是"语义独大"，一旦向量没命中就没有兜底。
3. **Agentic grep 收窄**：给 MCP 加 `grep`（ripgrep over `_SourceCode`）。对"哪个 Def 用了这个字段""哪个类调了这个 API"这类**字面查询**，比嵌入强得多，且**零 GPU 成本**。
4. **Provider 抽象**：检索/重排做成可插拔 provider（联网搜索不在本项目范围，见 §5）。
5. **记忆层**：查询→结果缓存（比现在的 100 条 embedding 缓存更进一步）。
6. **MCP 双向**：RiMCP 作为 MCP **client** 去挂外部搜索/文档 server，比自己在 C# 里实现出网更省事。

**不该抄**

1. **DeepDoc / OCR / 表格解析**：那是解决"PDF 里没有结构"的问题；而代码有 Roslyn，**AST 永远比文档解析器更懂代码**。
2. **整套 Docker 重栈**（ES/Infinity + MinIO + Redis + MySQL，16 GB 内存、50 GB 磁盘）：RiMCP 全部索引才 ~2.5 GB、单 .NET 进程即可跑。为 167k chunk 引入这套是**十倍的复杂度换不到一分收益**。
3. **LLM 全量实体抽取式 GraphRAG**：成本高、且对代码来说**结构信息本来就有**（继承/调用/字段引用），不该用 LLM 去猜。
4. 多租户、聊天渠道、代码沙箱等企业功能。

### 8.4 结论

**RAGFlow 值得"抄架构"，不值得"换实现"。**

- 它解决的是「**把非结构化文档变成可问答知识库**」，RiMCP 解决的是「**精确代码导航**」——不是替代关系。
- 如果将来要覆盖 wiki / PDF / mod 文档这类语料，**正确姿势是并存**：RiMCP 管代码，RAGFlow 管文档，**在 MCP 层让 agent 同时挂两个 server**（这也是 RAGFlow 自己把 MCP 做成双向的原因）。
- 对当前 RiMCP，**从 RAGFlow 只取 3 件东西**：归一化融合 + 空结果降级 + agentic grep 收窄。其余一律不要碰。
