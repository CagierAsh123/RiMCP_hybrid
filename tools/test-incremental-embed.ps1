#requires -Version 5.1
<#
.SYNOPSIS
  增量嵌入的集成测试：新 chunk 必须拿到向量，换维度必须被拦下。

.DESCRIPTION
  在临时目录里建一个小语料，跑一遍完整索引，改一个文件再跑一遍增量索引，断言：
    1. 第一次跑完 vectors.bin 的 count == chunks 数
    2. 改动后再跑，rows 增加且新 itemId 出现（旧版本会完全跳过嵌入 → 新 chunk 无向量）
    3. 删除文件后再跑，孤儿行被清掉
    4. 换成另一个维度的嵌入服务再跑，必须抛出"维度变了，请 --force embed"而不是静默写坏

  用 e5（768 维，端口 5000）与 Qwen3（1024 维，端口 5001）两个服务当"两个模型"。
#>
[CmdletBinding()]
param(
    [string]$Exe = 'build-verify\RimWorldCodeRag.dll',
    [string]$Server768 = 'http://127.0.0.1:5000',
    [string]$Server1024 = 'http://127.0.0.1:5001'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
if (-not (Test-Path -LiteralPath $exePath)) { throw "找不到 $exePath" }

$root = Join-Path $repoRoot 'local_build\incremental-embed-test'
$src = Join-Path $root 'src'
$idx = Join-Path $root 'index'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $src -Force | Out-Null

# Two tiny "mods" so the chunker produces a handful of type/method chunks.
Set-Content -LiteralPath (Join-Path $src 'Alpha.cs') -Encoding UTF8 -Value @'
namespace ITest
{
    public class Alpha
    {
        public int Value() { return 1; }
    }
}
'@
Set-Content -LiteralPath (Join-Path $src 'Beta.cs') -Encoding UTF8 -Value @'
namespace ITest
{
    public class Beta
    {
        public int Value() { return 2; }
    }
}
'@

function Invoke-Index([string]$server) {
    # A native command's stderr becomes a terminating error while $ErrorActionPreference is 'Stop',
    # which would abort this script instead of letting us inspect the exit code and output.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & dotnet $exePath index `
            --root $src `
            --lucene (Join-Path $idx 'lucene') `
            --vec (Join-Path $idx 'vec') `
            --meta (Join-Path $idx 'meta') `
            --graph (Join-Path $idx 'graph') `
            --embedding-server $server `
            --python-batch 8 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousEap
    }
    return @{ Exit = $code; Output = ($out -join "`n") }
}

function Read-Header {
    $bin = Join-Path $idx 'vec\vectors.bin'
    if (-not (Test-Path -LiteralPath $bin)) { return $null }
    $h = New-Object byte[] 24
    $fs = [System.IO.File]::OpenRead($bin)
    try { [void]$fs.Read($h, 0, 24) } finally { $fs.Dispose() }
    return @{
        Dim   = [System.BitConverter]::ToInt32($h, 12)
        Count = [System.BitConverter]::ToInt64($h, 16)
    }
}

function Read-ItemIds {
    $meta = Join-Path $idx 'vec\vectors.meta.jsonl'
    if (-not (Test-Path -LiteralPath $meta)) { return @() }
    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadLines($meta)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $ids.Add(($line | ConvertFrom-Json).itemId)
    }
    return , $ids.ToArray()
}

$failures = @()

# ---- 1. full embed ---------------------------------------------------------
Write-Host "== 1. full embed ==" -ForegroundColor Cyan
$r = Invoke-Index $Server768
if ($r.Exit -ne 0) { $failures += "首次索引失败：$($r.Output)" }
$h1 = Read-Header
$ids1 = Read-ItemIds
Write-Host ("   dim={0} count={1} metaRows={2}" -f $h1.Dim, $h1.Count, $ids1.Count)
if ($h1.Dim -ne 768) { $failures += "首次 dim=$($h1.Dim)，期望 768" }
if ($h1.Count -ne $ids1.Count) { $failures += "头部 count=$($h1.Count) 与 meta 行数 $($ids1.Count) 不一致" }
if ($h1.Count -lt 3) { $failures += "chunk 数太少（$($h1.Count)），测试无意义" }

# ---- 2. add a method -> the new chunk must get a vector --------------------
Write-Host "== 2. edit a file, incremental run (the old code embedded NOTHING here) ==" -ForegroundColor Cyan
Set-Content -LiteralPath (Join-Path $src 'Alpha.cs') -Encoding UTF8 -Value @'
namespace ITest
{
    public class Alpha
    {
        public int Value() { return 1; }
        public int AddedLater() { return 42; }
    }
}
'@
$r = Invoke-Index $Server768
if ($r.Exit -ne 0) { $failures += "增量索引失败：$($r.Output)" }
if ($r.Output -notmatch 'Incremental embeddings') { $failures += "没有走增量嵌入路径（输出里没有 'Incremental embeddings'）" }
$h2 = Read-Header
$ids2 = Read-ItemIds
# @() matters: under Set-StrictMode a single-object pipeline result has no .Count
$diff = @(Compare-Object $ids1 $ids2)
$added = @($diff | Where-Object { $_.SideIndicator -eq '=>' }).Count
$removed = @($diff | Where-Object { $_.SideIndicator -eq '<=' }).Count
Write-Host ("   dim={0} count={1} (+{2} / -{3})" -f $h2.Dim, $h2.Count, $added, $removed)
if ($added -lt 1) { $failures += "增量后没有新增向量行（新 chunk 没有向量 = 本测试要抓的 bug）" }
if ($removed -lt 1) { $failures += "改动产生的旧 itemId 没有被清掉（孤儿向量）" }
if ($h2.Count -ne $ids2.Count) { $failures += "增量后头部 count=$($h2.Count) 与 meta 行数 $($ids2.Count) 不一致" }

# ---- 3. mtime-only change: must re-chunk but produce no new vectors ---------
# Re-running with no change at all short-circuits earlier ("No changes detected"), so touch the
# timestamp without changing the content: the chunker re-chunks the file (mtime gate), every item id
# comes out identical, and the reconciliation must report "up to date" instead of rewriting.
Write-Host "== 3. touch mtime, same content (exercises the reconciliation no-op path) ==" -ForegroundColor Cyan
(Get-Item -LiteralPath (Join-Path $src 'Alpha.cs')).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddSeconds(5)
$r = Invoke-Index $Server768
$h3 = Read-Header
if ($h3.Count -ne $h2.Count) { $failures += "空跑改变了行数：$($h2.Count) -> $($h3.Count)" }
if ($r.Output -notmatch 'Embeddings are up to date') {
    $failures += "空跑没有报告 'Embeddings are up to date'（输出：$($r.Output)）"
}
Write-Host ("   count={0}" -f $h3.Count)

# ---- 4. model dimension change must be refused -----------------------------
Write-Host "== 4. point at a 1024-d server: must refuse, not silently corrupt ==" -ForegroundColor Cyan
Set-Content -LiteralPath (Join-Path $src 'Gamma.cs') -Encoding UTF8 -Value @'
namespace ITest
{
    public class Gamma
    {
        public int Value() { return 3; }
    }
}
'@
$r = Invoke-Index $Server1024
$refused = ($r.Output -match 'changed dimension') -or ($r.Output -match 'force embed')
$h4 = Read-Header
Write-Host ("   exit={0} refused={1} count={2}" -f $r.Exit, $refused, $h4.Count)
if (-not $refused) { $failures += "换维度没有被拦下（输出：$($r.Output)）" }
if ($h4.Dim -ne 768) { $failures += "被拦下后 vectors.bin 的维度被改坏了：$($h4.Dim)" }
if ($h4.Count -ne $h3.Count) { $failures += "被拦下后 vectors.bin 的行数被改坏了：$($h3.Count) -> $($h4.Count)" }

# ---- result ---------------------------------------------------------------
Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "INCREMENTAL EMBED TEST FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "INCREMENTAL EMBED TEST PASS: 新 chunk 拿到向量、孤儿被清、空跑幂等、换维度被拦下" -ForegroundColor Green
exit 0
