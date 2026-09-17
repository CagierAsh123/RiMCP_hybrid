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

### 任务 0.5 在线监控（L3，最小可用）
- MCP 工具打一行 JSONL：`{ts, tool, query, kind, max, latency_ms, result_count, ok}`
- 派生指标：延迟 P50/P95、0 结果率、工具调用分布（`rough_search` → `get_item` 的转化率能直接反映"粗搜质量"）
- 加一个**金标冒烟测试**（5 条查询必须命中），改完代码 1 分钟跑完

**验收**：① 一条命令输出 L1 指标表（e5 基线已存档）；② L2 有 5 个任务的四维评分；③ 后续每次改动都能用同一套数字对比。
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
| 3.1 | **`grep` 工具** | ripgrep over `_SourceCode`，返回 `文件:行 + 上下文`；零 GPU 成本；对字面查询强于向量 | RAGFlow `grep_sed_narrow` |
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
