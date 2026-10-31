@echo off
setlocal
set "REPO=%~dp0"
set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Pathfinder Second Adventure"

if not exist "%GAME%\Wrath.exe" (
    echo [ERREUR] Jeu introuvable : %GAME%
    echo Modifiez la variable GAME en tete de ce script si le jeu est ailleurs.
    pause
    exit /b 1
)

echo Lancement de l'instance HOTE...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO%tools\start-rtwp-a0-instance.ps1" -Profile host -Automation -GamePath "%GAME%"
if errorlevel 1 (
    echo [ERREUR] L'instance hote a refuse de demarrer (message ci-dessus).
    pause
    exit /b 1
)

echo.
echo Lancement de l'instance CLIENTE...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO%tools\start-rtwp-a0-instance.ps1" -Profile client -Automation -GamePath "%GAME%"
if errorlevel 1 (
    echo [ERREUR] L'instance cliente a refuse de demarrer (message ci-dessus).
    pause
    exit /b 1
)

echo.
echo Deux instances lancees. Menus principaux suffisants.
echo Passez-les en revue puis fermez cette fenetre.
pause
