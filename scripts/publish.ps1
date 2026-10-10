$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $taskRoot 'artifacts'
$destination = Join-Path $artifacts 'GenshinVideoHelper'
$staging = Join-Path $artifacts ('publish-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $artifacts 'GenshinVideoHelper-win-x64.zip'
try {
    dotnet publish (Join-Path $taskRoot 'src/GenshinVideoHelper.App/GenshinVideoHelper.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }
    if (!(Test-Path -LiteralPath (Join-Path $staging 'GenshinVideoHelper.exe'))) { throw '未生成 exe。' }
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
    Write-Host "可直接运行：$(Join-Path $destination 'GenshinVideoHelper.exe')"
    Write-Host "分发包：$archive"
}
finally {
    # Only this invocation's unique staging directory, verified within artifacts.
    $resolved = [IO.Path]::GetFullPath($staging)
    if (!$resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '暂存目录越界。' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
