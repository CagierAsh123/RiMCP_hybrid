#requires -Version 5.1
<#
.SYNOPSIS
  金标冒烟测试：5 条必须命中的查询，改完代码 1 分钟跑完（阶段 0 任务 0.5）。

.DESCRIPTION
  它**不衡量质量**（质量看 tests\retrieval-baseline.json 的完整指标），它只拦"整类失效"：
  索引缺失、路径过滤过宽、语义腿静默为空、kind 过滤把结果滤空……
  这类故障的表现是"所有查询都返回 0 结果"，而完整评测要跑 40 条才看得出来。

  由来：2026-09-17 夜线上 MCP 工具静默返回 0 结果（计划书 §15），
  L1 完整评测跑了十几轮都没发现，因为 CLI 每次都是新进程新配置。
  这个冒烟测试就是那次事故的产物——它会在 1 分钟内报红。

  断言（任一不过即 exit 1）：
    1. 没有"标注问题"（每个 expected 必须真实存在于索引里）
    2. Recall@10 == 1.0（5 条全部命中）
    3. 每条查询的结果数 > 0（进一步区分"整条查询空"与"排序偏了"）

.PARAMETER EmbeddingServer
  嵌入服务地址（e5 用 5000，Qwen3 换端口后用对应端口）。

.PARAMETER VecDir
  向量目录名，默认 vec。

.PARAMETER Exe
  可执行 dll 路径，默认 bin\Release\net8.0（被占用时可指向别处的构建副本）。

.EXAMPLE
  .\tools\smoke-gold.ps1
  .\tools\smoke-gold.ps1 -EmbeddingServer http://127.0.0.1:5001 -VecDir vec.e5
#>
[CmdletBinding()]
param(
    [string]$EmbeddingServer = 'http://127.0.0.1:5000',
    [string]$VecDir = 'vec',
    [string]$Exe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$queries = Join-Path $repoRoot 'tests\smoke-gold.json'
if (-not (Test-Path -LiteralPath $queries)) { throw "找不到 $queries" }

$report = Join-Path ([System.IO.Path]::GetTempPath()) 'rimcp-smoke-gold.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force }

Write-Host "gold smoke: $queries" -ForegroundColor Cyan
$benchArgs = @{
    Queries         = $queries
    Out             = $report
    Label           = 'gold smoke'
    VecDir          = $VecDir
    EmbeddingServer = $EmbeddingServer
}
if ($Exe) { $benchArgs.Exe = $Exe }

& (Join-Path $PSScriptRoot 'bench-retrieval.ps1') @benchArgs | Out-Host

if (-not (Test-Path -LiteralPath $report)) {
    Write-Host "FAIL: bench 没有产出报告，冒烟测试无法判定" -ForegroundColor Red
    exit 1
}

$data = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$failures = @()

$labelProblems = @($data.labelProblems | Where-Object { $_ })
if ($labelProblems.Count -gt 0) {
    $failures += "标注问题 $($labelProblems.Count) 个：$($labelProblems -join '; ')"
}

$recall10 = [double]$data.aggregate.recall10
if ($recall10 -lt 1.0) {
    $failures += "Recall@10 = $recall10（要求 1.0）"
}

foreach ($query in $data.queries) {
    if ([int]$query.results.Count -eq 0) {
        $failures += "$($query.id) ('$($query.query)') 返回 0 条结果"
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "SMOKE FAILED:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    Write-Host ""
    Write-Host "常见原因（按可能性排序）：" -ForegroundColor Yellow
    Write-Host "  1. 索引/向量文件缺失或不完整（看 bench 输出里的 vector-index 行）"
    Write-Host "  2. 嵌入服务没起或地址不对（看有没有 'could not embed the query' 警告）"
    Write-Host "  3. 排除规则写得太宽（把正常内容也排掉了）"
    Write-Host "  4. kind 过滤把候选全滤空"
    exit 1
}

Write-Host "SMOKE PASS: $($data.queries.Count) 条金标查询全部命中（Recall@10 = $recall10）" -ForegroundColor Green
exit 0
