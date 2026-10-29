#Requires -Version 7.0
[CmdletBinding()]
param([string]$CandidateFingerprint)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'rtwp-package-protocol.ps1')
$repoPath = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repoPath 'src\WOTRMultiplayer\bin\Release\net481'
if ($CandidateFingerprint) {
    if ($CandidateFingerprint -cnotmatch '^[A-F0-9]{64}$') { throw 'Empreinte de candidat invalide.' }
    $candidate = Join-Path $repoPath "local-artifacts\a0-candidates\$CandidateFingerprint"
    $package = Join-Path $candidate '00_WOTRMultiplayerRTwP'
}
$assembly = Join-Path $package 'WOTRMultiplayer.dll'
$version = Get-RtwpPackageProtocolVersion $assembly
Assert-RtwpPackageManifestProtocol $assembly ([pscustomobject]@{ protocolVersion = $version })
$checks = 1
foreach ($invalid in @($null, '51', ($version - 1), ($version + 1))) {
    $rejected = $false
    try { Assert-RtwpPackageManifestProtocol $assembly ([pscustomobject]@{ protocolVersion = $invalid }) }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Un protocole de manifeste invalide a été accepté.' }
    $checks++
}
$rejected = $false
try { Get-RtwpPackageProtocolVersion (Join-Path $package 'WOTRMultiplayer.Networking.dll') | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw 'Une DLL sans protocole RTwP a été acceptée.' }
$checks++
if ($CandidateFingerprint) {
    $manifest = Get-Content -LiteralPath (Join-Path $candidate 'manifest.json') -Raw | ConvertFrom-Json
    Assert-RtwpPackageManifestProtocol $assembly $manifest
    $checks++
}
Write-Output "Protocole lu dans la DLL : $version ; $checks contrôles réussis ; aucun jeu ni assembly exécuté."
