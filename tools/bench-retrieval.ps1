#requires -Version 5.1
<#
.SYNOPSIS
  RiMCP 检索评测（阶段 0 任务 0.2）：跑标注查询集，输出确定性 IR 指标。

.DESCRIPTION
  薄封装，真正的指标计算在 C# 侧的 `bench` 子命令（一次进程内加载索引，跑完全部查询）。
  输出：Recall@5/@10、宽松 Recall（期望类型的成员也算命中）、MRR、nDCG@10、Context Precision@10、
  延迟 P50/P95、0 结果率，以及「标注自检」（expected 是否真的存在于索引里）。

.PARAMETER Queries
  标注查询集，默认 tests\retrieval-baseline.json。

.PARAMETER Out
  结果 JSON 输出路径。缺省只打印，不落盘。

.PARAMETER Compare
  与一份历史结果对比（输出指标差异表 + 逐条回归/提升清单）。

.PARAMETER Label
  本次运行的标签，写进 JSON，便于日后辨识（如 "qwen3-0.6b / hybrid"）。

.PARAMETER Hybrid
  用混合打分（UseSemanticScoringOnly=false）。默认是纯语义排序。

.PARAMETER Fusion
  融合算法：weighted（默认，min-max 归一化后加权和）或 rrf（倒数排名融合）。

.PARAMETER Weights
  加权和的权重 "lex,sem"，默认 0.5,0.5。实测 0.5/0.5 劣于纯语义，0.3/0.7 最优。

.PARAMETER Diagnose
  额外输出「候选来源诊断」：每个期望项分别在第几条被词法腿/语义腿捞到、融合后落到第几。
  用来区分「候选生成问题（两腿都没有）」和「排序问题（捞到了没排上来）」。

.PARAMETER SemanticK
  语义路候选数（默认 5）。

.PARAMETER LexicalK
  词法路候选数（默认 1000）。

.PARAMETER NoExclude
  关闭路径排除（用于量化排除对指标的影响；注意 Lucene 侧已物理重建，只影响语义路）。

.PARAMETER NoDedupe
  关闭 SymbolId 去重（用于量化去重的影响）。

.EXAMPLE
  .\tools\bench-retrieval.ps1 -Label baseline -Out tests\baseline-e5.json

.EXAMPLE
  .\tools\bench-retrieval.ps1 -Hybrid -Weights 0.3,0.7 -Label fused -Compare tests\baseline-e5.json

.EXAMPLE
  .\tools\bench-retrieval.ps1 -Diagnose -Hybrid -Weights 0.3,0.7 -Label diag
#>
[CmdletBinding()]
param(
    [string]$Queries = 'tests\retrieval-baseline.json',
    [string]$Out,
    [string]$Compare,
    [string]$Label = 'unlabeled',
    [switch]$Hybrid,
    [ValidateSet('weighted', 'rrf')]
    [string]$Fusion = 'weighted',
    [string]$Weights = '0.5,0.5',
    [switch]$Diagnose,
    [int]$SemanticK = 5,
    [int]$LexicalK = 1000,
    [switch]$NoExclude,
    [switch]$NoDedupe,
    [int]$MaxResults = 20,
    [int]$Warmup = 1,
    [string]$EmbeddingServer = 'http://127.0.0.1:5000'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot 'src\RimWorldCodeRag\bin\Release\net8.0\RimWorldCodeRag.dll'
$indexRoot = Join-Path $repoRoot 'src\RimWorldCodeRag\index'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到 $exe。先跑：dotnet build src\RimWorldCodeRag\RimWorldCodeRag.csproj -c Release"
}
if (-not (Test-Path -LiteralPath $Queries)) {
    throw "找不到查询集 $Queries（相对路径按当前工作目录解析）。"
}

$arguments = @(
    $exe, 'bench',
    '--queries', (Resolve-Path -LiteralPath $Queries).Path,
    '--lucene', (Join-Path $indexRoot 'lucene'),
    '--vec', (Join-Path $indexRoot 'vec'),
    '--embedding-server', $EmbeddingServer,
    '--max-results', $MaxResults,
    '--warmup', $Warmup,
    '--semantic-k', $SemanticK,
    '--lexical-k', $LexicalK,
    '--label', $Label
)

if ($Out) { $arguments += @('--out', $Out) }
else {
    # Always give bench an --out target: without it the full JSON report goes to stdout and
    # buries the metric table this wrapper exists to show.
    $arguments += @('--out', (Join-Path ([System.IO.Path]::GetTempPath()) 'rimcp-bench-report.json'))
}
if ($Compare) { $arguments += @('--compare', (Resolve-Path -LiteralPath $Compare).Path) }
if ($Hybrid) {
    $arguments += @('--hybrid', '--fusion', $Fusion, '--weights', $Weights)
}
if ($Diagnose) { $arguments += '--diagnose' }
if ($NoExclude) { $arguments += '--no-exclude' }
if ($NoDedupe) { $arguments += '--no-dedupe' }

Write-Host "bench: $Queries" -ForegroundColor Cyan

# stderr carries per-query debug lines; keep them out of the metric table.
$stderrLog = Join-Path ([System.IO.Path]::GetTempPath()) 'rimcp-bench.stderr.log'

# PowerShell turns a native command's stderr into a terminating error while
# $ErrorActionPreference is 'Stop', which would abort the script even though dotnet succeeded.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & dotnet @arguments 2> $stderrLog
    $code = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousEap
}

if ($code -ne 0) {
    Write-Host "bench 失败（exit=$code）。stderr 末尾：" -ForegroundColor Red
    Get-Content -LiteralPath $stderrLog -Tail 30 | Write-Host
}

exit $code
