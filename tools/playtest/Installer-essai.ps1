#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Activer', 'RetourStable', 'Etat', 'Diagnostiquer')][string]$Action = 'Etat',
    [string]$GamePath,
    [switch]$AutoriserVersionExperimentale
)
$ErrorActionPreference = 'Stop'
$gamePathRecord = Join-Path $PSScriptRoot 'chemin-jeu.local.json'
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = if (Test-Path -LiteralPath $gamePathRecord -PathType Leaf) {
        (Get-Content -LiteralPath $gamePathRecord -Raw | ConvertFrom-Json).gamePath
    } else { 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure' }
}
$gameRoot = [IO.Path]::GetFullPath($GamePath).TrimEnd('\')
$mods = Join-Path $gameRoot 'Mods'
$stable = Join-Path $mods '00_WOTRMultiplayer'
$experimental = Join-Path $mods '00_WOTRMultiplayerRTwP'
# Outside Mods: UMM scans disabled folders if they remain inside Mods.
# Outside the extracted ZIP: deleting the ZIP folder must not delete stable.
$backupRoot = Join-Path $gameRoot 'RTwP-RetourStable'
$savedStable = Join-Path $backupRoot '00_WOTRMultiplayer'
$payload = Join-Path $PSScriptRoot '00_WOTRMultiplayerRTwP'
$manifestPath = Join-Path $PSScriptRoot 'manifest-essai.json'

function Assert-DirectChild([string]$Path, [string]$Parent) {
    if (-not [string]::Equals([IO.Path]::GetFullPath((Split-Path -Parent $Path)).TrimEnd('\'),
        [IO.Path]::GetFullPath($Parent).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Chemin inattendu : $Path"
    }
    if (Test-Path -LiteralPath $Path) {
        if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Lien de dossier refuse : $Path"
        }
    }
}

function Assert-Payload([string]$Folder, $Manifest) {
    if ($Manifest.runtimeValidated -ne $false -or $Manifest.hostPresentationPrototype -ne $true -or
        @($Manifest.files).Count -lt 6) { throw 'Manifeste experimental invalide.' }
    $resolvedFolder = [IO.Path]::GetFullPath($Folder).TrimEnd('\')
    foreach ($file in $Manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Folder $file.path))
        if (-not $path.StartsWith($resolvedFolder + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Fichier absent ou chemin invalide : $($file.path)"
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "Fichier different du paquet fourni : $($file.path)"
        }
    }
    $actualDlls = @(Get-ChildItem -LiteralPath $Folder -File -Filter '*.dll')
    $expectedDlls = @($Manifest.files | Where-Object { $_.path -match '^[^\\/]+\.dll$' })
    if ($actualDlls.Count -ne $expectedDlls.Count) { throw 'DLL supplementaire dans le paquet.' }
    $info = Get-Content -LiteralPath (Join-Path $Folder 'Info.json') -Raw | ConvertFrom-Json
    if ($info.id -ne 'WOTRMultiplayerRTwP' -or $info.version -ne $Manifest.version) {
        throw 'Identifiant ou version du mod incoherent.'
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'Wrath.exe') -PathType Leaf)) {
    throw 'Jeu introuvable. Utilisez -GamePath avec votre dossier Pathfinder Second Adventure.'
}
foreach ($path in @($stable, $experimental)) { Assert-DirectChild $path $mods }
Assert-DirectChild $mods $gameRoot
Assert-DirectChild $backupRoot $gameRoot
Assert-DirectChild $savedStable $backupRoot
if ($Action -eq 'Diagnostiquer') {
    $destination = Join-Path $PSScriptRoot ('diagnostic-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    Assert-DirectChild $destination $PSScriptRoot
    New-Item -ItemType Directory -Path $destination | Out-Null
    $failures = @()
    $logRoot = Join-Path $env:USERPROFILE 'AppData\LocalLow\Owlcat Games\Pathfinder Wrath Of The Righteous'
    foreach ($name in @('Player.log','Player-prev.log','GameLog.txt','GameLogFull.txt','combatLog.txt')) {
        $source = Join-Path $logRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            try { Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name) }
            catch { $failures += "$name : $($_.Exception.Message)" }
        }
    }
    $modsInfo = @(Get-ChildItem -LiteralPath $mods -Directory | ForEach-Object {
        $infoPath = Join-Path $_.FullName 'Info.json'
        if (Test-Path -LiteralPath $infoPath -PathType Leaf) {
            try { $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
                [ordered]@{ folder = $_.Name; id = $info.id; version = $info.version } }
            catch { $failures += "Info.json illisible : $infoPath" }
        }
    })
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destination 'manifest-essai.json')
    }
    $report = [ordered]@{
        capturedAt = (Get-Date).ToString('o'); gamePath = $gameRoot
        gameRunning = [bool](Get-Process -Name Wrath -ErrorAction SilentlyContinue)
        installedMods = $modsInfo; stableActive = Test-Path -LiteralPath $stable
        experimentalActive = Test-Path -LiteralPath $experimental
        stablePreserved = Test-Path -LiteralPath $savedStable
        errors = $failures; savesCopied = $false; voiceFilesCopied = $false
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'diagnostic.json') -Encoding UTF8
    Compress-Archive -LiteralPath $destination -DestinationPath ($destination + '.zip')
    Write-Output "Diagnostic local : $destination.zip"
    Write-Output 'Aucun envoi reseau. Journaux pouvant contenir vos noms et adresses IP : partagez uniquement en prive.'
    exit 0
}
if ($Action -eq 'Etat') {
    Write-Output "Stable actif : $(Test-Path -LiteralPath $stable)"
    Write-Output "Essai actif : $(Test-Path -LiteralPath $experimental)"
    Write-Output "Stable conserve : $(Test-Path -LiteralPath $savedStable)"
    exit 0
}
if (Get-Process -Name Wrath -ErrorAction SilentlyContinue) { throw 'Fermez le jeu avant de basculer les mods.' }

if ($Action -eq 'RetourStable') {
    if (Test-Path -LiteralPath $stable) {
        if (Test-Path -LiteralPath $experimental) { throw 'Deux variantes presentes : retour automatique refuse.' }
        Write-Output 'Le stable est deja en place.'
        exit 0
    }
    if (-not (Test-Path -LiteralPath $savedStable -PathType Container)) { throw 'Stable conserve introuvable. Aucun dossier modifie.' }
    $retired = Join-Path $backupRoot ('essai-' + [Guid]::NewGuid().ToString('N'))
    Assert-DirectChild $retired $backupRoot
    $moved = $false
    try {
        if (Test-Path -LiteralPath $experimental) {
            Move-Item -LiteralPath $experimental -Destination $retired
            $moved = $true
        }
        Move-Item -LiteralPath $savedStable -Destination $stable
    } catch {
        if ($moved -and -not (Test-Path -LiteralPath $experimental)) {
            Move-Item -LiteralPath $retired -Destination $experimental
        }
        throw
    }
    Write-Output 'Stable restaure. Essai conserve hors de Mods. Aucune sauvegarde ni voix modifiee.'
    exit 0
}

if (-not $AutoriserVersionExperimentale) { throw 'Version non validee en jeu. Activer exige -AutoriserVersionExperimentale.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Payload $payload $manifest
$gameAssembly = Join-Path $gameRoot 'Wrath_Data\Managed\Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne $manifest.gameAssemblySha256) {
    throw 'Version du jeu differente de celle du paquet. Aucun dossier modifie.'
}
if (Test-Path -LiteralPath $experimental) {
    if (Test-Path -LiteralPath $stable) { throw 'Deux variantes presentes. Aucun dossier modifie.' }
    try { Assert-Payload $experimental $manifest }
    catch { throw 'Une autre version experimentale est active. Lancez RETOUR-STABLE.cmd, puis ACTIVER-ESSAI.cmd du nouveau paquet.' }
    Write-Output 'Ce paquet experimental est deja en place.'
    exit 0
}
if (-not (Test-Path -LiteralPath $stable -PathType Container)) { throw 'Stable actif introuvable. Aucun dossier modifie.' }
if (Test-Path -LiteralPath $savedStable) { throw 'Une copie stable existe deja. Aucun ecrasement autorise.' }
$stableInfo = Get-Content -LiteralPath (Join-Path $stable 'Info.json') -Raw | ConvertFrom-Json
if ($stableInfo.id -ne 'WOTRMultiplayer' -or $stableInfo.version -ne '0.4.16') {
    throw 'Le stable attendu est WOTRMultiplayer 0.4.16. Aucun dossier modifie.'
}
foreach ($folder in Get-ChildItem -LiteralPath $mods -Directory) {
    $infoPath = Join-Path $folder.FullName 'Info.json'
    if (-not (Test-Path -LiteralPath $infoPath -PathType Leaf)) { continue }
    $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
    if ($info.id -in @('WOTRMultiplayer', 'WOTRMultiplayerRTwP') -and $folder.FullName -ne $stable) {
        throw "Autre variante detectee : $($folder.FullName). Aucun dossier modifie."
    }
}
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$staging = Join-Path $backupRoot ('preparation-' + [Guid]::NewGuid().ToString('N'))
Assert-DirectChild $staging $backupRoot
New-Item -ItemType Directory -Path $staging | Out-Null
$stableMoved = $false
try {
    Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $staging -Recurse
    Assert-Payload $staging $manifest
    Move-Item -LiteralPath $stable -Destination $savedStable
    $stableMoved = $true
    Move-Item -LiteralPath $staging -Destination $experimental
} catch {
    if ($stableMoved -and -not (Test-Path -LiteralPath $stable) -and -not (Test-Path -LiteralPath $experimental)) {
        Move-Item -LiteralPath $savedStable -Destination $stable
    }
    throw
}
Write-Warning 'ESSAI NON VALIDE EN JEU. Utilisez une copie de sauvegarde, pas votre partie habituelle.'
try { @{ gamePath = $gameRoot } | ConvertTo-Json | Set-Content -LiteralPath $gamePathRecord -Encoding UTF8 }
catch { Write-Warning 'Chemin du jeu non memorise : reutilisez -GamePath pour revenir au stable.' }
Write-Output 'Essai active. Stable conserve dans RTwP-RetourStable. Le jeu ne sera pas lance.'
Write-Output 'Verifiez dans Unity Mod Manager que Multiplayer RTwP est coche. Ne changez pas WOTR_AIVO.'
