@echo off
setlocal

REM Always run from repository root, even when launched elsewhere.
cd /d "%~dp0"

dotnet run --project "MediaFileAnalyzer.csproj" -c Debug
