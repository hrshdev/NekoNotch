# The NekoNotch mark, shared by tools/Export-BrandIcon.ps1 and store-assets/Export-StoreArt.ps1.
# Source of truth: src/OrixNotch/Assets/Brand/mark.svg (the designer's file; the app's BrandMark reads it too).
# Drawn with WPF so vector rendering is pixel-perfect across all resolutions. Dot-source it: . (Join-Path $repoRoot "tools/BrandMark.ps1")

Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

$BrandEye = [System.Windows.Media.Color]::FromRgb(0xFF, 0xFF, 0xFF)
$BrandTile = [System.Windows.Media.Color]::FromRgb(0x0C, 0x0E, 0x14)

function Draw-MochiPetGraphic([System.Windows.Media.DrawingContext]$dc, [double]$Size, [double]$MarkScale = 0.72, [bool]$Tile = $false) {
    if ($Tile) {
        $tileRadius = $Size * 0.22
        $tileBrush = New-Object System.Windows.Media.SolidColorBrush($BrandTile)
        $tilePen = New-Object System.Windows.Media.Pen(
            (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(40, 255, 255, 255))),
            [Math]::Max(1.0, $Size * 0.015)
        )
        $dc.DrawRoundedRectangle($tileBrush, $tilePen, (New-Object System.Windows.Rect(0, 0, $Size, $Size)), $tileRadius, $tileRadius)
    }

    $cx = $Size / 2.0
    $cy = $Size / 2.0
    $targetWidth = $Size * $MarkScale
    $targetHeight = $targetWidth * (24.0 / 38.0)
    
    # Mochi Squircle Body (CornerRadius = 8 on 38x24 => 8/24 = 0.333)
    $radius = $targetHeight * (8.0 / 24.0)
    $x = $cx - $targetWidth / 2.0
    $y = $cy - $targetHeight / 2.0
    $rect = New-Object System.Windows.Rect($x, $y, $targetWidth, $targetHeight)

    $bodyBrush = New-Object System.Windows.Media.LinearGradientBrush
    $bodyBrush.StartPoint = New-Object System.Windows.Point(0.5, 0)
    $bodyBrush.EndPoint = New-Object System.Windows.Point(0.5, 1)
    $bodyBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#F4F4F6'), 0.0)))
    $bodyBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#EDEDEF'), 0.4)))
    $bodyBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#C5C6CB'), 1.0)))

    $strokePen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(140, 176, 178, 184))),
        [Math]::Max(0.75, $targetHeight * 0.03)
    )

    $dc.DrawRoundedRectangle($bodyBrush, $strokePen, $rect, $radius, $radius)

    # Specular gloss highlight
    $glossBrush = New-Object System.Windows.Media.RadialGradientBrush
    $glossBrush.Center = New-Object System.Windows.Point(0.5, 0.25)
    $glossBrush.GradientOrigin = New-Object System.Windows.Point(0.5, 0.25)
    $glossBrush.RadiusX = 0.5
    $glossBrush.RadiusY = 0.5
    $glossBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(150, 255, 255, 255), 0.0)))
    $glossBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0, 255, 255, 255), 1.0)))
    $glossCenter = New-Object System.Windows.Point($cx, ($y + $targetHeight * 0.18))
    $dc.DrawEllipse($glossBrush, $null, $glossCenter, ($targetWidth * 0.35), ($targetHeight * 0.22))

    # Rosy Blush Cheeks (reduced compact width, delicate Coucou pink #FF7896)
    $blushBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(115, 255, 120, 150))
    $blushRx = $targetWidth * (1.6 / 38.0)
    $blushRy = $targetHeight * (1.2 / 24.0)
    $blushY = $y + $targetHeight * 0.58
    $leftBlushPt = New-Object System.Windows.Point(($x + $targetWidth * 0.14), $blushY)
    $rightBlushPt = New-Object System.Windows.Point(($x + $targetWidth * 0.86), $blushY)
    $dc.DrawEllipse($blushBrush, $null, $leftBlushPt, $blushRx, $blushRy)
    $dc.DrawEllipse($blushBrush, $null, $rightBlushPt, $blushRx, $blushRy)

    # Espresso Ink Eye Pills (#1A1412)
    $eyeBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.ColorConverter]::ConvertFromString('#1A1412'))
    $eyeW = $targetWidth * (4.8 / 38.0)
    $eyeH = $targetHeight * (8.0 / 24.0)
    $eyeRadius = $eyeW / 2.0
    $eyeY = $y + ($targetHeight - $eyeH) / 2.0 + ($targetHeight * 0.01)

    $leftEyeX = $cx - $targetWidth * 0.18 - $eyeW / 2.0
    $dc.DrawRoundedRectangle($eyeBrush, $null, (New-Object System.Windows.Rect($leftEyeX, $eyeY, $eyeW, $eyeH)), $eyeRadius, $eyeRadius)

    $rightEyeX = $cx + $targetWidth * 0.18 - $eyeW / 2.0
    $dc.DrawRoundedRectangle($eyeBrush, $null, (New-Object System.Windows.Rect($rightEyeX, $eyeY, $eyeW, $eyeH)), $eyeRadius, $eyeRadius)

    # Specular Glint Dots
    $glintBrush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(242, 255, 255, 255))
    $glintR = [Math]::Max(0.75, $eyeW * 0.20)
    $leftGlintPt = New-Object System.Windows.Point(($leftEyeX + $eyeW * 0.38), ($eyeY + $eyeH * 0.26))
    $rightGlintPt = New-Object System.Windows.Point(($rightEyeX + $eyeW * 0.38), ($eyeY + $eyeH * 0.26))
    $dc.DrawEllipse($glintBrush, $null, $leftGlintPt, $glintR, $glintR)
    $dc.DrawEllipse($glintBrush, $null, $rightGlintPt, $glintR, $glintR)
}

function Get-BrandMarkGeometry([string]$RepoRoot) {
    [xml]$svg = Get-Content -Raw (Join-Path $RepoRoot "src/OrixNotch/Assets/Brand/mark.svg")
    $group = New-Object System.Windows.Media.GeometryGroup
    $group.FillRule = [System.Windows.Media.FillRule]::EvenOdd
    if ($svg.svg.path) {
        foreach ($path in $svg.svg.path) {
            $geometry = [System.Windows.Media.Geometry]::Parse($path.d).Clone()
            if ($path.transform -match 'translate\(\s*([-\d.]+)[\s,]+([-\d.]+)\s*\)') {
                $geometry.Transform = New-Object System.Windows.Media.TranslateTransform(
                    [double]::Parse($Matches[1], [cultureinfo]::InvariantCulture), [double]::Parse($Matches[2], [cultureinfo]::InvariantCulture))
            }
            $group.Children.Add($geometry)
        }
    }
    $bounds = $group.Bounds
    if (-not $bounds.IsEmpty) {
        $group.Transform = New-Object System.Windows.Media.TranslateTransform(-$bounds.X, -$bounds.Y)
    }
    $group.Freeze()
    return $group
}

function New-BrandBrush {
    return [System.Windows.Media.Brushes]::White
}

function Save-BrandMarkPng([string]$RepoRoot, [int]$Size, [string]$Path, [double]$MarkScale = 0.72, [switch]$Tile) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    try {
        Draw-MochiPetGraphic $dc $Size $MarkScale ($Tile.IsPresent)
    } finally {
        $dc.Close()
    }

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Close() }
}
