[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Build', 'Install', 'EnableStable', 'EnableExperimental', 'Status')]
    [string]$Action,

    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure'
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

function Assert-GameStopped {
    if (Get-Process -Name 'Wrath' -ErrorAction SilentlyContinue) {
        throw 'Wrath.exe est actif. Fermez le jeu avant de modifier le mod installé.'
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

function Install-Experimental {
    Assert-GameStopped
    Build-Experimental
    Backup-Mod $experimentalPath 'experimental'
    if (Test-Path -LiteralPath $experimentalPath) {
        Remove-Item -LiteralPath $experimentalPath -Recurse
    }
    New-Item -ItemType Directory -Path $experimentalPath -Force | Out-Null
    Copy-Item -Path (Join-Path $buildPath '*') -Destination $experimentalPath -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $experimentalPath 'localization') -Force | Out-Null
    Copy-Item -Path (Join-Path $repoPath 'localization\*.json') -Destination (Join-Path $experimentalPath 'localization') -Force
}

function Enable-Stable {
    Assert-GameStopped
    if (Test-Path -LiteralPath $experimentalPath) {
        if (Test-Path -LiteralPath $disabledExperimentalPath) { throw "Le dossier cible existe déjà: $disabledExperimentalPath" }
        Move-Item -LiteralPath $experimentalPath -Destination $disabledExperimentalPath
    }
    if (Test-Path -LiteralPath $disabledStablePath) {
        if (Test-Path -LiteralPath $stablePath) { throw "Le dossier cible existe déjà: $stablePath" }
        Move-Item -LiteralPath $disabledStablePath -Destination $stablePath
    }
}

function Enable-Experimental {
    Assert-GameStopped
    if (Test-Path -LiteralPath $stablePath) {
        if (Test-Path -LiteralPath $disabledStablePath) { throw "Le dossier cible existe déjà: $disabledStablePath" }
        Move-Item -LiteralPath $stablePath -Destination $disabledStablePath
    }
    if (Test-Path -LiteralPath $disabledExperimentalPath) {
        if (Test-Path -LiteralPath $experimentalPath) { throw "Le dossier cible existe déjà: $experimentalPath" }
        Move-Item -LiteralPath $disabledExperimentalPath -Destination $experimentalPath
    }
    if (-not (Test-Path -LiteralPath $experimentalPath)) {
        throw "La variante expérimentale n'est pas installée. Lancez d'abord Install."
    }
}

switch ($Action) {
    'Build' { Build-Experimental }
    'Install' { Install-Experimental }
    'EnableStable' { Enable-Stable }
    'EnableExperimental' { Enable-Experimental }
    'Status' { Show-Status }
}

if ($Action -ne 'Status') {
    Show-Status
}
