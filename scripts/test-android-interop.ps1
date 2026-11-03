param([switch]$SkipCompile, [string]$JavaClasspath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$androidRoot = Join-Path $projectRoot 'android'
$localAndroidSdk = Join-Path $projectRoot '.tools/android-sdk'
if (-not $env:ANDROID_HOME -and (Test-Path -LiteralPath $localAndroidSdk)) { $env:ANDROID_HOME = $localAndroidSdk }
$isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$localJdk = Join-Path $projectRoot '.tools/android-jdk17/jdk-17.0.20.1+1'
$javaDirectory = if (Test-Path -LiteralPath $localJdk) { $localJdk } else { $env:JAVA_HOME }
$javaCommand = if ($javaDirectory) { Join-Path $javaDirectory $(if ($isWindowsHost) { 'bin/java.exe' } else { 'bin/java' }) } else { (Get-Command java -ErrorAction Stop).Source }

if (-not $SkipCompile) {
    $previousJavaHome = $env:JAVA_HOME
    try {
        if ($javaDirectory) { $env:JAVA_HOME = $javaDirectory }
        if ($isWindowsHost) { & (Join-Path $androidRoot 'gradlew.bat') -p $androidRoot --no-daemon exportInteropClasspath }
        else { & bash (Join-Path $androidRoot 'gradlew') -p $androidRoot --no-daemon exportInteropClasspath }
        if ($LASTEXITCODE -ne 0) { throw 'Cannot compile Android Java interop classes.' }
    } finally { $env:JAVA_HOME = $previousJavaHome }
    & $dotnetCommand build (Join-Path $projectRoot 'tests/AndroidInteropSmoke/AndroidInteropSmoke.csproj') --configuration Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compile C# loopback interop server.' }
}

$classpathFile = Join-Path $androidRoot 'app/build/interop-classpath.txt'
if (-not $JavaClasspath -and -not (Test-Path -LiteralPath $classpathFile)) { throw 'Missing Gradle classpath. Run without -SkipCompile first.' }
$javaClasspath = if ($JavaClasspath) { $JavaClasspath } else { [IO.File]::ReadAllText($classpathFile).Trim() }
$serverDll = Join-Path $projectRoot 'tests/AndroidInteropSmoke/bin/Release/net10.0/AndroidInteropSmoke.dll'
if (-not (Test-Path -LiteralPath $serverDll)) { throw 'Missing C# test binary. Run without -SkipCompile first.' }

foreach ($mode in @('device', 'code', 'files-device', 'files-code')) {
    $serverInfo = [Diagnostics.ProcessStartInfo]::new()
    $serverInfo.FileName = $dotnetCommand
    $serverInfo.WorkingDirectory = $projectRoot
    $serverInfo.UseShellExecute = $false
    $serverInfo.CreateNoWindow = $true
    $serverInfo.RedirectStandardOutput = $true
    $serverInfo.RedirectStandardError = $true
    $serverInfo.ArgumentList.Add($serverDll)
    $serverInfo.ArgumentList.Add($mode)
    $server = [Diagnostics.Process]::Start($serverInfo)
    $probe = $null
    try {
        $readiness = $server.StandardOutput.ReadLineAsync()
        if (-not $readiness.Wait(15000)) { throw 'C# loopback server readiness timed out.' }
        $testPairingCode = $readiness.Result
        if (-not $testPairingCode -or -not $testPairingCode.StartsWith('LB1|127.0.0.1|')) {
            throw ('Loopback server did not start: ' + $server.StandardError.ReadToEnd())
        }
        $probeInfo = [Diagnostics.ProcessStartInfo]::new()
        $probeInfo.FileName = $javaCommand
        $probeInfo.WorkingDirectory = $projectRoot
        $probeInfo.UseShellExecute = $false
        $probeInfo.CreateNoWindow = $true
        $probeInfo.RedirectStandardOutput = $true
        $probeInfo.RedirectStandardError = $true
        foreach ($argument in @('-cp', $javaClasspath, 'io.github.dtai52hz.lanbridge.net.DesktopInteropProbe', $mode, $testPairingCode)) {
            $probeInfo.ArgumentList.Add($argument)
        }
        $probe = [Diagnostics.Process]::Start($probeInfo)
        if (-not $probe.WaitForExit(40000)) { throw 'Java loopback probe timed out.' }
        $probe.StandardOutput.ReadToEnd().Trim()
        $probeError = $probe.StandardError.ReadToEnd()
        if ($probe.ExitCode -ne 0) { throw ('Java interop verification failed: ' + $probeError) }
        if (-not $server.WaitForExit(5000)) { throw 'C# loopback server failed to finish.' }
        $server.StandardOutput.ReadToEnd().Trim()
        $serverError = $server.StandardError.ReadToEnd()
        if ($server.ExitCode -ne 0) { throw ('C# interop verification failed: ' + $serverError) }
    } finally {
        if ($null -ne $probe) { if (-not $probe.HasExited) { $probe.Kill() }; $probe.Dispose() }
        if (-not $server.HasExited) { $server.Kill() }
        $server.Dispose()
    }
}
