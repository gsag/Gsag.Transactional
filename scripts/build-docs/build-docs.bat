@echo off
cd /d "%~dp0..\.."
dotnet run "scripts/build-docs/build-docs.cs"
pause
