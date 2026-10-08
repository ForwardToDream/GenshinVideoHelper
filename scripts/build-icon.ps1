# Original vector artwork in AppIcon.xaml; generate Windows icon sizes without external tooling.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskResources = Join-Path $taskRoot 'src/GenshinVideoHelper.App/Resources'
$taskDrawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $taskResources 'AppIcon.xaml')))
$taskFrames = @()
$taskSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($taskSize in $taskSizes) {
    $taskVisual = [Windows.Media.DrawingVisual]::new()
    $taskContext = $taskVisual.RenderOpen()
    $taskContext.DrawImage($taskDrawing, [Windows.Rect]::new(0, 0, $taskSize, $taskSize))
    $taskContext.Close()
    $taskBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize, $taskSize, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskStream = [IO.MemoryStream]::new()
    $taskEncoder.Save($taskStream)
    $taskFrames += ,($taskStream.ToArray())
    if ($taskSize -eq 256) { [IO.File]::WriteAllBytes((Join-Path $taskResources 'AppIcon.png'), $taskStream.ToArray()) }
    $taskStream.Dispose()
}
$taskStream = [IO.MemoryStream]::new()
$taskWriter = [IO.BinaryWriter]::new($taskStream)
$taskWriter.Write([uint16]0)
$taskWriter.Write([uint16]1)
$taskWriter.Write([uint16]$taskSizes.Count)
$taskOffset = 6 + 16 * $taskSizes.Count
for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
    $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
    $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
    $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
    $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
    $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
    $taskOffset += $taskFrames[$taskIndex].Length
}
foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame) }
$taskWriter.Flush()
[IO.File]::WriteAllBytes((Join-Path $taskResources 'AppIcon.ico'), $taskStream.ToArray())
$taskWriter.Dispose()
Write-Output 'Generated original app icon: 16–256 px (9 sizes).'