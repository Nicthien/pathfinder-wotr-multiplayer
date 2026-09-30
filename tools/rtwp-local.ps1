[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Build', 'Install', 'StageTest', 'EnableStable', 'EnableExperimental', 'EnableTestExperimental', 'Status')]
    [string]$Action,

    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure',

    [switch]$AllowUnvalidatedTest
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$modsPath = Join-Path $GamePath 'Mods'
$stablePath = Join-Path $modsPath '00_WOTRMultiplayer'
$experimentalPath = Join-Path $modsPath '00_WOTRMultiplayerRTwP'
$disabledStablePath = Join-Path $modsPath '00_WOTRMultiplayer.disabled'
$disabledExperimentalPath = Join-Path $modsPath '00_WOTRMultiplayerRTwP.disabled'
$buildPath = Join-Path $repoPath 'src\WOTRMultiplayer\bin\Release\net481'
$backupRoot = Join-Path $repoPath 'local-artifacts\backups'
$runtimeReadyPath = Join-Path $repoPath 'local-artifacts\RTWP_RUNTIME_VALIDATED.json'

function Assert-RuntimeReady {
    if (-not (Test-Path -LiteralPath $runtimeReadyPath)) {
        throw 'Installation RTwP bloquée : le moteur de combat temps réel et ses tests en jeu ne sont pas encore validés. Build reste disponible sans toucher au jeu.'
    }

    $validation = Get-Content -LiteralPath $runtimeReadyPath -Raw | ConvertFrom-Json
    if ($validation.protocolVersion -ne 10 -or $validation.twoInstanceCombatVerified -ne $true) {
        throw 'Validation RTwP absente ou incompatible : installation refusée.'
    }
}

function Assert-UnvalidatedTestRequested {
    if (-not $AllowUnvalidatedTest) {
        throw 'Action réservée aux essais locaux : relancez avec -AllowUnvalidatedTest après avoir vérifié la sauvegarde et fermé le jeu.'
    }
}

function Assert-GameStopped {
    if (Get-Process -Name 'Wrath' -ErrorAction SilentlyContinue) {
        throw 'Wrath.exe est actif. Fermez le jeu avant de modifier le mod installé.'
    }
}

function Assert-GameInstallation {
    if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'Wrath.exe'))) {
        throw "Dossier du jeu invalide : Wrath.exe est absent de $GamePath"
    }
}

function Assert-SafeModPath([string]$path) {
    $resolvedModsPath = [IO.Path]::GetFullPath($modsPath).TrimEnd('\')
    $resolvedPath = [IO.Path]::GetFullPath($path)
    $parent = Split-Path -Parent $resolvedPath
    if (-not [string]::Equals($parent, $resolvedModsPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Chemin de mod hors du dossier prévu : $resolvedPath"
    }
}

function Get-ModInfo([string]$path) {
    $infoPath = Join-Path $path 'Info.json'
    if (-not (Test-Path -LiteralPath $infoPath)) {
        return $null
    }

    return Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
}

function Show-Status {
    $entries = @(
        [pscustomobject]@{ Slot = 'Stable'; Path = $stablePath; Enabled = Test-Path -LiteralPath $stablePath; Info = Get-ModInfo $stablePath }
        [pscustomobject]@{ Slot = 'Experimental'; Path = $experimentalPath; Enabled = Test-Path -LiteralPath $experimentalPath; Info = Get-ModInfo $experimentalPath }
        [pscustomobject]@{ Slot = 'Stable disabled'; Path = $disabledStablePath; Enabled = $false; Info = Get-ModInfo $disabledStablePath }
        [pscustomobject]@{ Slot = 'Experimental disabled'; Path = $disabledExperimentalPath; Enabled = $false; Info = Get-ModInfo $disabledExperimentalPath }
    )

    $entries | Where-Object { $_.Info } | Select-Object Slot, Enabled, Path, @{Name='Id';Expression={$_.Info.id}}, @{Name='Version';Expression={$_.Info.version}}

    if ((Test-Path -LiteralPath $stablePath) -and (Test-Path -LiteralPath $experimentalPath)) {
        throw 'Les variantes stable et expérimentale sont actives simultanément. Corrigez avec EnableStable ou EnableExperimental.'
    }
}

function Build-Experimental {
    & (Join-Path $PSScriptRoot 'check-localization.ps1')
    $env:WOTR_PATH = $GamePath
    & dotnet build (Join-Path $repoPath 'src\WOTRMultiplayer.Localization.Generator\WOTRMultiplayer.Localization.Generator.csproj') --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'La génération des traductions a échoué.' }
    & dotnet test (Join-Path $repoPath 'src\WOTRMultiplayer.sln') --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'La compilation ou les tests ont échoué.' }
}

function Backup-Mod([string]$path, [string]$name) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $destination = Join-Path $backupRoot "$stamp-$name"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Copy-Item -LiteralPath $path -Destination $destination -Recurse
}

function Install-Experimental([bool]$testOnly) {
    if ($testOnly) {
        Assert-UnvalidatedTestRequested
    } else {
        Assert-RuntimeReady
    }
    Assert-GameStopped
    Assert-GameInstallation
    if ((Test-Path -LiteralPath $stablePath) -and (Test-Path -LiteralPath $experimentalPath)) {
        throw 'Installation refusée : les variantes stable et expérimentale sont déjà actives simultanément.'
    }
    if ($testOnly -and (Test-Path -LiteralPath $experimentalPath)) {
        throw 'Essai refusé : la variante expérimentale est déjà active. Réactivez au préalable la version stable.'
    }
    $targetPath = if ($testOnly) { $disabledExperimentalPath } elseif (Test-Path -LiteralPath $experimentalPath) { $experimentalPath } else { $disabledExperimentalPath }
    if ((Test-Path -LiteralPath $experimentalPath) -and (Test-Path -LiteralPath $disabledExperimentalPath)) {
        throw 'Installation refusée : deux dossiers expérimentaux existent déjà.'
    }
    Assert-SafeModPath $targetPath
    Build-Experimental
    Backup-Mod $targetPath 'experimental'
    if (Test-Path -LiteralPath $targetPath) {
        Remove-Item -LiteralPath $targetPath -Recurse
    }
    New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
    Copy-Item -Path (Join-Path $buildPath '*') -Destination $targetPath -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $targetPath 'localization') -Force | Out-Null
    Copy-Item -Path (Join-Path $repoPath 'localization\*.json') -Destination (Join-Path $targetPath 'localization') -Force
}

function Enable-Stable {
    Assert-GameStopped
    Assert-GameInstallation
    Assert-SafeModPath $stablePath
    Assert-SafeModPath $experimentalPath
    Assert-SafeModPath $disabledStablePath
    Assert-SafeModPath $disabledExperimentalPath
    if ((Test-Path -LiteralPath $stablePath) -and (Test-Path -LiteralPath $disabledStablePath)) {
        throw "Activation stable refusée : les deux dossiers existent déjà : $stablePath et $disabledStablePath"
    }
    if ((Test-Path -LiteralPath $experimentalPath) -and (Test-Path -LiteralPath $disabledExperimentalPath)) {
        throw "Activation stable refusée : les deux dossiers existent déjà : $experimentalPath et $disabledExperimentalPath"
    }
    if (-not (Test-Path -LiteralPath $stablePath) -and -not (Test-Path -LiteralPath $disabledStablePath)) {
        throw "Activation stable refusée : la version stable est absente."
    }
    if (Test-Path -LiteralPath $experimentalPath) {
        Move-Item -LiteralPath $experimentalPath -Destination $disabledExperimentalPath
    }
    if (Test-Path -LiteralPath $disabledStablePath) {
        Move-Item -LiteralPath $disabledStablePath -Destination $stablePath
    }
}

function Enable-Experimental([bool]$testOnly) {
    if ($testOnly) {
        Assert-UnvalidatedTestRequested
        Write-Warning 'Variante non validée : uniquement pour une session de test sur une copie de sauvegarde. Revenez à EnableStable après le test.'
    } else {
        Assert-RuntimeReady
    }
    Assert-GameStopped
    Assert-GameInstallation
    Assert-SafeModPath $stablePath
    Assert-SafeModPath $experimentalPath
    Assert-SafeModPath $disabledStablePath
    Assert-SafeModPath $disabledExperimentalPath
    if (-not (Test-Path -LiteralPath $experimentalPath) -and -not (Test-Path -LiteralPath $disabledExperimentalPath)) {
        throw "La variante expérimentale n'est pas installée. Lancez d'abord Install."
    }
    if ((Test-Path -LiteralPath $experimentalPath) -and (Test-Path -LiteralPath $disabledExperimentalPath)) {
        throw "Activation expérimentale refusée : les deux dossiers existent déjà : $experimentalPath et $disabledExperimentalPath"
    }
    if ((Test-Path -LiteralPath $stablePath) -and (Test-Path -LiteralPath $disabledStablePath)) {
        throw "Activation expérimentale refusée : les deux dossiers existent déjà : $stablePath et $disabledStablePath"
    }
    if (Test-Path -LiteralPath $stablePath) {
        Move-Item -LiteralPath $stablePath -Destination $disabledStablePath
    }
    if (Test-Path -LiteralPath $disabledExperimentalPath) {
        Move-Item -LiteralPath $disabledExperimentalPath -Destination $experimentalPath
    }
}

switch ($Action) {
    'Build' { Build-Experimental }
    'Install' { Install-Experimental $false }
    'StageTest' { Install-Experimental $true }
    'EnableStable' { Enable-Stable }
    'EnableExperimental' { Enable-Experimental $false }
    'EnableTestExperimental' { Enable-Experimental $true }
    'Status' { Show-Status }
}

if ($Action -ne 'Status') {
    Show-Status
}
