[CmdletBinding()]
param([string]$LocalizationPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'localization'))

$ErrorActionPreference = 'Stop'

function Get-Leaves($node, [string]$prefix) {
    $result = @{}
    foreach ($key in $node.Keys) {
        $path = if ($prefix) { "$prefix.$key" } else { $key }
        if ($node[$key] -is [System.Collections.IDictionary]) {
            foreach ($entry in (Get-Leaves $node[$key] $path).GetEnumerator()) {
                $result[$entry.Key] = $entry.Value
            }
        } else {
            $result[$path] = [string]$node[$key]
        }
    }
    return $result
}

$english = Get-Leaves (Get-Content -LiteralPath (Join-Path $LocalizationPath 'enGB.json') -Raw | ConvertFrom-Json -AsHashtable) ''
$french = Get-Leaves (Get-Content -LiteralPath (Join-Path $LocalizationPath 'frFR.json') -Raw | ConvertFrom-Json -AsHashtable) ''
$problems = [System.Collections.Generic.List[string]]::new()

foreach ($key in $english.Keys) {
    if (-not $french.ContainsKey($key)) {
        $problems.Add("Missing French key: $key")
        continue
    }
    if ([string]::IsNullOrWhiteSpace($french[$key])) {
        $problems.Add("Empty French text: $key")
    }
    $expected = @([regex]::Matches($english[$key], '\{\d+\}') | ForEach-Object Value | Sort-Object)
    $actual = @([regex]::Matches($french[$key], '\{\d+\}') | ForEach-Object Value | Sort-Object)
    if (($expected -join ',') -ne ($actual -join ',')) {
        $problems.Add("Placeholder mismatch: $key")
    }
}
foreach ($key in $french.Keys) {
    if (-not $english.ContainsKey($key)) { $problems.Add("Unknown French key: $key") }
}

if ($problems.Count -gt 0) { throw ($problems -join [Environment]::NewLine) }
Write-Output "French localization verified: $($french.Count) / $($english.Count) keys; placeholders match."
