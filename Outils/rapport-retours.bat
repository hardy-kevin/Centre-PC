@echo off
rem Rapport local des retours d'experience (Retours\rapport.md, jamais publie sur GitHub)
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "Outils\rapport-retours.ps1" %* > "Retours-dernier-lancement.log" 2>&1
if exist "Retours\rapport.md" start "" notepad.exe "Retours\rapport.md"
