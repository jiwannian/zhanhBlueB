param([string]$JavaHome = 'D:\tool\jdk21', [string]$Python = 'python', [string]$Version = '1.0.2', [string]$AndroidVersion = '1.0.0')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'verify.ps1') -JavaHome $JavaHome -Python $Python
    & (Join-Path $projectRoot 'android\build-release.ps1') -JavaHome $JavaHome
    # A fresh directory prevents old executable names or runtime files entering a release.
    $publishDirectory = Join-Path $projectRoot ('artifacts\publish-' + [guid]::NewGuid().ToString('N'))
    & dotnet publish 'windows/BlueBridge.Desktop/BlueBridge.Desktop.csproj' -c Release -r win-x64 --self-contained true '-p:PublishReadyToRun=true' -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Windows 发布构建失败。' }
    $releaseDirectory = Join-Path $projectRoot ('artifacts\release\' + $Version)
    & $Python 'tools/package_release.py' --apk 'android/app/build/outputs/apk/release/app-release.apk' --windows-publish $publishDirectory --version $Version --android-version $AndroidVersion --output-dir $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw '交付打包失败。' }
    & $Python 'tools/check_release.py' --directory $releaseDirectory --version $Version --android-version $AndroidVersion
    if ($LASTEXITCODE -ne 0) { throw '交付包校验失败。' }
} finally { Pop-Location }
