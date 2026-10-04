[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('host', 'client')]
    [string]$Profile,

    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure'
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$profilePath = Join-Path $repoPath "local-artifacts\runtime-profiles\$Profile"
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
$env:WOTR_RTWP_TEST_PROFILE = $profilePath
$logPath = Join-Path $profilePath 'Player.log'
$process = Start-Process -FilePath $executable -ArgumentList @('-logFile', "`"$logPath`"") -PassThru
Write-Output "Instance $Profile lancée. PID=$($process.Id)"
Write-Output "Profil : $profilePath"
Write-Output "Journal : $logPath"
