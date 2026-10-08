$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $taskRoot 'src/GenshinVideoHelper.App/GenshinVideoHelper.App.csproj')
exit $LASTEXITCODE
