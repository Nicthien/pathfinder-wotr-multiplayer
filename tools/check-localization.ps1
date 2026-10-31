[CmdletBinding()]
param([string]$LocalizationPath)

# $PSScriptRoot is only populated in the script body under Windows PowerShell 5.1,
# so the default path cannot be computed in the param block above.
if (-not $LocalizationPath) {
    $LocalizationPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'localization'
}

$ErrorActionPreference = 'Stop'

function Get-Leaves($node, [string]$prefix) {
    $result = @{}
    if ($null -eq $node) { return $result }
    if ($node -is [System.Collections.IDictionary]) {
        $properties = $node.GetEnumerator() | ForEach-Object { @{ Name = $_.Key; Value = $_.Value } }
    } else {
        # ConvertFrom-Json returns PSCustomObject on Windows PowerShell 5.1
        # (no -AsHashtable there), so enumerate PSObject properties.
        $properties = $node.PSObject.Properties | ForEach-Object { @{ Name = $_.Name; Value = $_.Value } }
    }
    foreach ($property in $properties) {
        $path = if ($prefix) { "$prefix.$($property.Name)" } else { $property.Name }
        $value = $property.Value
        if ($null -ne $value -and ($value -is [System.Collections.IDictionary] -or $value -is [PSCustomObject])) {
            foreach ($entry in (Get-Leaves $value $path).GetEnumerator()) {
                $result[$entry.Key] = $entry.Value
            }
        } else {
            $result[$path] = [string]$value
        }
    }
    return $result
}

$english = Get-Leaves (Get-Content -LiteralPath (Join-Path $LocalizationPath 'enGB.json') -Raw | ConvertFrom-Json) ''
$french = Get-Leaves (Get-Content -LiteralPath (Join-Path $LocalizationPath 'frFR.json') -Raw | ConvertFrom-Json) ''
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
