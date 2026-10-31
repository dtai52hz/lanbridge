#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$PayloadPath,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$toolsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot '.tools'))
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $projectRoot 'src\LanBridge.csproj') -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must be a three-part release number.' }
if (-not $InstallerPath) { $InstallerPath = Join-Path $projectRoot "release\LanBridge-$version-Setup-x64.exe" }
if (-not $PayloadPath) { $PayloadPath = Join-Path $projectRoot "release\LanBridge-$version-win-x64" }
if (-not $OutputRoot) { $OutputRoot = Join-Path $toolsRoot "installer-smoke-$version-$([Guid]::NewGuid().ToString('N'))" }
$InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
$PayloadPath = [System.IO.Path]::GetFullPath($PayloadPath)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

function Assert-IsolatedPath([string]$Path) {
    $absolute = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($toolsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke output must be a child of the project's .tools directory: $absolute"
    }
    # A lexical prefix is insufficient when an ancestor is a junction or symlink.
    $ancestor = $absolute
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Smoke output cannot traverse a junction or symbolic link: $ancestor"
            }
        }
        $parent = [System.IO.Path]::GetDirectoryName($ancestor)
        if ($parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    return $absolute
}

function Get-FileFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '<absent>' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-RegistryKeySnapshot([Microsoft.Win32.RegistryHive]$Hive, [Microsoft.Win32.RegistryView]$View, [string]$KeyPath) {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    try {
        $key = $baseKey.OpenSubKey($KeyPath, $false)
        if ($null -eq $key) { return '<absent>' }
        try {
            $values = [ordered]@{}
            foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                $values[$name] = [ordered]@{
                    Kind = [string]$key.GetValueKind($name)
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
            return ConvertTo-Json -InputObject $values -Depth 10 -Compress
        } finally { $key.Dispose() }
    } finally { $baseKey.Dispose() }
}

function Get-UninstallSnapshot {
    $keyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{82183248-7B34-4DC8-95F3-D86B1DD26D70}_is1'
    $snapshot = [ordered]@{}
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
            $snapshot["$hive/$view"] = Get-RegistryKeySnapshot $hive $view $keyPath
        }
    }
    return ConvertTo-Json -InputObject $snapshot -Depth 10 -Compress
}

function Get-ExistingAppProcesses {
    return @(Get-Process -Name LanBridge -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{ Id = $_.Id; StartTicks = $_.StartTime.ToUniversalTime().Ticks }
    })
}

function Assert-ExistingAppsUnchanged($OriginalProcesses) {
    foreach ($original in $OriginalProcesses) {
        $current = Get-Process -Id $original.Id -ErrorAction SilentlyContinue
        if ($null -eq $current -or $current.StartTime.ToUniversalTime().Ticks -ne $original.StartTicks) {
            throw "An existing LanBridge process changed during the smoke test (PID $($original.Id))."
        }
    }
}

function Invoke-SmokeProcess([string]$Executable, [string[]]$Arguments, [int]$TimeoutMilliseconds = 120000) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not launch smoke process: $Executable" }
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            # Only this script's own child may be stopped, never a pre-existing app.
            $process.Kill($true)
            $process.WaitForExit()
            throw "Smoke process timed out: $Executable"
        }
        if ($process.ExitCode -ne 0) { throw "Smoke process failed with exit code $($process.ExitCode): $Executable" }
    } finally { $process.Dispose() }
}

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Installer not found: $InstallerPath" }
if (-not (Test-Path -LiteralPath $PayloadPath -PathType Container)) { throw "Published payload not found: $PayloadPath" }
$null = Assert-IsolatedPath $OutputRoot
if (Test-Path -LiteralPath $OutputRoot) { throw "Choose a fresh smoke directory; this path already exists: $OutputRoot" }
$installDirectory = Assert-IsolatedPath (Join-Path $OutputRoot 'installed')
$installLog = Assert-IsolatedPath (Join-Path $OutputRoot 'install.log')
$uninstallLog = Assert-IsolatedPath (Join-Path $OutputRoot 'uninstall.log')
$previewPath = Assert-IsolatedPath (Join-Path $OutputRoot 'installed-preview.png')
$fixturePath = Assert-IsolatedPath (Join-Path $OutputRoot 'external-user-data\keep.txt')
$reportPath = Assert-IsolatedPath (Join-Path $OutputRoot 'result.json')

$payloadFiles = @(Get-ChildItem -LiteralPath $PayloadPath -File -Recurse)
if ($payloadFiles.Count -eq 0) { throw 'Published payload is empty.' }
$requiredPayload = @('LanBridge.exe', 'README.md', 'TESTING.md', 'CHANGELOG.md', 'LICENSE', 'NAudio-LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'licenses\DOTNET-LICENSE.txt', 'licenses\DOTNET-THIRD-PARTY-NOTICES.txt', 'licenses\WINDOWS-DESKTOP-LICENSE.txt')
foreach ($relative in $requiredPayload) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadPath $relative) -PathType Leaf)) { throw "Required package file is missing: $relative" }
}

$uninstallBefore = Get-UninstallSnapshot
$existingApps = @(Get-ExistingAppProcesses)
$preferencesPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'LanBridge\preferences.json'
$preferencesBefore = Get-FileFingerprint $preferencesPath
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'LanBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'LanBridge\LanBridge.lnk'),
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'LanBridge\Uninstall LanBridge.lnk')
)
$shortcutsBefore = @{}
foreach ($path in $shortcutPaths) { $shortcutsBefore[$path] = Get-FileFingerprint $path }
$null = [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fixturePath))
[System.IO.File]::WriteAllText($fixturePath, 'LanBridge packaging smoke: this file is outside the installation directory.', [System.Text.UTF8Encoding]::new($false))
$fixtureBefore = Get-FileFingerprint $fixturePath
$setupFinished = $false
$uninstallFinished = $false
$matchedPayloadFiles = 0
$failure = $null

try {
    $null = Assert-IsolatedPath $installDirectory
    Invoke-SmokeProcess $InstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/SP-', '/NOICONS', '/TASKS=', "/DIR=$installDirectory", '/PACKAGETEST=1', "/LOG=$installLog")
    $setupFinished = $true
    foreach ($source in $payloadFiles) {
        $relative = [System.IO.Path]::GetRelativePath($PayloadPath, $source.FullName)
        $installedFile = Assert-IsolatedPath (Join-Path $installDirectory $relative)
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { throw "Installed payload file is missing: $relative" }
        if ((Get-FileFingerprint $source.FullName) -ne (Get-FileFingerprint $installedFile)) { throw "Installed payload hash differs: $relative" }
        $matchedPayloadFiles++
    }
    if ((Get-UninstallSnapshot) -cne $uninstallBefore) { throw 'Package-test installation changed an uninstall registry entry.' }
    foreach ($path in $shortcutPaths) {
        if ((Get-FileFingerprint $path) -ne $shortcutsBefore[$path]) { throw 'Package-test installation changed a user shortcut.' }
    }
    Assert-ExistingAppsUnchanged $existingApps
    $installedExecutable = Assert-IsolatedPath (Join-Path $installDirectory 'LanBridge.exe')
    # Preview creates no hooks, network sessions, tray icon, preferences or app-mutex ownership.
    Invoke-SmokeProcess $installedExecutable @('--preview', '--preview-output', $previewPath) 30000
    if (-not (Test-Path -LiteralPath $previewPath -PathType Leaf)) { throw 'Installed app preview did not produce its image.' }
    $previewBytes = [System.IO.File]::ReadAllBytes($previewPath)
    if ($previewBytes.Length -lt 24 -or [Convert]::ToHexString([byte[]]$previewBytes[0..7]) -ne '89504E470D0A1A0A') { throw 'Installed app preview is not a PNG image.' }
    if ((Get-FileFingerprint $preferencesPath) -ne $preferencesBefore) { throw 'Preview changed the real user preferences.' }
} catch {
    $failure = $_
} finally {
    # Leave logs and images in .tools; remove only the copied installation through its own uninstaller.
    $uninstaller = Assert-IsolatedPath (Join-Path $installDirectory 'unins000.exe')
    if ($setupFinished -or (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        try {
            $null = Assert-IsolatedPath $installDirectory
            if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Installed package has no uninstaller.' }
            Invoke-SmokeProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/PACKAGETEST=1', "/LOG=$uninstallLog")
            # Inno's relocated uninstaller may finish final directory cleanup shortly after its launcher exits.
            $cleanupDeadline = [System.Diagnostics.Stopwatch]::StartNew()
            do {
                $remainingPayload = @($payloadFiles | Where-Object {
                    Test-Path -LiteralPath (Join-Path $installDirectory ([System.IO.Path]::GetRelativePath($PayloadPath, $_.FullName)))
                })
                if ($remainingPayload.Count -eq 0) { break }
                Start-Sleep -Milliseconds 100
            } while ($cleanupDeadline.Elapsed.TotalSeconds -lt 10)
            if ($remainingPayload.Count -ne 0) { throw "Uninstaller left $($remainingPayload.Count) copied payload files." }
            $uninstallFinished = $true
        } catch { if ($null -eq $failure) { $failure = $_ } }
    }
    try {
        if ((Get-FileFingerprint $fixturePath) -ne $fixtureBefore) { throw 'Uninstall changed the external user-data fixture.' }
        if ((Get-UninstallSnapshot) -cne $uninstallBefore) { throw 'Smoke test changed an uninstall registry entry.' }
        if ((Get-FileFingerprint $preferencesPath) -ne $preferencesBefore) { throw 'Smoke test changed real user preferences.' }
        foreach ($path in $shortcutPaths) {
            if ((Get-FileFingerprint $path) -ne $shortcutsBefore[$path]) { throw 'Smoke test changed a user shortcut.' }
        }
        Assert-ExistingAppsUnchanged $existingApps
    } catch { if ($null -eq $failure) { $failure = $_ } }
    $report = [ordered]@{
        Passed = ($null -eq $failure -and $setupFinished -and $uninstallFinished)
        Version = $version
        Installer = [System.IO.Path]::GetFileName($InstallerPath)
        PayloadFilesVerified = $matchedPayloadFiles
        ExistingAppProcessesPreserved = $existingApps.Count
        Installed = $setupFinished
        Uninstalled = $uninstallFinished
        Preview = $previewPath
        InstallLog = $installLog
        UninstallLog = $uninstallLog
        Error = if ($null -eq $failure) { $null } else { $failure.Exception.Message }
    }
    [System.IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $report -Depth 5), [System.Text.UTF8Encoding]::new($false))
}

if ($null -ne $failure) { throw $failure }
Write-Host "PASS: installer copied $matchedPayloadFiles matching payload files; installed preview and isolated uninstall succeeded."
Write-Host "Logs and preview: $OutputRoot"
