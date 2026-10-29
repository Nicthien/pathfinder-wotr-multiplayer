#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CandidateFingerprint)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$candidate = Join-Path $repo ('local-artifacts\a0-candidates\' + $CandidateFingerprint.ToUpperInvariant())
$source = Join-Path $candidate '00_WOTRMultiplayerRTwP'
$manifest = Get-Content -LiteralPath (Join-Path $candidate 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.experimentalBuildFingerprint -ne $CandidateFingerprint -or $manifest.runtimeValidated -ne $false -or
    $manifest.hostPresentationPrototype -ne $true) { throw 'Candidat experimental incoherent.' }
$info = Get-Content -LiteralPath (Join-Path $source 'Info.json') -Raw | ConvertFrom-Json
$exportRoot = Join-Path $repo 'local-artifacts\livraisons'
$name = 'WotR-RTwP-Essai-' + $info.version + '-' + $CandidateFingerprint.Substring(0, 12).ToUpperInvariant()
$destination = Join-Path $exportRoot $name
$archive = $destination + '.zip'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $archive)) { throw 'Livraison deja presente : aucun ecrasement.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$exportPayload = Join-Path $destination '00_WOTRMultiplayerRTwP'
# Parallel file copy avoids serial antivirus scanning of every dependency.
# No /MIR, /PURGE or /MOVE: the candidate is never removed or changed.
& robocopy.exe $source $exportPayload /E /MT:8 /R:1 /W:1 /NP /NJH /NJS /NDL /NFL | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copie du paquet incomplete (robocopy $LASTEXITCODE)." }
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'playtest') -File | Copy-Item -Destination $destination
$files = @(Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($source, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$delivery = [ordered]@{
    version = $info.version
    protocolVersion = $manifest.protocolVersion
    experimentalBuildFingerprint = $manifest.experimentalBuildFingerprint
    gameAssemblySha256 = $manifest.gameAssemblySha256
    hostPresentationPrototype = $true
    runtimeValidated = $false
    testsRunForThisDelivery = $false
    files = $files
}
$delivery | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'manifest-essai.json') -Encoding UTF8
Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal
Write-Output "Paquet d'essai : $archive"
Write-Output "Dossier utilisable : $destination"
Write-Output "SHA-256 ZIP : $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
Write-Output 'Aucun jeu lance, aucun mod installe, aucune preuve de validation moteur creee.'
