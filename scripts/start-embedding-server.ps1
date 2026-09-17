#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Start the persistent embedding server.

.DESCRIPTION
    Launches the Flask embedding server with GPU acceleration. 
    The server keeps the model loaded in memory for fast queries.

.PARAMETER Port
    Port to listen on (default: 5000)

.PARAMETER Host  
    Host to bind to (default: 127.0.0.1)

.PARAMETER Model
    Path to the model directory (default: src\RimWorldCodeRag\models\e5-base-v2)

.PARAMETER MaxLength
    Max sequence length; 0 = use the model's own limit (Qwen3-Embedding: 32768).
    e5-base-v2 is capped at 512 by its own config, so 0 is safe for both.

.PARAMETER Backend
    auto (default) | sentence-transformers | transformers.
    Qwen3-Embedding needs last-token pooling + a query-side instruction, which only the
    sentence-transformers backend reads from the model repo.

.EXAMPLE
    .\start-embedding-server.ps1
    .\start-embedding-server.ps1 -Model ..\RimWorldCodeRag\models\Qwen3-Embedding-0.6B
#>

param(
    [int]$Port = 5000,
    [string]$ServerHost = "127.0.0.1",
    [string]$Model = "",
    [int]$MaxLength = 0,
    [ValidateSet("auto", "sentence-transformers", "transformers")]
    [string]$Backend = "auto"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venvPython = Join-Path $repoRoot "src\RimWorldCodeRag\.venv\Scripts\python.exe"
$serverScript = Join-Path $repoRoot "src\RimWorldCodeRag\python\embedding_server.py"

if (-not $Model) {
    $Model = Join-Path $repoRoot "src\RimWorldCodeRag\models\e5-base-v2"
}

if (-not (Test-Path $venvPython)) {
    Write-Error "Virtual environment not found at $venvPython. Run setup-embedding-env.ps1 first."
    exit 1
}

if (-not (Test-Path $serverScript)) {
    Write-Error "Server script not found at $serverScript"
    exit 1
}

if (-not (Test-Path $Model)) {
    Write-Error "Model directory not found at $Model"
    exit 1
}

Write-Host "Starting embedding server..." -ForegroundColor Cyan
Write-Host "  Host: $ServerHost" -ForegroundColor Gray
Write-Host "  Port: $Port" -ForegroundColor Gray
Write-Host "  Model: $Model" -ForegroundColor Gray
Write-Host ""
Write-Host "Server will load model on startup (may take 10-20 seconds)..." -ForegroundColor Yellow
Write-Host "Press Ctrl+C to stop the server" -ForegroundColor Yellow
Write-Host ""

& $venvPython $serverScript --model $Model --host $ServerHost --port $Port
