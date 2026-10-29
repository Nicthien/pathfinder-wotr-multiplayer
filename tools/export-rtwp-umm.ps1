#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Delivery')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Delivery')][string]$DeliveryName,
    [Parameter(Mandatory, ParameterSetName = 'Candidate')][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CandidateFingerprint
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($PSCmdlet.ParameterSetName -eq 'Delivery') {
    if ($DeliveryName -notmatch '^WotR-RTwP-Essai-[0-9.]+-[A-F0-9]{12}$') { throw 'Nom de livraison invalide.' }
    $delivery = Join-Path $repo ('local-artifacts\livraisons\' + $DeliveryName)
    $payload = Join-Path $delivery '00_WOTRMultiplayerRTwP'
    $manifest = Get-Content -LiteralPath (Join-Path $delivery 'manifest-essai.json') -Raw | ConvertFrom-Json
} else {
    $delivery = Join-Path $repo ('local-artifacts\a0-candidates\' + $CandidateFingerprint.ToUpperInvariant())
    $payload = Join-Path $delivery '00_WOTRMultiplayerRTwP'
    $manifest = Get-Content -LiteralPath (Join-Path $delivery 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.experimentalBuildFingerprint -ne $CandidateFingerprint -or $manifest.hostPresentationPrototype -ne $true -or
        $manifest.runtimeValidated -ne $false -or
        (Get-FileHash -LiteralPath (Join-Path $payload 'WOTRMultiplayer.dll') -Algorithm SHA256).Hash -ne $manifest.mainAssemblySha256) {
        throw 'Candidat experimental incoherent.'
    }
    $manifest | Add-Member version ((Get-Content -LiteralPath (Join-Path $payload 'Info.json') -Raw | ConvertFrom-Json).version)
    $manifest | Add-Member files @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($payload, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$info = Get-Content -LiteralPath (Join-Path $payload 'Info.json') -Raw | ConvertFrom-Json
if ($info.id -ne 'WOTRMultiplayerRTwP' -or $info.version -ne $manifest.version) { throw 'Identite incoherente.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    if (-not $path.StartsWith([IO.Path]::GetFullPath($payload) + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Contenu different du candidat.' }
}
$archive = Join-Path $repo ('local-artifacts\livraisons\WOTRMultiplayerRTwP-' + $info.version + '-UMM.zip')
if (Test-Path -LiteralPath $archive) { throw 'Archive deja presente : aucun ecrasement.' }
# UMM requires Info.json beside the DLL at the archive root; no delivery wrapper.
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -CompressionLevel Optimal
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    if ('Info.json' -notin $names -or 'WOTRMultiplayer.dll' -notin $names -or
        @($names | Where-Object { $_ -match '(^|/)Info\.json$' }).Count -ne 1 -or
        @($names | Where-Object { $_ -match 'ACTIVER-ESSAI|Installer-essai|WOTR_AIVO' }).Count -ne 0) { throw 'Structure UMM incorrecte.' }
    foreach ($file in $manifest.files) {
        $entry = $zip.GetEntry($file.path.Replace('\','/'))
        if ($null -eq $entry) { throw 'Fichier absent du ZIP.' }
        $stream = $entry.Open()
        try {
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne $file.sha256) { throw 'Fichier altere dans le ZIP.' }
        } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
Write-Output "ZIP UMM : $archive"
Write-Output "Empreinte du candidat inchangee : $($manifest.experimentalBuildFingerprint)"
Write-Output "SHA256 ZIP : $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
Write-Output 'Structure archive verifiee ; aucune installation ni lancement de jeu.'
