@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-essai.ps1" -Action Activer -AutoriserVersionExperimentale %*
if errorlevel 1 echo ECHEC : lire le message ci-dessus. Ne pas lancer le jeu avec deux variantes.
pause
