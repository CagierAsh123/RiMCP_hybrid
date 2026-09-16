# RiMCP 代码 RAG 升级实施计划

- 制定日期：2026-09-16
- 依据：`docs/rag-assessment-2026-09.md`（现状评估，含实测数据）+ `rag-upgrade-research-2026.md` / `rag-websearch-research-2026.md`
- 基线索引：2026-09-16 全量重建完成（167,213 chunks / 830,791 边 / 81 分钟）

---

## 0. 目标 / 非目标 / 验收

### 0.1 量化目标

| 指标 | 现状（实测） | 目标 |
|---|---|---|
| 带筛选查询延迟（`kind`） | **14.34 s** | **< 0.5 s** |
| 冷启动查询（含索引加载） | **15.34 s** | **< 2 s** |
| 热查询（同参数） | 0.26 s | 保持 |
| 检索质量（评测集 Recall@10） | 待测（阶段 0 建立） | **+15 个百分点** |
| 打开/关闭某 mod 源码 | 手动全量重跑（81 分钟） | **自动增量，< 2 分钟** |
| 部署依赖 | .NET + Python/Flask 嵌入服务 | 单 .NET 进程（Python 降为可选回退） |

### 0.2 非目标（明确不做，防止跑偏）

1. **不引入向量数据库/ANN**（Qdrant/LanceDB/Milvus/FAISS）：167k 规模下真实检索已 0.26 s，瓶颈是序列化不是算法
2. **不搬 RAGFlow 那套 Docker 重栈**（ES/Infinity + MinIO + Redis + MySQL，≥16 GB 内存）：为 2 GB 索引引入十倍复杂度
3. **不做 LLM 生成层**：RiMCP 的定位是"检索/导航工具"，生成交给调用它的 agent
4. **不做 DeepDoc 式文档解析**：代码有 Roslyn，AST 比任何文档解析器更懂代码

### 0.3 总原则

- **先有裁判再改**：阶段 0 的评测集是一切优化的唯一验收标准；"感觉更好"不算
- **每阶段可独立回滚**：改索引格式前先备份 `index\`；保留旧格式读取路径
- **小步提交**：每个任务一个可验证的产出，不搞"大重写"

---

## 1. 现状基线（2026-09-16 实测，改造前必须记录）

```
索引：lucene 131 MB/34 文件 · vec/vectors.jsonl 1,626 MB · graph.nodes.tsv 167,213 行 · pagerank 14.6 MB
规模：167,213 chunks（C# 138,224 / XML 28,989）· 830,791 条边
耗时：全量重建 81 分钟（快照+分块 ~2 min / Lucene ~3 min / 嵌入 ~65 min GPU / 图 ~11 min CPU）
```

| 延迟实验 | 结果 |
|---|--:|
| 默认参数首次（冷，含解析 1.63 GB JSONL） | 15.34 s |
| 同查询同参数第二次（热） | **0.26 s** |
| 带 `kind='cs'`（每次 `new RoughSearcher`） | 14.34 s |

**三处硬伤**：① 向量文本 JSONL + 换参数重建 searcher（→ 14~27 s）② `UseSemanticScoringOnly=true` 关掉了混合检索
③ e5-base-v2 落后两代 + 512 token 截断。
**两个质量问题**：① `Develop` 目录被索引且 `private/` 副本重复（`Patch_GetFood` ×2）② 纯语义排序翻车（`pawn hunger tick` 前 3 名 2 个是 RJW 色情 mod）。

---

## 2. 阶段 0：建立评测基准与护栏（0.5 天）

> 没有这一步，后面所有改动都无法证明"变好了"。**必须最先做。**

### 任务 0.1 评测集
- 产出：`tests/retrieval-baseline.json`
- 内容：**30 条查询**，覆盖 5 类（每类 6 条）
  1. 精确符号名（`Pawn_FlightTracker`、`CompExplosive`）
  2. Def 名 / DefType（`HeavyBridge`、`ThingDef weapon`）
  3. 自然语言机制（"pawn hunger tick"、"how does gravship launch work"）
  4. 跨层（"哪些 Def 用了这个 Comp"）
  5. 中英混合 + mod 相关（"米莉拉 飞行"）
- 每条标注：`expected`（期望命中的 itemId 或 symbolId 集合，允许 1~5 个正确项）+ `kind` + `note`
- 标注方法：人工从当前索引结果里挑正确项（用 `get_item` 核对）

### 任务 0.2 跑分脚本
- 产出：`tools/bench-retrieval.ps1`（PowerShell 调 CLI `rough-search`，不引第三方框架）
- 指标：**Recall@5 / Recall@10 / MRR / 平均延迟 / P95 延迟**
- 支持 `--baseline` 保存分数、`--compare` 与上次对比

### 任务 0.3 记录基线
- 把改造前分数写进本文件（`## 附录 A`）；失败用例单独列出（这些就是优化靶子）

**验收**：一条命令输出分数表；失败用例清单里能明确看到"精确符号名查不到""语义霸榜"这两类问题。

---

## 3. 阶段 1：P0 性能与召回（1~1.5 天，收益最大）

### 任务 1.1 向量二进制化 + 内存映射 ★
- 目标：冷启动 15.34 s → **< 1 s**
- 改动点：
  - `src/RimWorldCodeRag/Indexer/IndexingPipeline.cs`（`GenerateEmbeddingsAsync`，第 120~155 行）→ 同时输出二进制
  - `src/RimWorldCodeRag/Indexer/VectorWriter.cs` → **当前是死代码**（全项目无引用）；改造它作为二进制写入器，或直接删除以免混淆
  - `src/RimWorldCodeRag/Retrieval/VectorIndex.cs`（`Load`，第 24~109 行）→ 改为读二进制
- 新格式（两种都行，建议 A）：
  - **A. 两文件**：`vec/vectors.bin`（定长 float32 平铺，167213×768×4 ≈ 513 MB）+ `vec/vectors.meta.jsonl`（itemId/symbolId/path/signature/preview/identifiers，纯文本只读一次）
  - **B. 单文件**：magic + dim + count + 偏移表 + 数据段
- 读取实现：`MemoryMappedFile` + `MemoryMappedViewAccessor` → 或用 `float[]` 一次性 `ReadAllBytes`（513 MB，可接受）
- 兼容：保留 `--legacy-json` 开关；`VectorIndexExists()` 同时认 bin 与 jsonl
- **顺带**：把 `preview` 从向量文件里挪出（XML 全文很长，是 JSONL 体积大头之一）
- 验收：冷启动 < 1 s；`vectors.bin` 加载后检索结果与旧实现**完全一致**（同一评测集分数不变）

### 任务 1.2 searcher 单例 / 按参数缓存 ★
- 目标：带筛选查询 14.34 s → **< 0.5 s**
- 改动点：`src/RimWorldCodeRag.McpServer/Tools/RoughSearchTool.cs`（第 149~168 行的 `else` 分支）
- 方案（推荐 B）：
  - A. `ConcurrentDictionary<(string? kind, int max), RoughSearcher>` 缓存实例
  - **B. 让配置可变**：`RoughSearchConfig.Kind/MaxResults` 改为可写，`RoughSearcher` 增加 `SearchAsync(query, kind, maxResults)` 重载 → 彻底消灭"为改参数重建对象"这种设计
- 同步修：`GetItemTool`（每次 `new ExactRetriever`）→ 缓存实例
- 验收：`kind='cs', max_results=3` 查询 < 0.5 s；并发 5 个不同参数查询不报错（线程安全）

### 任务 1.3 恢复真正的混合检索 ★
- 目标：评测集 Recall@10 +15pt；`pawn hunger tick` 不再被 RJW 霸榜
- 改动点：`src/RimWorldCodeRag/Retrieval/RoughSearcher.cs`
  - `SearchAsync`（第 58~143 行）：两路**并行**召回，各取 top-100（现在语义只取 5）
  - `MergeResults`（第 289~356 行）：替换 `UseSemanticScoringOnly` 的"只留语义"逻辑
- 融合算法（**推荐先做 A，A 不行再 B**）：
  - **A. 归一化加权和**（RAGFlow 做法）：两路分数各自 min-max 归一化到 [0,1]，再 `w_lex*lex + w_sem*sem`，默认 0.5/0.5 可配
    - 这正是作者当年"分数相加失败"缺的一步：**BM25 与余弦量纲不同，必须先归一化**
  - **B. RRF**：`score = Σ w_i/(60 + rank_i)`，对量纲不敏感，与 A 二选一或叠加
- **标识符识别**：查询若形如符号名（含 `.`/下划线/驼峰且命中索引），把 `w_lex` 临时拉高（如 0.8/0.2）→ 精确查询稳赢
- 保留开关：`UseSemanticScoringOnly`（默认 **false**）用于 A/B 对比
- 验收：评测集分数上升；失败用例（符号名类）全部转为命中

### 任务 1.4 去重与排除
- 目标：消灭 `Patch_GetFood` 类同分重复条目
- 改动点：
  - `src/RimWorldCodeRag/Indexer/Chunker.cs`（`IsIndexableFile`，第 525 行）→ 增加排除表（默认排除 `\Develop\`、`\bin\`、`\obj\`、`\.git\`；做成可配置 `--exclude`）
  - 同 `SymbolId` 多路径去重：**保留优先级** = 非 `Develop` > 非 `private/` > 路径短者
- 验收：`rough_search("Patch_GetFood")` 不再返回两条同分项

### 任务 1.5 查询结果缓存
- `Retrieval/EmbeddingCache.cs` 已缓存 100 条**查询向量**；扩展为 `(query+kind+max)` → 结果列表（LRU，如 200 条）
- 验收：重复查询延迟 < 50 ms（命中缓存）

**阶段 1 里程碑 M1**：带筛选查询 < 0.5 s、冷启动 < 1 s、评测集 Recall@10 +15pt、无重复条目 → **暂停，向用户汇报**

---

## 4. 阶段 2：P1 模型与部署（3~5 天，质量台阶）

### 任务 2.1 换嵌入模型 Qwen3-Embedding-0.6B
- 理由：e5-base-v2（2022）中文/代码弱 + 512 token 截断；Qwen3-Embedding-0.6B（1024 维、MRL 可截 512、**32K 上下文**、Apache-2.0）适配 8 GB 显存
- 改动点：`src/RimWorldCodeRag/python/embedding_server.py`（模型名/维度/**前缀规范**：Qwen3 用 instruction 前缀，不是 e5 的 `query:`/`passage:`）、`models/`、`Chunker`/`VectorIndex` 里的维度常量
- **成本**：全量重嵌入一次 ≈ 65 分钟（GPU 实测），**先备份 `index\vec`**
- 风险：前缀用错会静默掉分 → 必须过评测集
- 验收：语义类查询 Recall@10 提升；512 截断率下降（可用 tokenizer 统计 chunk 长度分布验证）

### 任务 2.2 长上下文 / late chunking（可选）
- 32K 上下文后不再截断；进一步可做 **late chunking**（先编码整文件再池化切块）
- 需评测证明有益，否则不做（避免为"新"而新）

### 任务 2.3 ONNX 进进程（推荐）
- `Microsoft.ML.OnnxRuntime.Gpu` 1.30.0（DirectML provider 还停在 1.24.4，别用）
- 收益：删掉 Flask+PyTorch 常驻服务；消灭"服务没起 → 退化子进程 → 每次半分钟"；部署变单进程
- 风险：ONNX 导出后池化/归一化必须与 Python 端**逐位对齐**（用同一批文本比对向量余弦 > 0.999）
- 回退：保留 Python 服务路径，配置切换

### 任务 2.4 重排器（可选开关，默认关）
- Qwen3-Reranker-0.6B（Apache-2.0，MTEB-Code 73.42）或 bge-reranker-v2-m3（MIT）
- top-50 重排，预算 80~200 ms；**只在评测集证明有提升时才默认开启**
- 注意：独立测试显示重排在 5 类查询里 4 类"打平或输给好嵌入"——**别当默认**

**阶段 2 里程碑 M2**：评测集总分提升 + 重建索引不再需要手动起 Python 服务

---

## 5. 阶段 3：P2 能力扩展（按需，可并行）

| # | 任务 | 要点 | 来源 |
|---|---|---|---|
| 3.1 | **`grep` 工具** | ripgrep over `_SourceCode`，返回 `文件:行 + 上下文`；零 GPU 成本；对字面查询强于向量 | RAGFlow `grep_sed_narrow` |
| 3.2 | **MCP 现代化** | 官方 `ModelContextProtocol` 2.2.0 SDK 替换手写 JSON-RPC；加 resources（索引统计）、prompts（预置工作流）、tool annotations、SSE/HTTP 传输 | 官方 SDK |
| 3.3 | **索引自动化** | 接上死代码 `Indexer/Watcher.cs` 或加 `index --watch`；与 `Sync-VanillaSource.ps1` / `Refresh-ModSource.ps1` 串成链 | 现状缺口 |
| 3.4 | **图语义化重建** | `Indexer/GraphBuilder.cs` 从 `CSharpSyntaxTree.ParseText(chunk.Text)` 升级为 `SemanticModel`（加载游戏 `Managed\*.dll` 做真编译）→ 补静态/反射/别名引用 | 评估 §2.5 |
| 3.5 | **LazyGraphRAG** | 社区检测（Leiden/Louvain）+ 查询期注入相关社区摘要（**不需要** LLM 全量生成摘要） | 调研 §1 |
| 3.6 | **web 搜索 + Steam Workshop** | provider 抽象（**You.com 免 key 兜底** / Tavily / Brave）；keyless `ISteamRemoteStorage/GetPublishedFileDetails/v1`；`fetch_repo`（Gitingest → `_SourceCode\external\` → 增量索引） | 评估 §5.4 |
| 3.7 | **记忆层** | 会话/查询记忆（比 1.5 的结果缓存更持久） | RAGFlow `memory/` |

---

## 6. 依赖与顺序

```
阶段0 评测集 ──► 阶段1(P0) ──► M1 汇报 ──► 阶段2(模型) ──► M2 汇报 ──► 阶段3(能力)
                     │                                          │
                     └── 1.1/1.2 可独立先做（半小时级见效）      └── 3.1/3.3 可并行
```

- 若要**尽快看到效果**：先做 **1.1 + 1.2**（半小时~1 小时，14.34 s → ~0.3 s），再补阶段 0
- 若要**稳妥**：严格按 0 → 1 → 2 → 3

---

## 7. 风险与回滚

| 风险 | 缓解 |
|---|---|
| 改向量格式导致检索结果变化 | 新格式上线前用评测集比对：同查询结果集合必须一致（只允许延迟变化） |
| 重嵌入（65 分钟 GPU）失败 | 动手前 `robocopy index\vec index\vec.bak /MIR`；旧 JSONL 保留 |
| 换模型后掉分 | 保留 e5 路径与旧 `vec`，评测集对比后再切换；`--model` 可切回 |
| ONNX 数值不一致 | 同批文本比对 Python 与 ONNX 向量余弦 > 0.999 |
| 融合权重调参过拟合评测集 | 评测集留 10 条 holdout 不参与调参 |
| 并发下 searcher 缓存出问题 | 缓存用 `ConcurrentDictionary` + `Lazy`；Lucene `SearcherManager` 如需热更新再引入 |
| 动了 `Develop` 被索引的现状 | 排除是**配置项**，用户可自己决定是否排除自研 mod |

**回滚基线**：本次重建的 `index\`（lucene + vec + graph）在改动前完整备份一份到 `index.bak\`。

---

## 8. 工作分解总表

| 阶段 | 任务 | 预估 | 依赖 | 产出 | 验收 |
|---|---|---|---|---|---|
| 0 | 0.1 评测集 | 0.3 天 | — | `tests/retrieval-baseline.json` | 30 条含标注 |
| 0 | 0.2 跑分脚本 | 0.2 天 | 0.1 | `tools/bench-retrieval.ps1` | 一条命令出分 |
| **1** | **1.1 向量二进制化** | **0.3 天** | — | `VectorIndex.cs`/`IndexingPipeline.cs` | 冷启 < 1 s |
| **1** | **1.2 searcher 单例** | **0.2 天** | — | `RoughSearchTool.cs` | 带筛选 < 0.5 s |
| 1 | 1.3 混合检索(RRF/归一化) | 0.5 天 | 0 | `RoughSearcher.cs` | Recall@10 +15pt |
| 1 | 1.4 去重与排除 | 0.2 天 | — | `Chunker.cs` | 无重复条目 |
| 1 | 1.5 结果缓存 | 0.1 天 | 1.3 | `EmbeddingCache.cs` | 缓存命中 < 50 ms |
| 2 | 2.1 换 Qwen3-Embedding | 1 天 + 65 min GPU | M1 | `embedding_server.py` | 评测集提升 |
| 2 | 2.3 ONNX 进进程 | 2 天 | 2.1 | 新 `Embedding/*.cs` | 删掉 Python 服务 |
| 2 | 2.4 重排器(可选) | 1 天 | 2.1 | `Reranker.cs` | ≥ 基线才默认开 |
| 3 | 3.1 `grep` 工具 | 0.5 天 | — | 新 MCP 工具 | 字面查询可用 |
| 3 | 3.2 MCP SDK + SSE | 2 天 | — | `McpServer.cs` 重写 | 协议 2025+ |
| 3 | 3.3 索引自动化 | 1 天 | — | `Watcher.cs` 接上 | 改码 2 min 内生效 |
| 3 | 3.4 图语义化 | 3 天 | 0.1 | `GraphBuilder.cs` | 静态引用边 + |
| 3 | 3.6 web/Workshop 工具 | 2 天 | — | 新 MCP 工具 | 免 key 可查工坊 |

---

## 9. 第一步（下次开工直接照做）

1. `robocopy index index.bak /MIR`（备份当前可用索引）
2. 做 **1.1 向量二进制化**（或先做 **1.2 searcher 单例** —— 20 分钟见效，风险最低）
3. 用 `kind='cs'` 查询验证：14.34 s → < 0.5 s

---

## 附录 A. 改造前评测基线

> 阶段 0 完成后填写：日期 / 版本 / Recall@5 / Recall@10 / MRR / 平均延迟 / 失败用例清单

| 日期 | 版本 | Recall@5 | Recall@10 | MRR | 平均延迟 | 备注 |
|---|---|---|---|---|---|---|
| 2026-09-16 | 重建后未优化 | 待测 | 待测 | 待测 | 冷 15.34 s / 热 0.26 s | 参数变更 14.34 s |
