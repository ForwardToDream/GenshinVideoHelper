@echo off
setlocal
cd /d "%~dp0"
if exist "artifacts\GenshinVideoHelper\GenshinVideoHelper.exe" (
  start "" "artifacts\GenshinVideoHelper\GenshinVideoHelper.exe"
  exit /b 0
)
dotnet run --project "src\GenshinVideoHelper.App\GenshinVideoHelper.App.csproj"
if errorlevel 1 pause
