param([string]$JavaHome = 'D:\tool\jdk21')
$ErrorActionPreference = 'Stop'
$env:JAVA_HOME = $JavaHome
$configuration = Join-Path (Split-Path -Parent $PSScriptRoot) '.secrets\android-signing.json'
if (Test-Path -LiteralPath $configuration) {
    $signing = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json
    $env:BLUEBRIDGE_KEYSTORE = $signing.keystore
    $env:BLUEBRIDGE_STORE_PASSWORD = $signing.storePassword
    $env:BLUEBRIDGE_KEY_PASSWORD = $signing.keyPassword
}
if (!$env:BLUEBRIDGE_KEYSTORE -or !$env:BLUEBRIDGE_STORE_PASSWORD -or !$env:BLUEBRIDGE_KEY_PASSWORD) {
    throw '请先配置 BLUEBRIDGE_KEYSTORE、BLUEBRIDGE_STORE_PASSWORD、BLUEBRIDGE_KEY_PASSWORD，或项目私有 .secrets/android-signing.json。'
}
Push-Location $PSScriptRoot
try {
    & '.\gradlew.bat' :core:test :core:jacocoTestCoverageVerification :app:testDebugUnitTest :app:assembleRelease :app:lintDebug
    if ($LASTEXITCODE -ne 0) { throw 'zhanhBlueB Android 构建或检查失败。' }
} finally { Pop-Location }
