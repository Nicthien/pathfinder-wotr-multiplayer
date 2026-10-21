[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('host', 'client')]
    [string]$Profile,

    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure',

    [string]$ProfilePath,

    [switch]$CombatSaveTest
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProfilePath)) {
    $ProfilePath = Join-Path $repoPath "local-artifacts\runtime-profiles\$Profile"
}
$profilePath = [IO.Path]::GetFullPath($ProfilePath)
$marker = Join-Path $profilePath '.rtwp-test-profile'
$stable = Join-Path $GamePath 'Mods\00_WOTRMultiplayer'
$experimental = Join-Path $GamePath 'Mods\00_WOTRMultiplayerRTwP'
$executable = Join-Path $GamePath 'Wrath.exe'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
    throw "Profil A0 non préparé : $profilePath"
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Jeu introuvable : $executable"
}
if (Test-Path -LiteralPath $stable) {
    throw 'Le mod stable est actif. Le lanceur A0 refuse de démarrer pour éviter deux variantes simultanées.'
}
if (-not (Test-Path -LiteralPath $experimental)) {
    throw "La variante expérimentale A0 n'est pas active. Préparez un essai contrôlé avant de la basculer."
}
$logPath = Join-Path $profilePath 'Player.log'
$previousProfile = $env:WOTR_RTWP_TEST_PROFILE
$previousCombatSaveTest = $env:WOTR_RTWP_COMBAT_SAVE_TEST
try {
    $env:WOTR_RTWP_TEST_PROFILE = $profilePath
    $env:WOTR_RTWP_COMBAT_SAVE_TEST = if ($CombatSaveTest) { '1' } else { $null }
    # Explicit visible game launch requested by the caller; never install a mod here.
    $process = Start-Process -FilePath $executable -WorkingDirectory $GamePath -ArgumentList @(
        '-logFile', "`"$logPath`"", '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720'
    ) -PassThru
}
finally {
    $env:WOTR_RTWP_TEST_PROFILE = $previousProfile
    $env:WOTR_RTWP_COMBAT_SAVE_TEST = $previousCombatSaveTest
}
Write-Output "Instance $Profile lancée. PID=$($process.Id)"
Write-Output "Profil : $profilePath"
Write-Output "Journal : $logPath"
