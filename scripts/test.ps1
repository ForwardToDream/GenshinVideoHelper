param([switch]$Browser, [switch]$Pip, [switch]$RenderUi, [switch]$Display, [switch]$Follow, [switch]$Preview, [switch]$Lifecycle, [switch]$Latency, [switch]$Background, [switch]$Progress, [switch]$Login, [switch]$Vision, [switch]$VisionAlgorithms, [switch]$VisionBrowser, [switch]$BrowserSwitch, [switch]$Edge)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'tests/GenshinVideoHelper.Smoke/GenshinVideoHelper.Smoke.csproj'
if ($VisionAlgorithms) {
    dotnet test (Join-Path $taskRoot 'tests/GenshinVideoHelper.Infrastructure.Tests/GenshinVideoHelper.Infrastructure.Tests.csproj') -c Release --filter 'FullyQualifiedName~.Vision.'
}
else { dotnet test (Join-Path $taskRoot 'GenshinVideoHelper.sln') -c Release }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Browser -or $Pip -or $Follow -or $Lifecycle -or $Preview -or $RenderUi -or $Display -or $Latency -or $Background -or $Progress -or $Login -or $Vision -or $VisionBrowser -or $BrowserSwitch) {
    dotnet build $taskProject -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Browser -or $Pip -or $Follow -or $Lifecycle -or $Vision -or $VisionBrowser) {
    New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot 'artifacts/test-media') | Out-Null
    ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart -y (Join-Path $taskRoot 'artifacts/test-media/sample.mp4')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Vision -or $VisionBrowser) {
    ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 12 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart -y (Join-Path $taskRoot 'artifacts/test-media/sample-hd.mp4')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$taskBrowserArgs = @()
if ($Display) {
    dotnet run --project $taskProject -c Release --no-build -- --display $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Edge) { $taskBrowserArgs = @('--edge') }
if ($Browser) {
    dotnet run --project $taskProject -c Release --no-build -- --browser $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Pip) {
    dotnet run --project $taskProject -c Release --no-build -- --pip $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($RenderUi) {
    dotnet run --project $taskProject -c Release --no-build -- --render-ui $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Preview) {
    dotnet run --project $taskProject -c Release --no-build -- --preview $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Follow) {
    dotnet run --project $taskProject -c Release --no-build -- --follow $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Lifecycle) {
    dotnet run --project $taskProject -c Release --no-build -- --lifecycle $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Latency) {
    dotnet run --project $taskProject -c Release --no-build -- --latency $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Background) {
    dotnet run --project $taskProject -c Release --no-build -- --background $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Progress) {
    dotnet run --project $taskProject -c Release --no-build -- --progress $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Login) {
    dotnet run --project $taskProject -c Release --no-build -- --login $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Vision) {
    dotnet run --project $taskProject -c Release --no-build -- --vision $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($VisionBrowser) {
    dotnet run --project $taskProject -c Release --no-build -- --vision-browser $taskRoot @taskBrowserArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($BrowserSwitch) {
    dotnet run --project $taskProject -c Release --no-build -- --browser-switch $taskRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
