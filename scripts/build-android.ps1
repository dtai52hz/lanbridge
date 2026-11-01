[CmdletBinding()]
param([ValidateSet('Build', 'Test', 'Publish')][string]$Task = 'Build')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localJdk = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.tools/android-jdk17') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($localJdk) { $env:JAVA_HOME = $localJdk.FullName }
if (-not $env:ANDROID_HOME) {
    $localSdk = Join-Path $projectRoot '.tools/android-sdk'
    if (Test-Path -LiteralPath $localSdk) { $env:ANDROID_HOME = $localSdk }
    elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_HOME = $env:ANDROID_SDK_ROOT }
}
$wrapper = Join-Path $projectRoot $(if ($IsWindows) { 'android/gradlew.bat' } else { 'android/gradlew' })
function Invoke-AndroidTool {
    param([string]$Tool, [string[]]$Arguments)
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Android tool failed (exit $LASTEXITCODE): $([IO.Path]::GetFileName($Tool))" }
}
if (-not $IsWindows) { [IO.File]::SetUnixFileMode($wrapper, [IO.UnixFileMode]493) }
$gradleTask = if ($Task -eq 'Test') { @('testDebugUnitTest', 'lintRelease') } else { @('assembleRelease') }
Invoke-AndroidTool $wrapper (@('-p', (Join-Path $projectRoot 'android'), '--no-daemon') + $gradleTask)
if ($Task -ne 'Publish') { return }
if (-not $env:JAVA_HOME -or -not $env:ANDROID_HOME) { throw 'Publish needs JAVA_HOME (JDK 17+) and ANDROID_HOME (SDK build-tools 36.0.0).' }
$toolSuffix = if ($IsWindows) { '.exe' } else { '' }
$keytool = Join-Path $env:JAVA_HOME "bin/keytool$toolSuffix"
$align = Join-Path $env:ANDROID_HOME "build-tools/36.0.0/zipalign$toolSuffix"
$signer = Join-Path $env:ANDROID_HOME $(if ($IsWindows) { 'build-tools/36.0.0/apksigner.bat' } else { 'build-tools/36.0.0/apksigner' })
$signing = Join-Path $projectRoot '.tools/android-signing'
New-Item -ItemType Directory -Path $signing -Force | Out-Null
$keystore = Join-Path $signing 'lanbridge-android.p12'
$passwordFile = Join-Path $signing 'password.txt'
if (-not (Test-Path -LiteralPath $keystore)) {
    if (Test-Path -LiteralPath $passwordFile) { throw 'Signing password exists without keystore; restore the signing key before publishing updates.' }
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($passwordFile, $password, [Text.UTF8Encoding]::new($false))
    $password = $null
    Invoke-AndroidTool $keytool @('-genkeypair', '-keystore', $keystore, '-storetype', 'PKCS12', '-alias', 'lanbridge',
        '-keyalg', 'RSA', '-keysize', '3072', '-validity', '10000', '-dname', 'CN=LanBridge Android, O=LanBridge',
        '-storepass:file', $passwordFile, '-keypass:file', $passwordFile)
}
if (-not (Test-Path -LiteralPath $passwordFile)) { throw 'Restore the Android signing password before publishing.' }
$gradle = Get-Content -LiteralPath (Join-Path $projectRoot 'android/app/build.gradle') -Raw
$version = [regex]::Match($gradle, "versionName\s+'([0-9.]+-preview\.[0-9]+)'").Groups[1].Value
if (-not $version) { throw 'Missing Android preview version.' }
$release = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$unsigned = Join-Path $projectRoot 'android/app/build/outputs/apk/release/app-release-unsigned.apk'
$aligned = Join-Path $signing 'aligned.apk'
$apk = Join-Path $release "LanBridge-Android-$version.apk"
Invoke-AndroidTool $align @('-f', '-p', '4', $unsigned, $aligned)
Invoke-AndroidTool $signer @('sign', '--ks', $keystore, '--ks-key-alias', 'lanbridge', '--ks-pass', "file:$passwordFile", '--out', $apk, $aligned)
Invoke-AndroidTool $signer @('verify', '--verbose', '--print-certs', $apk)
$digest = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$apk.sha256", "$digest  $([IO.Path]::GetFileName($apk))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Published signed APK: $apk"
