@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-essai.ps1" -Action RetourStable %*
if errorlevel 1 echo ECHEC : lire le message ci-dessus.
pause
