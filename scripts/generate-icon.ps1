param(
    [string]$OutputPath,
    [string]$PreviewPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Resolve-RepositoryOutput([string]$Candidate, [string]$DefaultRelativePath) {
    if ([string]::IsNullOrWhiteSpace($Candidate)) { $Candidate = Join-Path $repositoryRoot $DefaultRelativePath }
    if (-not [System.IO.Path]::IsPathRooted($Candidate)) { $Candidate = Join-Path $repositoryRoot $Candidate }
    $resolved = [System.IO.Path]::GetFullPath($Candidate)
    $repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Icon output must stay inside the LanBridge repository.'
    }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolved)) | Out-Null
    return $resolved
}

function New-RoundedRectangle([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    [single]$diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function New-IconBitmap([int]$Size) {
    # Render the same vector geometry at 4x resolution for smooth, consistent small sizes.
    $working = [System.Drawing.Bitmap]::new($Size * 4, $Size * 4, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($working)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#10141D'))
    $tealFill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#173F40'))
    $blueFill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#17304C'))
    $teal = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#2FD0B1'), 15)
    $blue = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#429FFF'), 15)
    $link = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#E7FAF6'), 12)
    foreach ($pen in @($teal, $blue, $link)) {
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    }
    $tile = New-RoundedRectangle 8 8 240 240 49
    $first = New-RoundedRectangle 36 49 111 80 12
    $second = New-RoundedRectangle 109 119 111 80 12
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($Size * 4 / 256.0, $Size * 4 / 256.0)
        $graphics.FillPath($background, $tile)
        $graphics.DrawLine($teal, [single]91.5, [single]129, [single]91.5, [single]151)
        $graphics.DrawLine($teal, [single]72, [single]151, [single]109, [single]151)
        $graphics.FillPath($tealFill, $first)
        $graphics.DrawPath($teal, $first)
        $graphics.DrawLine($blue, [single]164.5, [single]199, [single]164.5, [single]221)
        $graphics.DrawLine($blue, [single]145, [single]221, [single]184, [single]221)
        $graphics.FillPath($blueFill, $second)
        $graphics.DrawPath($blue, $second)
        # A short white bridge joins the screen interiors without relying on a letter or font.
        $graphics.DrawLine($link, [single]119, [single]101, [single]137, [single]101)
        $graphics.DrawLine($link, [single]137, [single]101, [single]137, [single]147)
        $graphics.DrawLine($link, [single]137, [single]147, [single]155, [single]147)
        $result = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $resampler = [System.Drawing.Graphics]::FromImage($result)
        try {
            $resampler.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $resampler.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $resampler.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $resampler.DrawImage($working, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $working.Width, $working.Height, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally { $resampler.Dispose() }
        return ,$result
    }
    finally {
        foreach ($resource in @($graphics, $tile, $first, $second, $background, $tealFill, $blueFill, $teal, $blue, $link, $working)) { $resource.Dispose() }
    }
}

$resolvedOutput = Resolve-RepositoryOutput $OutputPath 'src/assets/lanbridge.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($stream.ToArray())
    }
    finally { $stream.Dispose(); $bitmap.Dispose() }
}
$file = [System.IO.File]::Create($resolvedOutput)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    # ICONDIR followed by seven ICONDIRENTRY records and their self-contained PNG payloads.
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    [uint32]$offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        [byte]$dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write($offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
}
finally { $writer.Dispose() }

if (-not [string]::IsNullOrWhiteSpace($PreviewPath)) {
    $resolvedPreview = Resolve-RepositoryOutput $PreviewPath '.tools/icon-preview.png'
    $preview = New-IconBitmap 256
    try { $preview.Save($resolvedPreview, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $preview.Dispose() }
}
Write-Output "Generated $resolvedOutput ($($sizes -join ', ') px)."
