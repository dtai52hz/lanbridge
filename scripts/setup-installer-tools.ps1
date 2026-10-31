param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$toolVersion = '6.7.4'
$toolDirectory = Join-Path $repositoryRoot ".tools\inno-$toolVersion"
$toolCompiler = Join-Path $toolDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $toolCompiler) {
    # Official compiler binaries may report PE version 0.0.0.0. The .iss script
    # checks the compiler engine's real Ver constant before accepting a build.
    Write-Output $toolCompiler
    return
}
$downloadDirectory = Join-Path $repositoryRoot '.tools\installer-downloads'
[System.IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
$downloadPath = Join-Path $downloadDirectory "innosetup-$toolVersion.exe"
$downloadUrl = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
$expectedDigest = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
if (-not (Test-Path -LiteralPath $downloadPath)) {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath
}
if ((Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedDigest) {
    throw 'Inno Setup download does not match the pinned official SHA-256.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $downloadPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'Inno Setup does not have the expected valid publisher signature.'
}
# The official installer supports /PORTABLE=1: no uninstall registration,
# file associations or shortcuts. Keep the compiler inside the ignored tool folder.
$toolLog = Join-Path $downloadDirectory 'inno-portable-install.log'
$toolArguments = @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
    '/NOICONS', '/TASKS=""', "/DIR=`"$toolDirectory`"", "/LOG=`"$toolLog`"")
$toolProcess = Start-Process -FilePath $downloadPath -ArgumentList $toolArguments -WindowStyle Hidden -PassThru -Wait
if ($toolProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $toolCompiler)) {
    throw "Portable Inno Setup preparation failed (exit $($toolProcess.ExitCode)); see $toolLog."
}
Write-Output $toolCompiler
