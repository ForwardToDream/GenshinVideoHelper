param([switch]$Browser, [switch]$Pip, [switch]$RenderUi, [switch]$Follow, [switch]$Preview, [switch]$Lifecycle, [switch]$Latency)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'tests/GenshinVideoHelper.Smoke/GenshinVideoHelper.Smoke.csproj'
dotnet test (Join-Path $taskRoot 'GenshinVideoHelper.sln') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Browser -or $Pip -or $Follow -or $Lifecycle -or $Preview -or $RenderUi -or $Latency) {
    dotnet build $taskProject -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Browser -or $Pip -or $Follow -or $Lifecycle) {
    New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot 'artifacts/test-media') | Out-Null
    ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart -y (Join-Path $taskRoot 'artifacts/test-media/sample.mp4')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Browser) {
    dotnet run --project $taskProject -c Release --no-build -- --browser $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Pip) {
    dotnet run --project $taskProject -c Release --no-build -- --pip $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($RenderUi) {
    dotnet run --project $taskProject -c Release --no-build -- --render-ui $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Preview) {
    dotnet run --project $taskProject -c Release --no-build -- --preview $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Follow) {
    dotnet run --project $taskProject -c Release --no-build -- --follow $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Lifecycle) {
    dotnet run --project $taskProject -c Release --no-build -- --lifecycle $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Latency) {
    dotnet run --project $taskProject -c Release --no-build -- --latency $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
