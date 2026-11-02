# The NekoNotch mark, shared by tools/Export-BrandIcon.ps1 and store-assets/Export-StoreArt.ps1.
# Source of truth: src/OrixNotch/Assets/Brand/mark.svg (the designer's file; the app's BrandMark reads it too).
# Drawn with WPF so the SVG path data is used as-is. Dot-source it:  . (Join-Path $repoRoot "tools/BrandMark.ps1")

Add-Type -AssemblyName PresentationCore, WindowsBase

$BrandEye = [System.Windows.Media.Color]::FromRgb(0xFF, 0xFF, 0xFF)
$BrandTile = [System.Windows.Media.Color]::FromRgb(0x0C, 0x0E, 0x14)

# The mark's outline (all paths), moved so its bounds start at 0,0. Even-odd fill keeps pupil holes and glints.
function Get-BrandMarkGeometry([string]$RepoRoot) {
    [xml]$svg = Get-Content -Raw (Join-Path $RepoRoot "src/OrixNotch/Assets/Brand/mark.svg")
    $group = New-Object System.Windows.Media.GeometryGroup
    $group.FillRule = [System.Windows.Media.FillRule]::EvenOdd
    foreach ($path in $svg.svg.path) {
        $geometry = [System.Windows.Media.Geometry]::Parse($path.d).Clone()
        if ($path.transform -match 'translate\(\s*([-\d.]+)[\s,]+([-\d.]+)\s*\)') {
            $geometry.Transform = New-Object System.Windows.Media.TranslateTransform(
                [double]::Parse($Matches[1], [cultureinfo]::InvariantCulture), [double]::Parse($Matches[2], [cultureinfo]::InvariantCulture))
        }
        $group.Children.Add($geometry)
    }
    $bounds = $group.Bounds
    $group.Transform = New-Object System.Windows.Media.TranslateTransform(-$bounds.X, -$bounds.Y)
    $group.Freeze()
    return $group
}

function New-BrandBrush {
    return [System.Windows.Media.Brushes]::White
}

# Square PNG of the mark: $MarkScale = share of the canvas the mark's longer side fills; -Tile puts it on
# the dark rounded square used for the app icon.
function Save-BrandMarkPng([string]$RepoRoot, [int]$Size, [string]$Path, [double]$MarkScale = 0.64, [switch]$Tile) {
    $mark = Get-BrandMarkGeometry $RepoRoot
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    try {
        if ($Tile) {
            $radius = $Size * 0.22
            $dc.DrawRoundedRectangle((New-Object System.Windows.Media.SolidColorBrush($BrandTile)), $null,
                (New-Object System.Windows.Rect(0, 0, $Size, $Size)), $radius, $radius)
        }
        $b = $mark.Bounds
        $scale = $Size * $MarkScale / [Math]::Max($b.Width, $b.Height)
        $x = ($Size - $b.Width * $scale) / 2
        $y = ($Size - $b.Height * $scale) / 2
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform($x, $y)))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $dc.DrawGeometry((New-BrandBrush), $null, $mark)
        $dc.Pop(); $dc.Pop()
    } finally { $dc.Close() }

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Close() }
}
