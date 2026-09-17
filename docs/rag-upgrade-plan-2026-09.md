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
| 冷启动查询（含索引加载） | **15.34 s**（`bench` 实测 12.2 s） | **< 2 s** |
| 热查询（同参数） | 0.26 s（`bench` 实测 p50 50 ms） | 保持 |
| 检索质量（评测集 Recall@10） | **0.5000**（2026-09-17 实测，见 §10.1） | **+15 个百分点** |
| 打开/关闭某 mod 源码 | 手动全量重跑（81 分钟） | **自动增量，< 2 分钟** |
| 部署依赖 | .NET + Python/Flask 嵌入服务 | 单 .NET 进程（Python 降为可选回退） |

### 0.2 非目标（明确不做，防止跑偏）

1. **不引入向量数据库/ANN**（Qdrant/LanceDB/Milvus/FAISS）：167k 规模下真实检索已 0.26 s，瓶颈是序列化不是算法
2. **不搬 RAGFlow 那套 Docker 重栈**（ES/Infinity + MinIO + Redis + MySQL，≥16 GB 内存）：为 2 GB 索引引入十倍复杂度
3. **不做 LLM 生成层**：RiMCP 的定位是"检索/导航工具"，生成交给调用它的 agent
4. **不做 DeepDoc 式文档解析**：代码有 Roslyn，AST 比任何文档解析器更懂代码
5. **不做联网/出网能力**：联网搜索与 GitHub 检索属于 **DeepSeek Harness** 的范畴（另行委派其他会话推进），RiMCP 保持纯本地（评估报告 §5 已据此改写为"不在本项目范围"）

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

## 2. 阶段 0：建立评测体系与基线（1 天）

> 方法论参考：RAGAS 四维（忠实度 / 答案相关性 / 上下文召回 / 上下文精确）+ 生产级监控
> （`https://www.smallyoung.cn/docs/031-RAG质量评估RAGAS与生产级监控实践`）。
> **但 RiMCP 没有生成层**（它只检索、不生成答案），所以不能照搬 RAGAS：把它拆成"确定性检索指标（主）+ 子代理端到端 + 在线监控"三层。

### 为什么分三层

| 层 | 测什么 | 怎么测 | 成本 | 可归因性 |
|---|---|---|---|---|
| **L1 检索层（主指标）** | 检索器行不行：该找到的找到没、排得靠前没 | 标注查询集 + **确定性 IR 指标（不用 LLM）** | 零 | ✅ 能精确归因到每次改动 |
| **L2 端到端层** | agent 拿这套工具能不能把活干成 | **子代理当"用户" + 子代理当"评委"**（LLM-as-Judge） | 中 | 只看趋势 |
| **L3 在线监控** | 真实使用中的延迟/失败/使用模式 | MCP 调用日志 + 延迟分位 + 失败分类 | 零 | 持续观测 |

> RAGAS 的 Faithfulness / Answer Relevancy 评的是**答案**，那是 agent 层（DSH + LLM）的事；
> 只有 Context Recall / Context Precision 是检索器的指标 —— 正好落在 L1。

### 任务 0.1 标注查询集（`tests/retrieval-baseline.json`）
- **40 条**查询，5 类各 8 条：① 精确符号名 ② Def 名 / DefType ③ 自然语言机制提问 ④ 跨层依赖 ⑤ 中英混合 + mod 相关
- 每条字段：`query` / `kind` / `expected`（1~5 个正确 itemId 或 symbolId）/ `category` / `note`
- 期望项来源：**从排除 Develop/rjw 后的干净索引**里人工挑选，并用 `get_item` 核对存在性
- 留 **10 条 holdout** 不参与调参，防止对着评测集过拟合

### 任务 0.2 跑分脚本（`tools/bench-retrieval.ps1`）
**确定性指标（主）** —— 直接对应 RAGAS 的检索侧两项：

| 指标 | 定义 | 对应 RAGAS |
|---|---|---|
| **Recall@5 / Recall@10** | 期望项集合被 top-k 覆盖的比例 | Context Recall |
| **MRR** | 首个命中位置的倒数均值 | — |
| **nDCG@10** | 位置加权的相关性总和 | — |
| **Context Precision@k** | 各位置 precision@k 的加权平均（RAGAS 口径） | Context Precision |
| 平均 / P95 延迟、0 结果率 | — | L3 的失败率 |

- 支持 `--baseline <file>` 存档、`--compare <file>` 与上次出差异表

### 任务 0.3 e5 基线（**必须在 1.4 改 chunk 集合之前跑完**）
- 用「**当前 e5 模型 + 已排除 Develop/rjw 的干净索引**」测一遍，存档 `tests/baseline-e5.json`
- 这是唯一机会：1.4 之后 chunk 集合变了，旧基线无法复现

### 任务 0.4 端到端评测（L2，用子代理）
- **被测**：给一个子代理真实任务（例："`CompExplosive` 的爆炸伤害是怎么算的？"），**只给它 RiMCP 的 MCP 工具**，让它作答并列出引用过的 `itemId`
- **评委**：另一个子代理按下表打分（评委必须先看证据、再看结论）

| 维度 | 判定方式 | 对应 RAGAS |
|---|---|---|
| 忠实度 | 答案里每条论断能否在它 `get_item` 取回的代码里找到依据 | Faithfulness |
| 相关性 | 是否直接回答了原问题 | Answer Relevancy |
| 上下文召回 | 检索到的东西是否覆盖完成任务所需的全部信息 | Context Recall |
| 上下文精确 | 有用结果是否排在前列（而不是在噪声里翻找） | Context Precision |

- 只跑 **5~10 个任务**；**人工复核评委结论**（LLM 评委有噪声，不能当唯一依据）

> **状态（2026-09-17）**：0.1 / 0.2 / 0.3 已完成，实测数据见 §10。0.4 / 0.5 待做。
> 实际产物：`tests/retrieval-baseline.json`（40 条标注）、`tools/bench-retrieval.ps1`（封装 C# `bench` 子命令）、
> `tools/Resolve-SymbolIds.ps1`（把名字解析成真实 symbolId）、`tests/baseline-e5.json`（基线存档）。

### 任务 0.5 在线监控（L3，最小可用）✅
- MCP 工具打一行 JSONL：`{ts, tool, query, kind, max, latency_ms, result_count, ok}` ✅（见 §13）
- 派生指标：延迟 P50/P95、0 结果率、工具调用分布（`rough_search` → `get_item` 的转化率能直接反映"粗搜质量"）✅
- 加一个**金标冒烟测试**（5 条查询必须命中），改完代码 1 分钟跑完 ✅ `tools/smoke-gold.ps1`（见 §16）
  —— 这个冒烟测试是 §15 那次"静默返回 0 结果"事故的产物：完整评测跑十几轮都没发现，
  它 1 分钟就能报红

**验收**：① 一条命令输出 L1 指标表（e5 基线已存档）→ `tools/bench-retrieval.ps1` ✅；
② L2 有 5 个任务的四维评分 → 协议与 8 条任务已就绪（`tests/l2-tasks.json` + `docs/l2-eval-protocol.md`），
**待换模型后首次运行**；③ 后续每次改动都能用同一套数字对比 ✅（`--out` + `--compare`）。
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

### 任务 1.4 排除与去重（**Develop / rjw 全程排除**）
- 目标：① `Develop\`（自研 mod；且 `private/` 副本与公开版各存一份 → 同分重复条目）与 `rjw\`（代码过老、屎山，用户另开项目处理）**不进主库**；② 同 `SymbolId` 多路径去重
- **关键设计：排除表必须"索引侧 + 读取侧"同时生效**
  - 索引侧：`Indexer/Chunker.cs` 的 `IsIndexableFile`（第 525 行）读排除表 → 不再产出这些 chunk（Lucene / 图自动变小）
  - **读取侧：`Retrieval/VectorIndex.cs` 的 `Load`（第 24 行起）按路径过滤**
    → **这样排除不需要重新嵌入！** 否则 rjw/Develop 的向量仍留在 `vectors.jsonl` 里，语义那一路照样会把它们排上来（只重建 Lucene 是没用的）
- 排除表：`index\exclude.json`（索引器与检索器读**同一份**），默认 `["\\Develop\\", "\\rjw\\"]`，路径匹配大小写不敏感
- 去重：同 `SymbolId` 保留优先级 = **非排除路径 > 非 `private/` > 路径短者**
- 验收：`rough_search("Patch_GetFood")` 不再出现 Develop 的重复项 —— ✅ 2026-09-17 已达成（见 §10）
  - **修正**：原验收里"`rjw` 相关查询 0 命中"**不可实现也不该追求**。排除的是 `_SourceCode\rjw\` 这个
    **mod 目录**；别的 mod 自己声明的 `<rjw.RaceGroupDef>`（如 Yuran race）、或代码里引用
    `"rjw.JobDriver_Sex"` 的常量（如 RimTalk），属于**那些 mod 的内容**，不该被抹掉。
    正确验收是"检索结果里不再出现 `_SourceCode\rjw\` 下的文件"。
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
| 3.1 | **`grep` 工具** ✅ | ripgrep over `_SourceCode`，返回 `文件:行 + 上下文`；零 GPU 成本；对字面查询强于向量 | RAGFlow `grep_sed_narrow` |
| 3.2 | **MCP 现代化** | 官方 `ModelContextProtocol` 2.2.0 SDK 替换手写 JSON-RPC；加 resources（索引统计）、prompts（预置工作流）、tool annotations、SSE/HTTP 传输 | 官方 SDK |
| 3.3 | **索引自动化** | 接上死代码 `Indexer/Watcher.cs` 或加 `index --watch`；与 `Sync-VanillaSource.ps1` / `Refresh-ModSource.ps1` 串成链 | 现状缺口 |
| 3.4 | **图语义化重建** | `Indexer/GraphBuilder.cs` 从 `CSharpSyntaxTree.ParseText(chunk.Text)` 升级为 `SemanticModel`（加载游戏 `Managed\*.dll` 做真编译）→ 补静态/反射/别名引用 | 评估 §2.5 |
| 3.5 | **LazyGraphRAG** | 社区检测（Leiden/Louvain）+ 查询期注入相关社区摘要（**不需要** LLM 全量生成摘要） | 调研 §1 |
| 3.6 | **记忆层** | 会话/查询记忆（比 1.5 的结果缓存更持久） | RAGFlow `memory/` |

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
| 2026-09-17 | e5-base-v2 / 已排除 Develop+rjw / 纯语义 | 0.4750 | 0.5000 | 0.3853 | 热 68 ms（冷启 12.2 s） | `tests/baseline-e5.json`；nDCG@10 0.4110 / CP@10 0.3812 / Hits@1 12/40 |

## 10. 实测记录：阶段 0 完成（2026-09-17）

### 10.1 检索基线（`tests/baseline-e5.json`，40 条查询，10 条 holdout）

| 指标 | 值 |
|---|---|
| Recall@5 / @10 | **0.4750 / 0.5000** |
| 宽松 Recall@5 / @10（期望类型的成员也算命中） | 0.5375 / 0.5625 |
| MRR | 0.3853 |
| nDCG@10 | 0.4110 |
| Context Precision@10 | 0.3812 |
| Hits@1 | **12/40** |
| 0 结果率 | 0% |
| 热查询延迟 | avg 68 ms / p50 50 ms / p95 172 ms |
| 冷启动（每次 `new RoughSearcher`，解析 1.63 GB JSONL） | **12.2 s ×3 次**（按 `kind` 分组） |

**读数**：**一半的期望项从未被召回**，且 22 个缺失项全部通过了"标注自检"（`bench` 会用 Lucene 反查每个
`expected` 是否真存在）——所以这是纯粹的检索能力问题，不是标注错。

### 10.2 三个免费 A/B（不改代码，只翻开关）

| 运行 | Recall@10 | MRR | Hits@1 | 结论 |
|---|---|---|---|---|
| 基线（纯语义） | 0.5000 | **0.3853** | **12/40** | — |
| `--no-dedupe` | 0.5000 | 0.3853 | 12/40 | 去重对**本评测集无影响**（top-20 里没有同 SymbolId 重复项） |
| `--hybrid --semantic-k 100`（旧加法融合） | 0.4875 | **0.2398** | **7/40** | ⚠ **回退**：MRR −38%，Hits@1 12→7 |
| `--no-exclude` | 0.5000 | 0.3853 | 12/40 | 排除对**本评测集无影响**（这 40 条查询本就不命中 Develop/rjw） |

**最重要的一条**：**把 `UseSemanticScoringOnly` 直接翻成 `false` 是负优化**。这坐实了作者当年"分数相加失败"
的观察——BM25 与余弦量纲不同，不归一化就相加会把排序搅乱。所以任务 1.3 **必须**做归一化/ RRF，
且有了可对比的数字（目标：MRR 从 0.3853 往上，且不低于 0.3853）。

### 10.3 顺带修掉的 bug

| # | 问题 | 位置 | 影响 |
|---|---|---|---|
| 1 | 重复 `--force` 旗标被覆盖：`--force lucene --force graph` 只剩 `graph` | `Program.ParseOptions` | 计划里的标准命令**从未真正强制重建 Lucene**，只重建了 graph（静默半 rebuild） |
| 2 | `ExactRetriever` 只按 `item_id` 查，symbolId 一律查不到 | `Retrieval/ExactRetriever.cs` | `get_uses`/`get_used_by` 返回的 symbolId **读不出源码**；已加 symbolId 回退 + `Exists()` |
| 3 | `LuceneWriter` 的重复报告注释谎称"同 SymbolId 的文档会被 UpdateDocument 替换" | `Indexer/LuceneWriter.cs:219` | 实际 key 是 `item_id`，重复项**并未去重**；真正的去重在读取侧（1.4） |

### 10.4 排除生效数据

```
排除前：167,213 chunks / 830,791 边 / lucene 131 MB / graph.nodes 167,213 行
排除后：144,732 chunks（C# 125,155 + XML 19,577）/ 728,396 边 / lucene 106.3 MB / graph.nodes 144,732 行
chunk −13.4%，边 −12.3%，lucene −18.9%
枚举：21,979 个可索引文件，22,167 个被排除（Develop 8,895 + rjw 10,621 + 其它路径命中）
读取侧：`vectors.jsonl` 仍 1,626 MB（未重嵌入），加载时 skip 22,481 条（日志可证）
```

### 10.5 新发现的索引盲区（不修，只记录）

| # | 盲区 | 规模 | 处置 |
|---|---|---|---|
| 1 | `Chunker` 的 XML 过滤要求根元素名 **以 `Def` 结尾**（`Chunker.cs:436`），于是 `AlienRace.RaceSettings`、`AncotLibrary.ThingDef_Custom`、`PsychicRitualDef_*` 等 **DefType 整类不入库** | 24 个 DefType / **33 个 def（占 19,047 的 0.17%）** | 不值得为它作废基线；记入阶段 3 索引完整性 |
| 2 | ILSpy 的编译器生成类型（`<>z__ReadOnlyArray` 等）仍被索引 | 51 个文件 / **485 chunks（0.34%）** | 忽略；若要清理，chunker 跳过 `[CompilerGenerated]` 即可 |
| 3 | 空 SymbolId 的 chunk | **2 个** | 忽略（去重时已回退到 itemId 作 key） |

> 盲区 1 之所以要记录，是因为它属于"**检索器看起来更差、实际是语料缺失**"那类问题——
> 评测时若把 HAR 的 RaceSettings 当期望项，会得到假阴性。

### 10.6 ⚠ 分类拆解：短板不在"符号名"，而在"自然语言 + 中文 mod"（**最重要的一条**）

把 40 条按类别拆开，画面和总分完全不同：

| 类别 | Recall@5 | Recall@10 | MRR | 判读 |
|---|---|---|---|---|
| **exact-symbol**（精确符号名） | 0.875 | **1.000** | 0.688 | 已 **满分**，无提升空间 |
| **def-name**（Def 名/DefType） | 1.000 | **1.000** | 0.781 | 已 **满分**，无提升空间 |
| **natural-language**（自然语言机制提问） | 0.125 | **0.125** | 0.139 | **重灾区** |
| **cross-layer**（继承/角色类提问） | 0.250 | **0.250** | 0.257 | 重灾区 |
| **bilingual-mod**（中英混合 + mod） | 0.125 | **0.125** | 0.063 | **最差** |

三个必须据此修正的判断：

1. **不要为"符号名查询"做优化**。它们在 e5 + 纯语义下已经 100% 命中——1.3 拟做的"标识符识别时提高词法权重"
   对这类查询**不会带来召回增益**（已经满分），最多把 MRR 0.688 再抬一点。
   真正的 +15pt 必须来自 **natural-language + bilingual-mod + cross-layer**（这三类合计 24/40 条，当前 Recall@10 仅 0.167）。
2. **换 Qwen3-Embedding 的收益主要落在中文/mod 类**：e5-base-v2 虽是多语言模型但中文弱，
   `bilingual-mod` 只拿 0.125；Qwen3 系列中文显著更强 —— 这是模型替换**最可验证**的收益点。
3. **"50% 召回"这个总分有误导性**：它其实是"两类满分 + 三类几乎全灭"的平均。汇报必须带分类表，
   否则会误判优化方向（例如误以为该去补 BM25）。

> 结论：**评测集的价值在这一张表上就已经兑现了** —— 它在写第一行优化代码之前就否决了一个错误方向
> （"符号名查询失败所以要混合检索"）并指明了真正的靶子。

### 10.7 holdout 对照（防过拟合检查）

| 集合 | 条数 | Recall@5 | Recall@10 | 宽松 R@10 | MRR |
|---|---|---|---|---|---|
| 全部 | 40 | 0.4750 | 0.5000 | 0.5625 | 0.3853 |
| holdout | 10 | 0.5000 | 0.5000 | 0.6000 | 0.3833 |

holdout 与总体几乎一致 → 评测集分布均衡，**调参时用 30 条、验收时看 10 条 holdout** 的做法可行。

### 10.8 下一步顺序（据实测修正）

```
① 1.2 searcher / config 可变（带筛选 14.34 s → <0.5 s；风险最低、立刻见效）
② 1.1 向量二进制化（冷启 12.2 s → <1 s）
③ 1.3 混合检索归一化融合（目标：natural-language / cross-layer 上升，且总 MRR 不得低于 0.3853）
④ 1.5 结果缓存
⑤ 复测 → M1 汇报（必须带分类表）
⑥ 换 Qwen3-Embedding-0.6B + --force all（重点看 bilingual-mod 能否从 0.125 起飞）
⑦ L2/L3：子代理端到端 + MCP 调用日志
```

---

## 11. 实测记录：阶段 1 P0 完成（2026-09-17）

### 11.1 达标情况

| 任务 | 目标 | 实测 | 状态 |
|---|---|---|---|
| **1.1** 向量二进制化 | 冷启动 < 1 s | **12.50 s → 0.85 s**（14.7×） | ✅ |
| **1.2** searcher 单例 / 参数可变 | 带筛选 < 0.5 s | 参数不再触发重建；全程 **1 次索引加载** | ✅ |
| **1.3** 恢复混合检索 | Recall@10 +15pt | 融合 **只改排序不改召回**（见 11.3） | ⚠ 部分 |
| **1.5** 结果缓存 | 命中 < 50 ms | 已实现（200 条 LRU，键含全部参数）；`warmup=2` 验证命中 | ✅ |

### 11.2 新向量格式（1.1）

```
旧：vec/vectors.jsonl        1,626.3 MB   （每行一个 JSON，含 preview/signature/identifiers/vector）
新：vec/vectors.bin            489.9 MB   （24 字节头 + 定长 float32 平铺）
    vec/vectors.meta.jsonl      47.3 MB   （每行 {itemId, symbolId, path}）
                              ─────────
合计                           537.2 MB   （−67%）
```
- 头：magic `RWVEC001` + version + dim + count（24 字节），数据段零拷贝切成 `ReadOnlyMemory<float>` 喂 `TensorPrimitives.Dot`
- **只存检索真正用到的字段**：`preview`/`signature`/`identifiers` 检索路径从不读（预览走 Lucene 文档），`preview` 正是旧文件体积的大头
- **不用重新嵌入**：新增 `pack-vectors` 子命令把旧 JSONL 就地转换（167,213 行 / **10.6 秒**）
- legacy `vectors.jsonl` 仍可读（老索引不会坏）
- **验收：指标逐位一致** —— Recall@5 0.4750 / Recall@10 0.5000 / MRR 0.3853 / nDCG@10 0.4110 / CP@10 0.3812 / Hits@1 12/40，与基线完全相同，**只有延迟变了**（计划 §7 风险条要求）

### 11.3 ⚠ 1.3 的关键结论：融合改排序，不改召回

**候选来源诊断**（新增 `SearchDiagnostics` + `bench --diagnose`）把 45 个期望项拆开：

| 来源 | 数量 | 含义 |
|---|---|---|
| 只被词法腿捞到 | 5 | |
| 只被语义腿捞到 | 12 | |
| 两条腿都捞到 | 16 | |
| **两条腿都没有** | **12** | **候选生成问题，融合/重排救不了** |
| 捞到了但没进 top-N | 10 | 排序问题，融合能救 |

权重矩阵（40 条查询，全部配置 **Recall@10 = 0.5000、R-Recall@10 = 0.5625**）：

| 配置 | MRR | nDCG@10 | CP@10 | Hits@1 |
|---|---|---|---|---|
| 纯语义（基线） | 0.3853 | 0.4110 | 0.3812 | 12/40 |
| 归一化加权 0.5/0.5 | 0.3154 | 0.3476 | 0.3133 | 9/40 |
| **归一化加权 0.3/0.7（已定为默认）** | **0.4042** | **0.4280** | **0.4042** | **14/40** |
| 归一化加权 0.2/0.8 | 0.3958 | 0.4187 | 0.3917 | 13/40 |
| 归一化加权 0.4/0.6 | 0.3740 | 0.4050 | 0.3740 | 12/40 |
| RRF k=60 | 0.2387 | 0.2918 | 0.2357 | 6/40 |

三条硬结论：
1. **0.5/0.5 仍然比纯语义差** —— 即使归一化了，给 BM25 一半权重依然会稀释好的语义命中。**"归一化就够了"是错的**，权重必须调。
2. **0.3/0.7 是甜点**：MRR +4.9%、nDCG@10 +4.1%、CP@10 +6.0%、Hits@1 +16.7%（12→14），Recall@10 不变。
3. **RRF 明显更差**（MRR 0.2387）—— 在"一路强一路弱"的场景下，无差别融合会把弱路的排名灌进来。
4. **融合有代价**：top-20 内缺失的期望项从 22 升到 25（长尾覆盖换头部精度）。指标口径要看 Recall@10，别用"缺失计数"。

> **所以计划里"+15pt Recall@10"靠 1.3 拿不到。** 12 个期望项在任何一条腿里都不存在，
> 且其中 **6 个是中文 mod 类** —— 查询用的是 mod 名（"Humanoid Alien Races"），代码里是
> namespace/defName（`AlienRace`），**词表对不上**。这属于"候选生成"问题，需要查扩展/模型换代，
> 不是排序问题。已据此新增任务 1.6。

### 11.4 顺带确认/修正

- 计划里说 `GetItemTool` 每次 `new ExactRetriever` —— **不成立**，它本来就是 `Lazy<ExactRetriever>`（已缓存）。只有 `RoughSearchTool` 需要修。
- `Lucene.QueryParser` **不是线程安全的**（有可变解析状态）。searcher 变成长期存活 + 可并发后，必须改成 `ThreadLocal<QueryParser>`，否则并发查询会互相污染。已修。
- `tools/*.ps1` 含中文时**必须带 UTF-8 BOM**：本机 PowerShell 会把无 BOM 的 UTF-8 按 ANSI 解析，中文会把字符串引号"吃掉"，报成莫名其妙的语法错（`Unexpected token`）。已在两个脚本上补 BOM。
- 原生命令的 stderr 在 `$ErrorActionPreference='Stop'` 下会被当成终止性错误 → `bench-retrieval.ps1` 在调用 dotnet 前后临时切 `Continue`。

### 11.5 下一步（据实测修正）

```
① 1.6 中文 mod 名 → namespace/defName 的查询扩展（直击 12 个"两腿都没有"中的 6 个）
② 换 Qwen3-Embedding-0.6B + 一次 --force embed（4~6 h GPU；重点看 natural-language 与 bilingual-mod）
③ 4.x 观察：融合把长尾挤出 top-20 → 考虑重排器（只在评测集证明有增益时才默认开）
④ L2/L3：子代理端到端 + MCP 调用日志
```

---

## 12. 任务 1.6（mod 词表）：两个负结果 + 换模型准备（2026-09-17）

### 12.1 动机

§11.3 的诊断显示：45 个期望项里 **12 个两条腿都没捞到**，其中 **6 个是中文 mod 类**。
根因是**词表不匹配**——查询用 mod 显示名（"Humanoid Alien Races"），代码里是 `AlienRace` / `HAR_`。
直觉的解法是"把 mod 的标识符追加到查询里"。

### 12.2 做法：`index/mods.json` 别名/词表

新增 `Common/ModCatalog.cs` + `Indexer/ModCatalogBuilder.cs` + `build-mod-catalog` 子命令（10 秒，不用重建索引）。
每个 mod 记录：**别名**（目录名按 `中文名（English Name）` 拆开 + 该 mod 最可信的单段命名空间）、
**命名空间**、**DefType**、**defName 前缀**。

两个必须的过滤（第一版都踩了）：
- **通用命名空间必须剔除**：`Verse` / `RimWorld` / `System.*`（按"出现在 ≥25% 的 mod 里"自动判定 doc frequency），
  否则给 NewRatkinPlus 扩展出 `Verse RimWorld`，把原版内容全拖进来
- **前缀的分母是 XML chunk 数，不是总 chunk 数**：Vehicle Framework 有 8,773 个 chunk（多数是 C#），
  用总数当分母会把 `VF_` 直接滤掉（第一版 `defPrefixes` 全空就是这个 bug）
- 别名只取**最可信的一个命名空间**，因为反编译出的 Harmony patch 会住在 `PatchOperationTryAdd`
  这种"命名空间"里，当别名会误命中

### 12.3 结果：两条路都不work，都不进默认

| 方案 | Recall@10 | MRR | 两腿都没捞到 |
|---|---|---|---|
| 基线（无 mod 处理） | 0.5000 | 0.4042 | 12 |
| 词法扩展 `--mod-expand lexical` | 0.5000 | 0.4042 | **13** |
| 两腿都扩展 `--mod-expand both` | 0.5000 | **0.4021** | **13** |
| 语义路路径加权 `--mod-boost 0.1` | 0.5000 | 0.4042 | 12 |
| `--mod-boost 0.2` | 0.5000 | 0.4042 | 12 |
| `--mod-boost 0.35` | 0.5000 | 0.4042 | 12 |

**为什么词法扩展反而更差**：BM25 用 **OR** 组合词项。追加 ~30 个词项后，目标 chunk 只命中其中一两个，
它占的分值比例被**稀释**了，排名反而掉。经典 query-expansion 陷阱。

**为什么路径加权"看起来在工作但指标不动"**：逐条 diff 证明它确实改变了排序
（bl06 从 NewRatkin 的补丁变成 HAR 自己的 def，bl04 结果全变成 Vehicle Framework），
但它是**均匀**加成——不改变 mod 内部的相对顺序，所以目标 chunk 在 mod 内排在 300 名的话，
加成后仍在 300 名。改动的是"展示哪个 mod 的东西"，不是"找没找到正确的东西"。

> **结论**：查询侧的 mod 技巧都拿不到召回增益，两者默认关闭
> （`ModExpansion = None`、`ModPathBoost = 0.0`）。机制和词表保留（`mods.json` 对人工排查有用），
> 但不再往这条路上加码。剩余缺口是**语义/多语言**问题 → 交给模型换代（§13）。

### 12.4 换 Qwen3 的准备：三个非显然的坑

**坑 1：`SentenceTransformer` 默认以 float32 加载，慢一倍多**
Qwen3-Embedding 权重是 **bfloat16**，但不显式指定 dtype 时 `from_pretrained` 会升成 fp32：

| 配置 | 显存（空闲） | 实测吞吐（874 token/块） |
|---|---|---|
| fp32, batch 8 | 7,617 MiB | 3.5 chunks/s |
| fp32, batch 16 | — | **2.3 chunks/s（更慢）** |
| fp32, batch 32 | — | **崩溃（OOM）** |
| **bf16, batch 16** | **1,781 MiB** | **6.4 chunks/s** |

fp32 的 2.4 GB 权重 + fp32 激活把 8 GB 显存吃满，所以"批次越大越慢"、到 32 直接崩。
显式传 `torch_dtype` 后显存降到 1.8 GB，吞吐 **1.83×**。
（`probe_embedding_throughput.py` 就是为此写的——先量再跑，别拿 6 小时赌。）

**坑 2：批次内存随"最长序列"走，固定批大小会在长 XML def 上 OOM**
padding 是逐批的，`batch_size × 批内最长长度` 决定显存。加了 **token 预算分批器**
（`_budgeted_batches`，默认 8,192 token），按"条数 ≤16 且总长 ≤预算"切。
12k 字符的长输入实测把显存推到 7.7 GB 仍不崩。

**坑 3：`HttpClient` 默认超时会掐断健康的重嵌入**
`EmbeddingServerClient` 原本统一 120 s。索引一批 1024 块要 ~170 s → 必然超时。
查询路径要"快速失败"，批处理路径要"允许几分钟"，所以把超时改成参数：
查询仍 120 s，**批处理 30 min**。

### 12.5 重嵌入启动参数与实测 ETA

```
python/embedding_server.py --model models/Qwen3-Embedding-0.6B --port 5001 \
    --max-length 2048 --st-batch-size 16 --st-token-budget 8192 --dtype auto
index --root B:\rimworld-code\_SourceCode --vec index\vec ... --embedding-server http://127.0.0.1:5001 --python-batch 256
```

- **端口 5001**（不是 5000）：e5 仍在 5000 服务线上 MCP，重建期间工具不降级
- `index\vec` → 改名为 `index\vec.e5`；线上 MCP 进程已把向量读进内存，**重命名不影响它**
- `index\vec.e5bak` 是完整备份（2,164 MB）

**实测速率与 ETA（重要修正）**：合成探测（874 token/块）给的是 6.4 chunks/s → 6.2 h，
但**真实语料实测 21.3 chunks/s**（3,840 块 / 180 s）→ **约 1.9 小时**。
说明真实 chunk 平均远短于 874 token（大量小 C# 成员）。
**教训：合成吞吐探测只能给下界，真值要看真实语料的进度计数。**

### 12.6 验收清单（重嵌入完成后执行）

**一条命令**：`tools/validate-model-swap.ps1 -EmbeddingServer http://127.0.0.1:5001`
（依次校验服务维度/dtype/prompt → 校验 `vectors.bin` 头与实际长度 → 冒烟测试 → 与 e5 基线对比，
任一步失败即停）。手动核对项：

1. `/health` 确认 `dim=1024`、`dtype=torch.bfloat16`、`prompts=[document, query]`
2. `smoke_embedding_server.py` 全过（含"query 与 passage 必须不同"这条）
3. `bench --diagnose` 对比 `tests/baseline-e5.json`：重点看 **bilingual-mod** 能否从 0.125 起飞
4. 重嵌入后 `vectors.bin` 应为 144,732 × 1024 × 4 ≈ **565 MB**
5. 把 5000 端口的 e5 服务换成 Qwen3（这样 MCP 配置不用改）

### 12.7 顺带修的一个真问题：索引写到一半会被读到

重嵌入跑到一半时，`index\vec\vectors.bin` 的头部还是占位符（`dim=0`）。
此时若 MCP server 重启，`ReadHeader` 会抛 "Corrupt packed vector header" ——
**几小时的重嵌入窗口里工具一重启就死**。

改为写 `vectors.bin.tmp` / `vectors.meta.jsonl.tmp`，**全部写完再 `File.Move` 换入**；
失败时删临时文件并抛出。这样任何时刻磁盘上的索引都是完整的。
（Lucene 与 graph 还有同类问题，记入阶段 3。）

---

## 13. 任务 0.5 在线监控（L3）实现完成（2026-09-17）

### 13.1 打点位置

`McpServer.HandleToolsCallAsync` 是**所有工具调用的唯一咽喉点**，遥测挂在这里，
不在每个工具里重复实现。每次调用追加一行 JSONL 到 `index\logs\mcp-tool-calls.jsonl`：

```json
{"ts":"2026-09-17T22:10:11.123+08:00","tool":"rough_search","ok":true,"ms":210.5,"n":12,
 "query":"pawn hunger tick","kind":"cs","max":20}
```
字段刻意取短（文件无上限增长）：`ts/tool/ok/ms/n/query/symbol/kind/max/lines/err`。

约定：
- 遥测**绝不允许影响工具调用**——每次写入都包在 try 里，写失败即自我禁用并只报一次 stderr
- `RIMWORLD_TELEMETRY=0` 关闭；`RIMWORLD_TELEMETRY_PATH` 改路径
- `n`（结果数）从工具返回值里提：`rough_search` 用 `totalFound`、`get_uses/get_used_by` 用
  `totalCount`、`get_item` 单条为 1、错误对象为 0

### 13.2 汇总：`telemetry` 子命令

```
RimWorldCodeRag telemetry --path src\RimWorldCodeRag\index\logs\mcp-tool-calls.jsonl [--since 24]
```

输出：调用数、错误率、**延迟 avg/p50/p95/max**、工具分布、`rough_search` 的 **0 结果率**、
以及 **`rough_search` → `get_item` 转化率**（"搜索给出的东西 agent 真的会去读"的最近似代理指标），
外加"返回 0 结果的查询清单"——**这是评测集新条目的直接来源**。

已用合成日志验证过一遍（6 条调用，含 0 结果、错误、慢查询各一）。

### 13.3 与 L1/L2 的分工

| 层 | 回答的问题 | 触发时机 |
|---|---|---|
| L1 `bench` | **能不能**找到（确定性、可归因） | 每次改动后 |
| **L2 子代理** | agent 用这套工具**能不能把活干成** | 里程碑 |
| **L3 `telemetry`** | 真实使用中**到底怎么样**（延迟/失败/使用模式） | 持续 |

L3 的另一半价值：它记录的 0 结果查询是**真实分布**，比人工拟的评测集更贴近实战。

---

## 14. 任务 3.1 `grep` 工具完成（2026-09-17）

### 14.1 为什么向量索引之外还需要一个字面搜索

嵌入擅长**语义**、不擅长**精确字符串**。"`rjw.JobDriver_Sex` 在哪里被引用了"、"哪些 XML 设置了
`<workerClass>`"、"这个方法上有几个 `[HarmonyPatch]`"——这些用一次文本扫描既便宜又精确，
走余弦相似度则不可靠。所以 `grep` 与 `rough_search` 是**互补**关系，不是替代。

### 14.2 实现

- **纯托管代码，不依赖 ripgrep**：`Search/GrepSearcher.cs`（引擎）+ `Tools/GrepTool.cs`（MCP）+ CLI `grep`（便于不起服务就测）
- **与索引共用同一份排除规则**（`PathExclusionFilter`）：一个会返回已排除 mod 的 grep 会和索引自相矛盾
  —— 实测 `grep 'rjw.JobDriver_Sex'` 只命中 RimTalk，**不返回 `_SourceCode\rjw\` 下的任何文件**
- **源码根自动发现**：索引器把源码根写进 `index\meta\source-root.txt`，工具读取它
  （可用 `RIMWORLD_SOURCE_ROOT` 覆盖），所以工具配置不会和它所属的索引漂移
- 默认跳过 `Languages/` 与 `DefInjected/`（纯翻译，数量盖过源码），但**保留 `About/` 与 `Patches/`**
  （mod 元数据与 PatchOperation 是真答案）；`--include-translations` 可放开

### 14.3 实测

| 场景 | 结果 |
|---|---|
| 字面 `rjw.JobDriver_Sex` | 2 处，均在 RimTalk；**无 rjw\ 路径** |
| 正则 `HarmonyPatch\(typeof` + `*.cs` | 正确行号列号 |
| 限定 `--path 'Vehicle Framework'` | **35 ms**（优化前 1,444 ms，**41×**）——路径命中目录时只遍历该子树 |
| `--glob '*.xml'` 搜 `VF_VehicleIsMoving` | 命中 `Parameters_General.xml`（正是 bl04 的期望 def） |
| 全树未命中 | 22,251 文件 / **2.3 s** |

### 14.4 工具分工（写进工具描述，让 agent 自己选）

| 诉求 | 该用哪个 |
|---|---|
| 知道确切标识符/字符串/属性，要**全部**出现处 | `grep` |
| 用自然语言描述机制、不知道标识符 | `rough_search` |
| 要读某个符号的完整源码 | `get_item` |
| 要继承/引用关系 | `get_uses` / `get_used_by` |

---

## 15. 事故与加固：MCP 工具静默返回 0 结果（2026-09-17 夜）

### 15.1 怎么发现的

用一个子代理做"能不能调用 RiMCP MCP 工具"的能力探测（为 L2 端到端评测铺路）。
子代理回答**能调用**，但顺手带回一条：
`rough_search("CompPowerTrader")` → `{"results":[],"totalFound":0,"queryTime":"0.19s"}`。
一个精确类型名返回 0 结果，而它在评测集里是 100% 命中的 —— 立刻自查，确认**所有查询都是 0 结果、0.06 s**。

> **这本身就是一次"意外收获"**：L2 探测顺带发现了一个线上故障，而 L1 评测（跑 CLI）**完全没发现**
> —— 因为 CLI 每次都是新进程、新配置。**跨进程状态才是这类故障的藏身处。**

### 15.2 诊断过程

| 检查 | 结果 | 结论 |
|---|---|---|
| e5 服务（5000）/ Qwen3 服务（5001）健康 | 都 healthy | 嵌入服务没问题 |
| `index\vec.e5`（e5 向量）内容 | `vectors.bin` 489.9 MB 完整 | 数据没问题 |
| **用当前代码 + e5 索引跑 CLI** | `CompPowerTrader` **正确命中**（score 1.000） | **代码与数据都没问题** |
| 运行中的 MCP 进程启动时间 | 21:10:13 | 是那个**长期存活实例**自身进入坏状态 |

⇒ 故障**局限于那个长驻进程**。但真正该改的是它**失败的方式**：不是报错，而是**静默返回空**。

### 15.3 三处加固（都已验证）

| # | 位置 | 改动 | 效果 |
|---|---|---|---|
| 1 | `VectorIndex.Load` | 两种格式都不存在时**抛异常**（原来静默返回空索引） | 缺索引立刻报错并给出重建命令；原来表现为"搜什么都搜不到" |
| 2 | `RoughSearcher.RetrieveAsync` | 查询嵌入失败**不再让整个查询失败**，告警后只用词法腿 | 挂掉嵌入服务 = 丢一半信号，而不是丢全部 |
| 3 | `MergeResults`（semantic-only 分支） | 语义腿为空时**回退到词法结果**并告警 | 原来 0 结果，现在 1000 条候选可用；这正是本次事故的形态 |

验证：
```
缺向量目录      -> 明确报错 + 重建命令（exit 1）
嵌入服务挂掉    -> WARNING: could not embed the query ...; continuing with the lexical leg only
                   WARNING: semantic-only requested but the semantic leg returned nothing
                   (144732 vectors loaded); falling back to 1000 lexical candidate(s)
正常路径        -> semantic score=1.000（不变）
```

### 15.4 回归证据：加固没有改变任何指标

`tests/regression-hardening.json`（e5 + 纯语义，与基线同配置）：

| | Recall@5 | Recall@10 | R-Recall@10 | MRR | nDCG@10 | CP@10 | Hits@1 |
|---|---|---|---|---|---|---|---|
| 基线 `baseline-e5.json` | 0.4750 | 0.5000 | 0.5625 | 0.3853 | 0.4110 | 0.3812 | 12/40 |
| 加固后 | 0.4750 | 0.5000 | 0.5625 | 0.3853 | 0.4110 | 0.3812 | 12/40 |

**逐位一致。**

### 15.5 遗留

运行中的那个 MCP 实例在**重嵌入完成并重启之前**仍会返回空结果 —— 这是模型换代的既定代价
（用户在计划里已接受重建窗口）。重嵌入一结束就重启，届时同时拾取 Qwen3 索引 + 本节的加固 + `grep` 工具。

**教训**：这次事故本可以在 30 秒内被 L3 遥测发现（0 结果率飙升）。它再次说明
**L1（离线、可归因）不能替代 L3（在线、看真实进程）**。

---

## 16. 任务 0.5 收尾：金标冒烟测试（2026-09-17）

### 16.1 它存在的唯一理由：1 分钟抓住"整类失效"

`tools/smoke-gold.ps1` + `tests/smoke-gold.json`（5 条查询）。它**不衡量质量**
（质量看 40 条的完整指标），只拦这一类故障：**所有查询都返回 0 结果**。
这类故障的表现太像"语料里没有"，而完整评测要跑 40 条、还得人工比对才发现异常 ——
§15 那次线上事故就是这样漏过去的。

断言（任一不过即 exit 1）：
1. **没有标注问题**（每个 `expected` 必须真实存在于索引里 —— 否则测试本身在骗人）
2. **Recall@10 == 1.0**（5 条全中）
3. **每条查询结果数 > 0**（把"整条查询空"和"排序偏了"区分开）

选材覆盖三条不同路径：vanilla C# 精确名（3 条，含上次出事的 `CompPowerTrader`）、
vanilla XML Def、**mod XML Def**（验证排除规则没误伤 mod 内容）。

### 16.2 正负两条路径都验证过

```
正：SMOKE PASS  Recall@10 = 1.0  Hits@1 3/5  p50 69 ms      (e5 索引)
负：bench 失败（exit=1）
    [bench] could not load the index: No vector index in '...\vec.does-not-exist'.
    Expected 'vectors.bin' + 'vectors.meta.jsonl', or the legacy 'vectors.jsonl'.
    Rebuild with: index --root <source> --vec <dir> --force embed
    FAIL: bench 没有产出报告，冒烟测试无法判定
    + 常见原因清单（索引缺失 / 嵌入服务没起 / 排除规则太宽 / kind 过滤滤空）
```

顺带把 `bench` 里未捕获异常喷栈的观感修掉（现在是一行可操作的错误 + exit 1）。

### 16.3 `bench-retrieval.ps1` 增加 `-VecDir` / `-IndexDir` / `-Exe`

换模型期间索引正在被重写、或要回跑旧向量（`vec.e5`）时，不必等索引写完也能评测。

---

## 17. 换端口运行手册（重嵌入完成后照抄，别临场即兴）

### 17.1 一个必须先知道的进程事实

本机 python 是 **venv shim + 基础解释器** 两个进程：

```
pid=1140   .venv\Scripts\python.exe        embedding_server.py ... --port 5000   <- shim（父）
pid=15548  A:\python\Python-3.10\python.exe embedding_server.py ... --port 5000  <- 真正持有端口（子）
```

- **venv 的 `python.exe` 是个 launcher**，它会 re-exec 基础解释器（`A:\python\Python-3.10\python.exe`），
  靠 `pyvenv.cfg` 让 `sys.prefix` 指向 venv，所以 **venv 里的包仍然可见**
- 判断"谁占着端口"要看 `Get-NetTCPConnection` 的 `OwningProcess`（是子进程）；
  **停服务要杀父（shim）**，子进程随之退出

### 17.2 致命的操作顺序陷阱

**Qwen3 服务必须用 venv 的 python 启动**（`src\RimWorldCodeRag\.venv\Scripts\python.exe`）。
`A:\python\Python-3.10\python.exe` 是基础解释器，**它看不到 venv 里的 `sentence-transformers`** →
服务会**静默退回** legacy transformers 后端 → 用 mean pooling、不加 query instruction →
**质量静默掉一大截，而且不报错**。

险的是 `A:\python` 那个解释器**也有 torch**，所以它不会崩，只会悄悄变差。
这就是为什么 `smoke_embedding_server.py` 里那条断言（**同一段文本作为 query 和 passage 的余弦必须不等于 1**）
必须存在 —— 它是这个陷阱的唯一警报。

### 17.3 命令

```powershell
# 0) 重嵌入结束后，Release 的 dll 解锁，先重新构建（脚本默认用它）
dotnet build src\RimWorldCodeRag\RimWorldCodeRag.csproj -c Release
dotnet build src\RimWorldCodeRag.McpServer\RimWorldCodeRag.McpServer.csproj -c Release

# 1) 验收（维度/dtype/prompt + vectors.bin 头与长度 + 冒烟 + 与 e5 基线对比）
.\tools\validate-model-swap.ps1 -EmbeddingServer http://127.0.0.1:5001

# 2) 把 5000 从 e5 换成 Qwen3（杀 shim 父进程，子进程随之退出）
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -like '*embedding_server.py*' -and $_.CommandLine -like '*--port 5000*' } |
  ForEach-Object { taskkill /F /PID $_.ProcessId }
# 确认 5000 已释放
Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue

# 3) 用 venv 的 python 在 5000 起 Qwen3（后台任务，日志可读）
$env:HF_HUB_OFFLINE='1'; $env:TRANSFORMERS_OFFLINE='1'
& .\src\RimWorldCodeRag\.venv\Scripts\python.exe .\src\RimWorldCodeRag\python\embedding_server.py `
    --model .\src\RimWorldCodeRag\models\Qwen3-Embedding-0.6B `
    --port 5000 --max-length 2048 --st-batch-size 16 --st-token-budget 8192 --dtype auto

# 4) 必须核对：dim=1024 / backend=sentence-transformers / dtype=bfloat16 / prompts 含 query
Invoke-RestMethod http://127.0.0.1:5000/health

# 5) 重启 MCP（DSH 会自动重拉）；它会同时拾取 Qwen3 索引 + §15 加固 + grep 工具
Get-CimInstance Win32_Process -Filter "Name='RimWorldCodeRag.McpServer.exe'" |
  ForEach-Object { taskkill /F /PID $_.ProcessId }

# 6) 复测 + 对比（重点看 bilingual-mod 与 natural-language）
.\tools\bench-retrieval.ps1 -Label 'qwen3-0.6b / fused 0.3-0.7' -Out tests\baseline-qwen3.json `
    -Compare tests\baseline-e5.json -Hybrid -Weights '0.3,0.7' -Diagnose

# 7) 1 分钟冒烟（改完任何东西都该跑）
.\tools\smoke-gold.ps1
```

### 17.4 回滚

`index\vec.e5`（e5 向量）与 `index\vec.e5bak`（完整备份）都还在；把 5000 端口换回 e5 服务、
`index\vec` 换回 `vec.e5` 即可。`index.bak\` 是改动前（含 Develop/rjw）的完整索引。

---

## 18. 修复：增量索引时新 chunk 没有向量（2026-09-17）

### 18.1 问题（读代码时发现的，不是猜的）

```csharp
// IndexingPipeline.RunAsync，改动前
if (_config.ForceRebuildEmbeddings || !VectorIndexExists())
{
    await GenerateEmbeddingsAsync(fullChunks, ...);   // 只有强制重建 / 向量文件不存在时才嵌入
}
```

所以**增量跑索引时完全跳过嵌入**。而 `itemId` 里含 span 哈希 —— **任何编辑都会产生新 itemId**。
结果：被改动的代码拿到 Lucene 文档和图节点，却**没有向量**，在语义那一路彻底消失
（只能靠词法腿勉强捞到）。这直接让计划里"改源码后生效 < 2 分钟"变成"改完代码检索质量静默下降"。

### 18.2 修法：对账，而不是"要么全量要么不干"

新增 `Indexer/PackedVectorStore.cs`：

- `VectorManifest.TryRead` —— 只读头部 + `vectors.meta.jsonl`（**不读 490 MB 浮点数据**），
  得到 dim/count/每行的 itemId
- `PackedVectorWriter` —— 流式写入，写 `.tmp` 再 `File.Move` 换入（**任何时刻磁盘上的索引都是完整的**）；
  这条原子语义原先只在全量路径里有，现在两条路共用
- `CopyKeptRows` —— 流式拷贝仍然有效的行（`Seek` 跳过孤儿行，不整文件缓冲）

`IndexingPipeline` 的嵌入段改成：

```
强制重建 / 无向量文件  ->  全量嵌入（原逻辑）
否则                  ->  对账：
                          toEmbed = 有 chunk 但没有向量
                          stale   = 有向量但没有 chunk
                          两者都为 0 -> "Embeddings are up to date"（不写盘）
                          否则 -> 先探测首批维度，再 拷贝保留行 + 嵌入新行 + 清掉孤儿
```

**维度守卫**：模型换代时（768 → 1024）混写会静默写坏索引，所以先探测首批向量维度，
不一致就抛 `The embedding model changed dimension (768 -> 1024); ... Rerun with --force embed`，
**并且不改动任何文件**。

### 18.3 集成测试（`tools/test-incremental-embed.ps1`，全过）

在临时语料上跑真实索引（用 e5 服务当"768 维模型"、Qwen3 服务当"1024 维模型"）：

| 步骤 | 断言 | 结果 |
|---|---|---|
| 1 全量嵌入 | `vectors.bin` 头 dim/count 与 meta 行数一致 | dim=768 count=4=meta ✓ |
| 2 改一个文件后增量 | **新增向量行 ≥ 1**（旧代码这里是 0 = bug）且**孤儿被清掉** | count 4→5，+2/−1 ✓ |
| 3 只动 mtime 不动内容 | 走对账路径并报 "up to date"，行数不变 | count=5 ✓ |
| 4 指向 1024 维服务 | 必须拒绝，且**不写坏文件** | exit=1，dim/count 未变 ✓ |

顺带：`index` 的异常现在输出一行可操作错误（`[index] failed: ...`）而不是喷栈。

### 18.4 意义

这一条是"自动增量 < 2 分钟"（计划 §0.1）从**看起来能跑**变成**真的正确**的差别。
它也解释了为什么"改完代码后检索变差"这类问题很难查：检索器没坏、语料也在，
**只是新代码没有向量**。

---

## 19. 任务 3.3 索引自动化：`index --watch`（2026-09-17）

### 19.1 为什么现在才能做

计划里 3.3 的验收是"改码 2 min 内生效"。但 §18 之前，增量跑索引**根本不给新 chunk 嵌入**，
所以 `--watch` 只会让索引"看起来更新了、实际语义那一路全是旧数据"。
§18 修完，`--watch` 才成立。

### 19.2 实现

`index --watch [--watch-debounce <秒>]`：

1. 先跑一遍普通索引（保留用户传的 `--force`）
2. `FileSystemWatcher` 监听源码根（只看 `.cs`/`.xml`，其它扩展名直接忽略）
3. 事件合并成"脏标记 + 安静期"（默认 5 s）：编辑器保存一个文件会触发几十个事件，
   必须去抖，否则会连续跑十几次索引
4. 安静期结束跑一次增量 pass，然后继续等
5. `Ctrl+C` 退出；**单次 pass 失败不会杀掉循环**（只打错误）
6. `--force` 是**一次性**的：只作用于第一遍

### 19.3 ⚠ 实测中抓到的 bug：force 标志跨 pass 残留

第一次测出来的日志里，第二遍出现了 `[index] Forcing Lucene rebuild.` ——
原因是 `IndexingPipeline` 拿到的是**同一个 config 对象**，而第一遍
`ResolveExclusionFilter` 合法地把 `ForceRebuildLucene` 置了 true（因为刚创建 exclude.json）。
这个标志**跨 pass 残留**，于是**每次改文件都会删掉并重建整个 Lucene 目录**：

- 真语料上意味着"改一行代码 → 几分钟全量重建"（正好与"增量"相反）
- 重建期间 Lucene 目录被删，正在读它的 MCP server 会直接坏掉

修法：每遍 pass 前把三个 force 标志清零（`--force` 只作用于第一遍），并写进注释。

```
第二遍（修复后）：
[index] exclusion: [...] (signature 332856a6cdfc)
[chunker] 2 indexable files ...
[index] Incremental embeddings: 2 new chunk(s) to embed, 1 stale row(s) to drop (existing 4 rows).
[index] Vector index updated: 5 rows x 768d (added 2, dropped 1).
[watch] reindexed in 0.1s at 22:42:55
```

**没有** "Forcing Lucene rebuild"，且新 chunk 拿到了向量。

### 19.4 与计划目标的对照

| 计划 §0.1 目标 | 现状 |
|---|---|
| 打开/关闭某 mod 源码：手动全量 81 min → **自动增量 < 2 min** | `--watch` + 增量嵌入已可；真实语料的增量耗时待测（小语料 0.1 s） |
| 改码 2 min 内生效（§8 表 3.3） | 去抖 5 s + 增量 pass；**待用真实语料计时** |

> 注：真实语料的增量 pass 会重建**整个图**（`GraphBuilder` 每次都全量重建，约 11 min），
> 这是剩下的最大瓶颈 —— 已记入阶段 3，未在本次处理。
