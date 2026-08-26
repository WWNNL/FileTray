# FileTray 本地打包脚本(Windows):
#   - win-x64 便携版 zip + Velopack 安装版 Setup.exe
#   - macOS .app(x64/arm64)zip(交叉编译;.dmg 需 macOS,走 .github/workflows/release.yml)
# 用法: powershell -File pack.ps1 [-Version 1.0.0]
param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
# 脚本位于仓库根
$Root = $PSScriptRoot
Set-Location $Root

# dotnet tool vpk 不在 PATH 时补上
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    $env:PATH = "$env:USERPROFILE\.dotnet/tools;$env:PATH"
}

Write-Host "== 发布 win-x64(单文件)"
dotnet publish FileTray/FileTray.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64

Write-Host "== 便携版目录"
$Stage = "build/portable/FileTray"
Remove-Item -Recurse -Force "build/portable" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
Copy-Item publish/win-x64/FileTray.exe $Stage/
Copy-Item publish/win-x64/*.dll $Stage/

Write-Host "== Velopack 安装版"
Remove-Item -Recurse -Force build/dist-velopack -ErrorAction SilentlyContinue
vpk pack --packId FileTray --packVersion $Version --packTitle FileTray --packAuthors WWNNL `
    --mainExe FileTray.exe --packDir build/portable/FileTray --icon FileTray/Assets/avalonia-logo.ico `
    --outputDir build/dist-velopack --channel win-x64 --runtime win-x64

Write-Host "== macOS .app(x64/arm64 交叉编译)"
foreach ($arch in @("x64", "arm64")) {
    dotnet publish FileTray/FileTray.csproj -c Release -r osx-$arch --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/osx-$arch-sf

    $App = "build/FileTray-$arch.app"
    Remove-Item -Recurse -Force $App -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path "$App/Contents/MacOS", "$App/Contents/Resources" | Out-Null
    Copy-Item publish/osx-$arch-sf/FileTray "$App/Contents/MacOS/"
    Copy-Item publish/osx-$arch-sf/*.dylib "$App/Contents/MacOS/"
    Copy-Item packaging/FileTray.icns "$App/Contents/Resources/FileTray.icns"
    (Get-Content packaging/Info.plist -Raw) -replace "1\.0\.0", $Version | Set-Content "$App/Contents/Info.plist"
}

Write-Host "== 汇总到 build/dist"
New-Item -ItemType Directory -Force -Path build/dist | Out-Null
Remove-Item build/dist/* -ErrorAction SilentlyContinue
Copy-Item build/dist-velopack/FileTray-win-x64-Setup.exe "build/dist/FileTray-win-x64-Setup-$Version.exe"
Copy-Item build/dist-velopack/FileTray-win-x64-Portable.zip "build/dist/FileTray-win-x64-portable-$Version.zip"
# tar(bsdtar)打 zip:对 .app 目录结构无损,且避开 Compress-Archive 对新写文件的占用问题
tar -a -cf "build/dist/FileTray-macos-x64-$Version.zip" -C build FileTray-x64.app
tar -a -cf "build/dist/FileTray-macos-arm64-$Version.zip" -C build FileTray-arm64.app

Write-Host "`n== 完成,产物:"
Get-ChildItem build/dist | Format-Table Name, @{L="SizeMB";E={[math]::Round($_.Length/1MB,1)}}
Write-Host "macOS .dmg 需在 macOS 上生成:推送 tag(如 v$Version)后由 GitHub Actions 自动产出并发布 Release。"
