[CmdletBinding()]
param(
    [ValidateSet('Build', 'Test', 'Publish')][string]$Task = 'Build',
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$desktopDotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-DesktopDotnet {
    & $desktopDotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)." }
}
Push-Location $projectRoot
try {
    $desktopProject = 'crossplatform/LanBridge.Desktop/LanBridge.Desktop.csproj'
    if ($Task -eq 'Build') { Invoke-DesktopDotnet build $desktopProject -c Release --nologo; return }
    if ($Task -eq 'Test') {
        foreach ($testProject in @(
            'crossplatform/LanBridge.Platform.Smoke/LanBridge.Platform.Smoke.csproj',
            'crossplatform/LanBridge.Linux.Smoke/LanBridge.Linux.Smoke.csproj',
            'tests/CrossPlatformSmoke/CrossPlatformSmoke.csproj',
            'crossplatform/LanBridge.Desktop.Smoke/LanBridge.Desktop.Smoke.csproj',
            'tests/TextInputSmoke/TextInputSmoke.csproj',
            'tests/FileSmoke/FileSmoke.csproj',
            'tests/TransportSmoke/TransportSmoke.csproj'
            'tests/ScreenStreamingSmoke/ScreenStreamingSmoke.csproj'
        )) { Invoke-DesktopDotnet run --project $testProject -c Release }
        return
    }
    [xml]$projectXml = Get-Content -LiteralPath $desktopProject -Raw
    $desktopVersion = [string]$projectXml.Project.PropertyGroup.Version
    if ($desktopVersion -notmatch '^\d+\.\d+\.\d+-preview\.\d+$') { throw 'Desktop preview version is invalid.' }
    $packageName = "LanBridge-Desktop-$desktopVersion-$Runtime"
    $publishDirectory = Join-Path $projectRoot "release/$packageName"
    # Keep native libraries visible and replaceable (including libuiohook's LGPL component).
    Invoke-DesktopDotnet publish $desktopProject -c Release -r $Runtime --self-contained true '-p:PublishSingleFile=false' '-p:DebugType=None' -o $publishDirectory --nologo
    Copy-Item -LiteralPath LICENSE -Destination $publishDirectory
    Copy-Item -LiteralPath crossplatform/README.md -Destination (Join-Path $publishDirectory 'README.md')
    Copy-Item -LiteralPath crossplatform/THIRD-PARTY-NOTICES.md -Destination $publishDirectory
    Copy-Item -LiteralPath crossplatform/licenses -Destination $publishDirectory -Recurse -Force
    Copy-Item -LiteralPath licenses/DOTNET-LICENSE.txt,licenses/DOTNET-THIRD-PARTY-NOTICES.txt -Destination (Join-Path $publishDirectory 'licenses')
    $artifactDirectory = Join-Path $projectRoot 'release/desktop-artifacts'
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    $appHost = Join-Path $publishDirectory 'LanBridge.Desktop'
    if ($Runtime -like 'osx-*') {
        if (-not $IsMacOS) { throw 'Build macOS .app archives on macOS to preserve executable modes and ad-hoc signatures.' }
        $appDirectory = Join-Path $projectRoot "release/$packageName.app"
        $macosDirectory = Join-Path $appDirectory 'Contents/MacOS'
        New-Item -ItemType Directory -Path $macosDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $macosDirectory -Recurse -Force
        $appHost = Join-Path $macosDirectory 'LanBridge.Desktop'
        [System.IO.File]::SetUnixFileMode($appHost, [System.IO.UnixFileMode]493)
        Copy-Item -LiteralPath crossplatform/packaging/Info.plist -Destination (Join-Path $appDirectory 'Contents/Info.plist')
        & codesign --force --deep --sign - $appDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc .app signing failed.' }
        & codesign --verify --deep --strict $appDirectory
        if ($LASTEXITCODE -ne 0) { throw '.app signature verification failed.' }
        $artifact = Join-Path $artifactDirectory "$packageName.zip"
        & ditto -c -k --sequesterRsrc --keepParent $appDirectory $artifact
        if ($LASTEXITCODE -ne 0) { throw 'macOS archive creation failed.' }
    } elseif ($Runtime -like 'linux-*') {
        if (-not $IsLinux) { throw 'Build Linux archives on Linux to preserve executable permissions.' }
        [System.IO.File]::SetUnixFileMode($appHost, [System.IO.UnixFileMode]493)
        $artifact = Join-Path $artifactDirectory "$packageName.tar.gz"
        & tar -czf $artifact -C (Split-Path -Parent $publishDirectory) (Split-Path -Leaf $publishDirectory)
        if ($LASTEXITCODE -ne 0) { throw 'Linux archive creation failed.' }
    } else {
        $artifact = Join-Path $artifactDirectory "$packageName.zip"
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $artifact -CompressionLevel Optimal -Force
    }
    $digest = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$artifact.sha256", "$digest  $([System.IO.Path]::GetFileName($artifact))`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output "Published: $artifact"
} finally { Pop-Location }
