@echo off
rem Verifie automatiquement que Centre PC compile apres chaque modification (resultat dans verification.log)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0surveiller.ps1"
