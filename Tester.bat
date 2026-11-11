@echo off
rem Lance les tests automatiques de Centre PC (les memes que sur GitHub)
cd /d "%~dp0"
dotnet test Tests\CentrePC.Tests\CentrePC.Tests.csproj -c Release --nologo
pause
