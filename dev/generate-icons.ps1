<#
.SYNOPSIS
    Generates multi-resolution app and tray icons from the SVG raven logo.

.DESCRIPTION
    Renders the SVG path data using WPF geometry at standard icon sizes,
    packages the PNG frames into proper ICO files.

    Output:
      assets/icons/sir-thaddeus.ico       (app icon — blue raven)
      assets/icons/sir-thaddeus-tray.ico   (tray icon — white raven for contrast)

    Run from repo root:
      .\dev\generate-icons.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$repoRoot = Split-Path -Parent $PSScriptRoot
$svgPath = Join-Path $repoRoot "assets\svg\sir-thaddeus.svg"
$outDir = Join-Path $repoRoot "assets\icons"

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# ── Standard ICO sizes ────────────────────────────────────────────────
$appSizes = @(16, 24, 32, 48, 64, 128, 256)
$traySizes = @(16, 24, 32, 48)

# ── Brand colors ──────────────────────────────────────────────────────
$navyBlue = [System.Windows.Media.Color]::FromRgb(0x1B, 0x3F, 0x6E)
$white = [System.Windows.Media.Color]::FromRgb(0xFF, 0xFF, 0xFF)

# ── Extract path data from SVG ────────────────────────────────────────
function Extract-SvgPathData {
    param([string]$SvgFilePath)

    $raw = Get-Content -Path $SvgFilePath -Raw
    # Grab the d="..." attribute from the first <path> element.
    if ($raw -match '(?s)<path[^>]+\sd="([^"]+)"') {
        return $Matches[1]
    }
    throw "No <path d='...'> found in SVG."
}

Write-Host "Parsing SVG path from: $svgPath"
$pathData = Extract-SvgPathData -SvgFilePath $svgPath
$geometry = [System.Windows.Media.Geometry]::Parse($pathData)
$bounds = $geometry.Bounds

Write-Host ("  Geometry bounds: X={0:F1} Y={1:F1} W={2:F1} H={3:F1}" -f `
        $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height)

# ── Render a single frame ────────────────────────────────────────────
function Render-IconPng {
    param(
        [int]$Size,
        [System.Windows.Media.Color]$FillColor,
        [double]$PaddingFraction = 0.08
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    # Scale geometry to fit with padding
    $available = $Size * (1.0 - 2.0 * $PaddingFraction)
    $scaleX = $available / $bounds.Width
    $scaleY = $available / $bounds.Height
    $scale = [Math]::Min($scaleX, $scaleY)

    $scaledW = $bounds.Width * $scale
    $scaledH = $bounds.Height * $scale

    # Transform: origin-shift → scale → center
    $group = New-Object System.Windows.Media.TransformGroup
    $group.Children.Add((New-Object System.Windows.Media.TranslateTransform(
                (-$bounds.X), (-$bounds.Y))))
    $group.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    $group.Children.Add((New-Object System.Windows.Media.TranslateTransform(
                (($Size - $scaledW) / 2.0), (($Size - $scaledH) / 2.0))))

    $dc.PushTransform($group)

    $brush = New-Object System.Windows.Media.SolidColorBrush($FillColor)
    $dc.DrawGeometry($brush, $null, $geometry)

    $dc.Pop()
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return $ms.ToArray()
}

# ── Write multi-resolution ICO ────────────────────────────────────────
function Write-IcoFile {
    param(
        [string]$OutputPath,
        [byte[][]]$PngDataArray,
        [int[]]$Sizes
    )

    $count = $PngDataArray.Count
    $headerSize = 6 + ($count * 16)

    $fs = [System.IO.File]::Create($OutputPath)
    $bw = New-Object System.IO.BinaryWriter($fs)

    # ICONDIR header
    $bw.Write([UInt16]0)       # reserved
    $bw.Write([UInt16]1)       # type = icon
    $bw.Write([UInt16]$count)  # image count

    $offset = $headerSize
    for ($i = 0; $i -lt $count; $i++) {
        $sz = $Sizes[$i]
        $wh = if ($sz -ge 256) { 0 } else { [byte]$sz }

        $bw.Write([byte]$wh)   # width  (0 = 256+)
        $bw.Write([byte]$wh)   # height (0 = 256+)
        $bw.Write([byte]0)     # color count
        $bw.Write([byte]0)     # reserved
        $bw.Write([UInt16]1)   # color planes
        $bw.Write([UInt16]32)  # bits per pixel
        $bw.Write([UInt32]$PngDataArray[$i].Length)
        $bw.Write([UInt32]$offset)
        $offset += $PngDataArray[$i].Length
    }

    for ($i = 0; $i -lt $count; $i++) {
        $bw.Write($PngDataArray[$i])
    }

    $bw.Close()
    $fs.Close()
}

# ── Generate app icon (navy blue) ────────────────────────────────────
Write-Host ""
Write-Host "Generating app icon (navy blue raven)..."
$appFrames = @()
foreach ($sz in $appSizes) {
    Write-Host "  ${sz}x${sz}"
    $appFrames += , (Render-IconPng -Size $sz -FillColor $navyBlue)
}
$appIcoPath = Join-Path $outDir "sir-thaddeus.ico"
Write-IcoFile -OutputPath $appIcoPath -PngDataArray $appFrames -Sizes $appSizes
Write-Host "  -> $appIcoPath"

# ── Generate tray icon (white for system tray contrast) ──────────────
Write-Host ""
Write-Host "Generating tray icon (white raven for tray contrast)..."
$trayFrames = @()
foreach ($sz in $traySizes) {
    Write-Host "  ${sz}x${sz}"
    $trayFrames += , (Render-IconPng -Size $sz -FillColor $white)
}
$trayIcoPath = Join-Path $outDir "sir-thaddeus-tray.ico"
Write-IcoFile -OutputPath $trayIcoPath -PngDataArray $trayFrames -Sizes $traySizes
Write-Host "  -> $trayIcoPath"

# ── Generate tray icon (dark for light mode system tray) ─────────────
Write-Host ""
Write-Host "Generating tray icon (navy blue raven for light mode tray contrast)..."
$trayDarkFrames = @()
foreach ($sz in $traySizes) {
    Write-Host "  ${sz}x${sz}"
    $trayDarkFrames += , (Render-IconPng -Size $sz -FillColor $navyBlue)
}
$trayDarkIcoPath = Join-Path $outDir "sir-thaddeus-tray-dark.ico"
Write-IcoFile -OutputPath $trayDarkIcoPath -PngDataArray $trayDarkFrames -Sizes $traySizes
Write-Host "  -> $trayDarkIcoPath"

Write-Host ""
Write-Host "Done."
Write-Host "  App icon:  $appIcoPath"
Write-Host "  Tray icon: $trayIcoPath"
Write-Host "  Tray dark: $trayDarkIcoPath"
