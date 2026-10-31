param([string]$CompilerPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $projectRoot 'src\LanBridge.csproj') -Raw
$releaseVersion = [string]$projectXml.Project.PropertyGroup.Version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Project Version must be a three-part release number.'
}

$publishDirectory = Join-Path $projectRoot "release\LanBridge-$releaseVersion-win-x64"
$publishedExecutable = Join-Path $publishDirectory 'LanBridge.exe'
foreach ($requiredFile in @('LanBridge.exe', 'README.md', 'TESTING.md', 'CHANGELOG.md', 'LICENSE', 'NAudio-LICENSE.txt', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "Missing published file '$requiredFile'. Run scripts/build.ps1 -Task Publish first."
    }
}
$publishedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable).FileVersion
if ($publishedVersion -ne "$releaseVersion.0") {
    throw "Published executable version '$publishedVersion' does not match project version '$releaseVersion'. Publish again before creating the installer."
}

if (-not $CompilerPath) {
    $localCompiler = Join-Path $projectRoot '.tools\inno-6.7.4\ISCC.exe'
    $installedCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $localCompiler -PathType Leaf) {
        $CompilerPath = $localCompiler
    } elseif (Test-Path -LiteralPath $installedCompiler -PathType Leaf) {
        $CompilerPath = $installedCompiler
    } else {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compilerCommand) { $CompilerPath = $compilerCommand.Source }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Run scripts/setup-installer-tools.ps1 or pass -CompilerPath pointing to ISCC.exe (Inno Setup 6.7.4 or newer).'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
# Inno's official command-line compiler can have a zero PE FileVersion.
# LanBridge.iss checks the real engine version through ISPP's Ver constant.

$packagePath = Join-Path $projectRoot "release\LanBridge-$releaseVersion-Windows-x64.zip"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw 'The portable ZIP is missing. Run scripts/build.ps1 -Task Publish first.'
}

$installerPath = Join-Path $projectRoot "release\LanBridge-$releaseVersion-Setup-x64.exe"
$compilerArguments = @(
    '/Qp'
    "/DAppVersion=$releaseVersion"
    "/DPublishDir=$publishDirectory"
    "/DProjectRoot=$projectRoot"
    (Join-Path $projectRoot 'installer\LanBridge.iss')
)
& $CompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed (exit $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw 'Inno Setup reported success but the expected installer was not created.'
}

$checksumLines = foreach ($artifactPath in @($packagePath, $installerPath)) {
    $artifactDigest = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$artifactDigest  $([System.IO.Path]::GetFileName($artifactPath))"
}
$checksumPath = Join-Path $projectRoot "release\LanBridge-$releaseVersion-SHA256SUMS.txt"
[System.IO.File]::WriteAllText($checksumPath, ($checksumLines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Installer: $installerPath"
Write-Output "SHA256: $checksumPath"
