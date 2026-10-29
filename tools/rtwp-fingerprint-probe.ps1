[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
$assembly = Join-Path $resolvedPackage 'WOTRMultiplayer.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Bibliothèque RTwP absente : $assembly"
}
Add-Type -Path $assembly
[WOTRMultiplayer.Services.RealTimeBuildFingerprint]::Compute($resolvedPackage)
