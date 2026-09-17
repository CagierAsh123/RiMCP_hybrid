#requires -Version 5.1
<#
.SYNOPSIS
  Resolve candidate RimWorld symbol/Def names to the exact symbolId form used by RiMCP's index.

.DESCRIPTION
  Used while authoring/validating `tests/retrieval-baseline.json`: for every name it searches the
  decompiled source tree and prints the symbolId the indexer would produce.

    * C#  : finds `class|struct|enum|interface <Name>` and reads the enclosing namespace
            -> "<namespace>.<Name>"
    * XML : finds `<defName>Name</defName>` and walks up to the nearest `*Def` element
            -> "xml:<DefType>:<defName>"

.PARAMETER Name
  One or more bare names or already-complete symbolIds ("RimWorld.Need_Food" also works).

.NOTES
  Source root comes from $env:RIMWORLD_SOURCE_ROOT (default B:\rimworld-code\_SourceCode).

.EXAMPLE
  .\Resolve-SymbolIds.ps1 Need_Food CompPowerTrader Steel Gun_BoltActionRifle
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Name
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A second positional parameter would steal the first name (ValueFromRemainingArguments
# binds positional args in declaration order), so the root is configured via the environment.
$Root = if ($env:RIMWORLD_SOURCE_ROOT) { $env:RIMWORLD_SOURCE_ROOT } else { 'B:\rimworld-code\_SourceCode' }

$csRoot = Join-Path $Root 'CSharp'
$xmlRoot = Join-Path $Root 'XML'
$modGlobs = @(Join-Path $Root '*')
$exclude = @('\Develop\', '\rjw\')

function Test-Excluded([string]$path) {
    foreach ($e in $exclude) { if ($path.IndexOf($e, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    return $false
}

$results = [System.Collections.Generic.List[object]]::new()

# Enumerate the candidate files ONCE; doing it per name is O(names x files).
# Vanilla C# lives under CSharp\; every other top-level folder is a mod (mod C# sits under
# <mod>\<version>\Source\...), so both have to be scanned.
$csFiles = @(Get-ChildItem -LiteralPath $csRoot -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue |
    Where-Object { -not (Test-Excluded $_.FullName) })

$xmlFiles = @()
$xmlFiles += @(Get-ChildItem -LiteralPath $xmlRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue)
foreach ($modDir in @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue)) {
    if (Test-Excluded $modDir.FullName) { continue }
    if ($modDir.Name -in @('CSharp', 'XML', '_meta')) { continue }
    $csFiles += @(Get-ChildItem -LiteralPath $modDir.FullName -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue)
    $xmlFiles += @(Get-ChildItem -LiteralPath $modDir.FullName -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue)
}
$xmlFiles = @($xmlFiles | Where-Object { $_.FullName -notmatch '\\(Languages|DefInjected|Patches|About)\\' })

Write-Verbose "cs files=$($csFiles.Count) xml files=$($xmlFiles.Count) root=$Root"

foreach ($raw in $Name) {
    $bare = $raw
    if ($bare.StartsWith('xml:')) { $bare = $bare.Split(':')[-1] }
    elseif ($bare.Contains('.')) { $bare = $bare.Split('.')[-1] }
    if ($bare.Contains('(')) { $bare = $bare.Substring(0, $bare.IndexOf('(')) }
    $bare = $bare.Trim()

    $found = $false

    # --- C# type declarations -------------------------------------------------
    $pattern = "(?:class|struct|interface|enum)\s+$([regex]::Escape($bare))\b"
    $csHits = @($csFiles | Select-String -Pattern $pattern -List -ErrorAction SilentlyContinue)

    foreach ($hit in $csHits) {
        $nsMatch = Select-String -LiteralPath $hit.Path -Pattern '^\s*namespace\s+([\w\.]+)' -List -ErrorAction SilentlyContinue | Select-Object -First 1
        $ns = if ($null -ne $nsMatch) { $nsMatch.Matches[0].Groups[1].Value } else { '' }
        $symbolId = if ($ns) { "$ns.$bare" } else { $bare }
        $results.Add([pscustomobject]@{ Query = $raw; SymbolId = $symbolId; Kind = 'csharp'; Path = $hit.Path })
        $found = $true
    }

    Write-Verbose "name=$raw bare=$bare csHits=$($csHits.Count)"

    # --- XML Defs -------------------------------------------------------------
    $xmlHits = @($xmlFiles |
        Select-String -Pattern "<defName>\s*$([regex]::Escape($bare))\s*</defName>" -List -ErrorAction SilentlyContinue)

    foreach ($hit in $xmlHits) {
        try {
            [xml]$doc = Get-Content -LiteralPath $hit.Path -Raw -ErrorAction Stop
        }
        catch { continue }

        if ($null -eq $doc.DocumentElement) { continue }

        $stack = [System.Collections.Generic.Stack[object]]::new()
        $stack.Push($doc.DocumentElement)
        $node = $null
        while ($stack.Count -gt 0) {
            $current = $stack.Pop()
            if ($null -eq $current) { continue }
            if ($current.LocalName -eq 'defName' -and $current.InnerText.Trim() -eq $bare) {
                $node = $current
                break
            }
            if ($null -ne $current.ChildNodes) {
                foreach ($child in $current.ChildNodes) {
                    if ($null -ne $child -and $child.NodeType -eq 'Element') { $stack.Push($child) }
                }
            }
        }

        if ($null -ne $node -and $null -ne $node.ParentNode) {
            $defType = $node.ParentNode.LocalName
            $results.Add([pscustomobject]@{ Query = $raw; SymbolId = "xml:${defType}:$bare"; Kind = 'xml'; Path = $hit.Path })
            $found = $true
        }
    }

    if (-not $found) {
        $results.Add([pscustomobject]@{ Query = $raw; SymbolId = '<NOT FOUND>'; Kind = '-'; Path = '-' })
    }
}

$results | Sort-Object Query, SymbolId | Format-Table -AutoSize -Wrap
