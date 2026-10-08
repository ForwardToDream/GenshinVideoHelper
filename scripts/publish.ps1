$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $taskRoot 'src/GenshinVideoHelper.App/GenshinVideoHelper.App.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $taskRoot 'artifacts/GenshinVideoHelper')
exit $LASTEXITCODE
