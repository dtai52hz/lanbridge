param([ValidateSet('Build', 'Test', 'Publish')][string]$Task = 'Build')
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
        foreach ($testProject in @('TransportSmoke', 'DiscoverySmoke', 'DevicesPageSmoke', 'FileSmoke', 'NativeInputSmoke', 'TextInputSmoke', 'MediaSmoke', 'SessionSmoke')) {
            Invoke-ProjectDotnet run --project "tests\$testProject\$testProject.csproj" -c Release
        }
    } elseif ($Task -eq 'Publish') {
        $publishDirectory = 'release\LanBridge-0.3.0-win-x64'
        Invoke-ProjectDotnet publish src\LanBridge.csproj -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' -o $publishDirectory --nologo
        Copy-Item -LiteralPath README.md,TESTING.md,CHANGELOG.md,NAudio-LICENSE.txt,THIRD-PARTY-NOTICES.md -Destination $publishDirectory
        if (Test-Path -LiteralPath licenses) { Copy-Item -LiteralPath licenses -Destination $publishDirectory -Recurse -Force }
    } else {
        Invoke-ProjectDotnet build src\LanBridge.csproj -c Release --nologo
    }
} finally { Pop-Location }
