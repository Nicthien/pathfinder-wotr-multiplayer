[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Jeux\Steam\steamapps\common\Pathfinder Second Adventure',
    [string]$CandidateFingerprint
)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$gameManaged = Join-Path $GamePath 'Wrath_Data\Managed'
$monoLibrary = Join-Path $GamePath 'MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll'
$harmonyLibrary = Join-Path $gameManaged 'UnityModManager\0Harmony.dll'
$gameAssembly = Join-Path $gameManaged 'Assembly-CSharp.dll'
$modAssembly = Join-Path $repoPath 'src\WOTRMultiplayer\bin\Release\net481\WOTRMultiplayer.dll'
if ($CandidateFingerprint) {
    if ($CandidateFingerprint -cnotmatch '^[A-F0-9]{64}$') { throw 'Empreinte de candidat invalide.' }
    $modAssembly = Join-Path $repoPath "local-artifacts\a0-candidates\$CandidateFingerprint\00_WOTRMultiplayerRTwP\WOTRMultiplayer.dll"
}
foreach ($required in @($monoLibrary, $harmonyLibrary, $gameAssembly, $modAssembly)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Fichier de diagnostic absent : $required" }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Compilateur C++ absent : vswhere introuvable.' }
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Compilateur C++ x64 absent.' }
$vsSetup = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'
$probeRoot = Join-Path $repoPath 'local-artifacts\harmony-dispatch-probe'
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$nativeExe = Join-Path $probeRoot 'mono-probe.exe'
$nativeObject = Join-Path $probeRoot 'mono-probe.obj'
$nativeSource = Join-Path $PSScriptRoot 'HarmonyDispatchProbe\mono-probe.cpp'
# These values enter cmd only for the compiler environment. Reject shell syntax.
foreach ($path in @($vsSetup, $nativeExe, $nativeObject, $nativeSource)) {
    if ($path -match '["&|<>%^\r\n]') { throw "Chemin de compilation non pris en charge : $path" }
}
$compile = 'call "{0}" -no_logo -arch=x64 -host_arch=x64 && cl.exe /nologo /EHsc /W4 /Fe:"{1}" /Fo:"{2}" "{3}"' -f $vsSetup, $nativeExe, $nativeObject, $nativeSource
& cmd.exe /d /c $compile
if ($LASTEXITCODE -ne 0) { throw 'Compilation du lanceur Mono isolé échouée.' }
$previousGamePath = $env:WOTR_PATH
try {
    $env:WOTR_PATH = $GamePath
    & dotnet build (Join-Path $PSScriptRoot 'HarmonyDispatchProbe\HarmonyDispatchProbe.csproj') --configuration Release --verbosity minimal "-p:RtwpProbeModPath=$modAssembly"
    if ($LASTEXITCODE -ne 0) { throw 'Compilation du diagnostic géré échouée.' }
} finally { $env:WOTR_PATH = $previousGamePath }
$probeBin = Join-Path $PSScriptRoot 'HarmonyDispatchProbe\bin\Release\net481'
$probeExe = Join-Path $probeBin 'HarmonyDispatchProbe.exe'
$modHash = (Get-FileHash -LiteralPath $modAssembly -Algorithm SHA256).Hash
foreach ($name in @('WOTRMultiplayer.dll', 'WOTRMultiplayer.Networking.dll')) {
    $expected = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $modAssembly) $name) -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $probeBin $name) -Algorithm SHA256).Hash -ne $expected) {
        throw "Le diagnostic ne charge pas la DLL exacte demandée : $name"
    }
}
# No game executable or UnityPlayer is started. The native helper loads Mono
# and executes only the diagnostic's managed Main; no Game.Instance is accessed.
$logPath = Join-Path $probeRoot 'latest.log'
$nativeArgs = @($monoLibrary, $gameManaged, $probeExe)
if ($CandidateFingerprint) { $nativeArgs += @((Split-Path -Parent $modAssembly), $CandidateFingerprint) }
& $nativeExe @nativeArgs 2>&1 | Tee-Object -FilePath $logPath
$exitCode = $LASTEXITCODE
$passed = $exitCode -eq 0 -and (Select-String -LiteralPath $logPath -SimpleMatch 'Mono=True' -Quiet)
$proof = [ordered]@{
    createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    scope = 'Offline rule/vital/calculated-stat/buff/area guards, native memory/fragment transfer and client load lease with synthetic field hydration; no native world capture/load, rendering or game/combat validation'
    success = [bool]$passed
    exitCode = $exitCode
    candidateFingerprint = if ($CandidateFingerprint) { $CandidateFingerprint } else { $null }
    modAssemblySha256 = $modHash
    networkAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $modAssembly) 'WOTRMultiplayer.Networking.dll') -Algorithm SHA256).Hash
    gameAssemblySha256 = (Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash
    monoLibrarySha256 = (Get-FileHash -LiteralPath $monoLibrary -Algorithm SHA256).Hash
    harmonyLibrarySha256 = (Get-FileHash -LiteralPath $harmonyLibrary -Algorithm SHA256).Hash
    managedProbeSha256 = (Get-FileHash -LiteralPath $probeExe -Algorithm SHA256).Hash
    nativeProbeSha256 = (Get-FileHash -LiteralPath $nativeExe -Algorithm SHA256).Hash
    gameStarted = $false
    runtimeValidated = $false
}
$proof | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $probeRoot 'latest.json') -Encoding UTF8
if (-not $passed) { throw "Le diagnostic hors jeu a échoué. Voir $logPath" }
if ($CandidateFingerprint) {
    $evidenceRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $modAssembly)) 'offline-rule-dispatch'
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    Copy-Item -LiteralPath $logPath, (Join-Path $probeRoot 'latest.json'), $probeExe, $nativeExe -Destination $evidenceRoot -Force
}
Write-Output "Interception des callbacks vérifiée hors jeu : $modHash"
