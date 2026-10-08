param([switch]$Browser, [switch]$Pip, [switch]$RenderUi, [switch]$Follow, [switch]$Preview, [switch]$Lifecycle)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'tests/GenshinVideoHelper.Smoke/GenshinVideoHelper.Smoke.csproj'
dotnet run --project $taskProject
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Browser -or $Pip -or $Follow -or $Lifecycle) {
    New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot 'artifacts/test-media') | Out-Null
    ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart -y (Join-Path $taskRoot 'artifacts/test-media/sample.mp4')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Browser) {
    dotnet run --project $taskProject -- --browser $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Pip) {
    dotnet run --project $taskProject -- --pip $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($RenderUi) {
    dotnet run --project $taskProject -- --render-ui $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Preview) {
    dotnet run --project $taskProject -- --preview $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Follow) {
    dotnet run --project $taskProject -- --follow $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Lifecycle) {
    dotnet run --project $taskProject -- --lifecycle $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
