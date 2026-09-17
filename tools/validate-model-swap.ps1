#requires -Version 5.1
<#
.SYNOPSIS
  RiMCP 换嵌入模型后的验收流程（阶段 2 任务 2.1 的收尾）。

.DESCRIPTION
  按顺序做四件事，任一步失败即停：

  1. 校验嵌入服务：维度、dtype、是否有 query instruction（Qwen3 必须走 sentence-transformers 后端）
  2. 校验新索引产物：vectors.bin 的头部 dim/count 是否与 chunks 数一致
  3. 跑 python/smoke_embedding_server.py（含"query 与 passage 必须不同"这条关键断言）
  4. 跑评测集并与旧基线对比（--compare），输出指标差异表 + 逐条回归/提升清单

.PARAMETER EmbeddingServer
  新模型的服务地址（换模型时通常是 5001，切完端口后是 5000）。

.PARAMETER Baseline
  要对比的旧基线 JSON。默认 tests\m1-fused-default.json —— 它用的是**与本次运行相同的配置**
  （融合 0.3/0.7），所以差异只反映模型换代。纯语义的 tests\baseline-e5.json 是更早的参照，
  拿它对比会把"融合的收益"和"模型换代的收益"混在一起。

.PARAMETER ExpectedDim
  期望的向量维度（Qwen3-Embedding-0.6B = 1024，e5-base-v2 = 768）。

.PARAMETER Label
  本次运行的标签，写进结果 JSON。

.PARAMETER Out
  结果 JSON 路径，默认 tests\baseline-qwen3.json。

.PARAMETER SkipBench
  只做前三步（想先切端口再单独跑评测时用）。

.EXAMPLE
  .\tools\validate-model-swap.ps1 -EmbeddingServer http://127.0.0.1:5001
#>
[CmdletBinding()]
param(
    [string]$EmbeddingServer = 'http://127.0.0.1:5001',
    [string]$Baseline = 'tests\m1-fused-default.json',
    [int]$ExpectedDim = 1024,
    [string]$Label = 'qwen3-embedding-0.6b',
    [string]$Out = 'tests\baseline-qwen3.json',
    [switch]$SkipBench
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$indexRoot = Join-Path $repoRoot 'src\RimWorldCodeRag\index'
$vecBin = Join-Path $indexRoot 'vec\vectors.bin'
$vecMeta = Join-Path $indexRoot 'vec\vectors.meta.jsonl'
$py = Join-Path $repoRoot 'src\RimWorldCodeRag\.venv\Scripts\python.exe'

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    exit 1
}

function Step([string]$message) {
    Write-Host ""
    Write-Host "== $message" -ForegroundColor Cyan
}

# --- 1. embedding server ----------------------------------------------------
Step "1/4 embedding server ($EmbeddingServer)"
try {
    $health = Invoke-RestMethod -Uri "$EmbeddingServer/health" -TimeoutSec 30
}
catch {
    Fail "无法访问 $EmbeddingServer/health：$($_.Exception.Message)"
}
$health | ConvertTo-Json -Compress | Write-Host

if ($health.dim -ne $ExpectedDim) { Fail "服务维度 $($health.dim) != 期望 $ExpectedDim（索引和查询向量必须同维）" }
if ($health.model_loaded -ne $true) { Fail "服务报告 model_loaded=false" }
if ($health.backend -and $health.backend -ne 'sentence-transformers') {
    Write-Host "WARN: 后端是 $($health.backend)；Qwen3 必须走 sentence-transformers，否则池化方式和 query instruction 都不对" -ForegroundColor Yellow
}
if ($health.prompts -and ($health.prompts -notcontains 'query')) {
    Write-Host "WARN: 服务没有 query prompt；Qwen3-Embedding 缺 instruction 会静默掉分" -ForegroundColor Yellow
}

# --- 2. index artifacts -----------------------------------------------------
Step "2/4 index artifacts"
if (-not (Test-Path -LiteralPath $vecBin)) { Fail "缺少 $vecBin（重嵌入没跑完？）" }
if (-not (Test-Path -LiteralPath $vecMeta)) { Fail "缺少 $vecMeta" }

$binLength = [System.IO.FileInfo]::new($vecBin).Length
$headerSize = 24
if ($binLength -lt $headerSize) { Fail "vectors.bin 只有 $binLength 字节，明显是半成品" }

$header = New-Object byte[] 24
$stream = [System.IO.File]::OpenRead($vecBin)
try {
    $read = $stream.Read($header, 0, 24)
}
finally {
    $stream.Dispose()
}
if ($read -ne 24) { Fail "vectors.bin 头部读不满 24 字节" }

$magic = [System.Text.Encoding]::ASCII.GetString($header, 0, 8)
$version = [System.BitConverter]::ToInt32($header, 8)
$dim = [System.BitConverter]::ToInt32($header, 12)
$count = [System.BitConverter]::ToInt64($header, 16)

"magic={0} version={1} dim={2} count={3:N0}" -f $magic, $version, $dim, $count | Write-Host
if ($magic -ne 'RWVEC001') { Fail "vectors.bin 头 magic 不对：$magic" }
if ($dim -ne $ExpectedDim) { Fail "索引 dim=$dim，与服务的 $ExpectedDim 不一致 —— 查询和文档向量不同维，点积恒为 0" }

$expectedLength = $headerSize + $count * $dim * 4
if ($binLength -ne $expectedLength) { Fail "vectors.bin 长度 $binLength != 头部推出来的 $expectedLength（写入被截断）" }

$metaLines = 0
foreach ($_ in [System.IO.File]::ReadLines($vecMeta)) { if (-not [string]::IsNullOrWhiteSpace($_)) { $metaLines++ } }
"meta 行数 = {0:N0}（应与 count 相等）" -f $metaLines | Write-Host
if ($metaLines -ne $count) { Fail "metadata 行数 $metaLines != count $count" }
"OK: {0} vectors x {1}d = {2:N0} MB" -f $count, $dim, ($binLength / 1MB) | Write-Host

# --- 3. server smoke test ---------------------------------------------------
Step "3/4 embedding smoke test"
& $py (Join-Path $repoRoot 'src\RimWorldCodeRag\python\smoke_embedding_server.py') --url $EmbeddingServer --expect-dim $ExpectedDim
if ($LASTEXITCODE -ne 0) { Fail "smoke_embedding_server.py 失败" }

if ($SkipBench) {
    Write-Host ""
    Write-Host "前三步通过（-SkipBench 已跳过评测）" -ForegroundColor Green
    exit 0
}

# --- 4. benchmark vs baseline ----------------------------------------------
Step "4/4 benchmark vs $Baseline"
& (Join-Path $PSScriptRoot 'bench-retrieval.ps1') `
    -Label $Label `
    -Out $Out `
    -Compare $Baseline `

    -EmbeddingServer $EmbeddingServer `
    -Diagnose
if ($LASTEXITCODE -ne 0) { Fail "bench-retrieval.ps1 失败" }

Write-Host ""
Write-Host "验收完成：结果在 $Out" -ForegroundColor Green
Write-Host "重点看 bilingual-mod（e5 基线只有 0.125）与 natural-language（0.125）是否起来。" -ForegroundColor Green
exit 0
