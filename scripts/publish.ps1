$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$props = [xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)
$releaseVersion = [string]$props.Project.PropertyGroup.Version
if ($releaseVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw '必须设置三段正式版本号。' }
$artifacts = Join-Path $taskRoot 'artifacts'
$destination = Join-Path $artifacts 'GenshinVideoHelper'
$staging = Join-Path $artifacts ('publish-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $artifacts 'GenshinVideoHelper-win-x64.zip'
try {
    dotnet publish (Join-Path $taskRoot 'src/GenshinVideoHelper.App/GenshinVideoHelper.App.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }
    $exe = Join-Path $staging 'GenshinVideoHelper.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw '未生成 exe。' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ("$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" -cne $releaseVersion -or $info.FilePrivatePart -ne 0) {
        throw 'exe 版本与源码不一致。'
    }
    # Package only build output, never the destination's user data.
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $staging -File).FullName -DestinationPath $archive -Force
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $staging -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
    }
    # Remove obsolete flat framework-dependent output; keep .gvh and any other user files.
    Get-ChildItem -LiteralPath $destination -File | Where-Object {
        $_.Name -like 'GenshinVideoHelper*.dll' -or $_.Name -like 'GenshinVideoHelper*.pdb' -or
        $_.Name -in @('GenshinVideoHelper.deps.json', 'GenshinVideoHelper.runtimeconfig.json')
    } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Write-Host "版本：$releaseVersion"
    Write-Host "exe：$(Join-Path $destination 'GenshinVideoHelper.exe')（$([Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)) MiB）"
    Write-Host '运行要求：.NET 8 Desktop Runtime（Windows x64）。'
    Write-Host "分发包：$archive"
}
finally {
    # Only this invocation's unique staging directory, verified within artifacts.
    $resolved = [IO.Path]::GetFullPath($staging)
    if (!$resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '暂存目录越界。' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
