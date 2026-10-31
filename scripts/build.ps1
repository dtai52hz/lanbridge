param([ValidateSet('Build', 'Test', 'Publish', 'Installer')][string]$Task = 'Build')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-ProjectDotnet {
    & $dotnetCommand @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE)." }
}
Push-Location $projectRoot
try {
    if ($Task -eq 'Test') {
        foreach ($testProject in @('TransportSmoke', 'DiscoverySmoke', 'DevicesPageSmoke', 'PreferencesSmoke', 'DesktopSmoke', 'FileSmoke', 'ClipboardFilesSmoke', 'ClipboardSessionSmoke', 'ClipboardImagesSmoke', 'ClipboardImageCodecSmoke', 'ClipboardImageSessionSmoke', 'NativeInputSmoke', 'TextInputSmoke', 'MediaSmoke', 'SessionSmoke')) {
            Invoke-ProjectDotnet run --project "tests\$testProject\$testProject.csproj" -c Release
        }
    } elseif ($Task -eq 'Installer') {
        & (Join-Path $PSScriptRoot 'build.ps1') -Task Publish
        & (Join-Path $PSScriptRoot 'build-installer.ps1')
    } elseif ($Task -eq 'Publish') {
        [xml]$projectXml = Get-Content -LiteralPath src\LanBridge.csproj -Raw
        $releaseVersion = [string]$projectXml.Project.PropertyGroup.Version
        if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must be a three-part release number.' }
        $publishDirectory = "release\LanBridge-$releaseVersion-win-x64"
        Invoke-ProjectDotnet publish src\LanBridge.csproj -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' -o $publishDirectory --nologo
        Copy-Item -LiteralPath LICENSE,README.md,TESTING.md,CHANGELOG.md,NAudio-LICENSE.txt,THIRD-PARTY-NOTICES.md -Destination $publishDirectory
        if (Test-Path -LiteralPath licenses) { Copy-Item -LiteralPath licenses -Destination $publishDirectory -Recurse -Force }
        if (Test-Path -LiteralPath docs) { Copy-Item -LiteralPath docs -Destination $publishDirectory -Recurse -Force }
        $packagePath = Join-Path $projectRoot "release\LanBridge-$releaseVersion-Windows-x64.zip"
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal -Force
        $packageDigest = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumPath = Join-Path $projectRoot "release\LanBridge-$releaseVersion-SHA256SUMS.txt"
        [System.IO.File]::WriteAllText($checksumPath, "$packageDigest  $([System.IO.Path]::GetFileName($packagePath))`n", [System.Text.UTF8Encoding]::new($false))
    } else {
        Invoke-ProjectDotnet build src\LanBridge.csproj -c Release --nologo
    }
} finally { Pop-Location }
