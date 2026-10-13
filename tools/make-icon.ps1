# Regenerates src/Avion2.Overlay/Assets/avion2.ico:
#   powershell -STA -File tools/make-icon.ps1 -OutIco src/Avion2.Overlay/Assets/avion2.ico -PreviewPng icon.png
param([string]$OutIco, [string]$PreviewPng)
# Renders the AVION2 icon with WPF at each size and packs a PNG-compressed .ico.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function New-Color([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }

function Render-Icon([int]$size) {
    $s = [double]$size
    $grid = New-Object System.Windows.Controls.Grid
    $grid.Width = $s; $grid.Height = $s

    # Night-blue disc with an aether glow in the middle
    $disc = New-Object System.Windows.Shapes.Ellipse
    $bg = New-Object System.Windows.Media.RadialGradientBrush
    $bg.GradientOrigin = New-Object System.Windows.Point 0.5, 0.42
    $bg.Center = New-Object System.Windows.Point 0.5, 0.45
    $bg.RadiusX = 0.62; $bg.RadiusY = 0.62
    $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF3E9AD0'), 0.0))
    $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF15476B'), 0.38))
    $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF0B1727'), 0.75))
    $bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF070D18'), 1.0))
    $disc.Fill = $bg
    $disc.Margin = New-Object System.Windows.Thickness ($s * 0.02)
    [void]$grid.Children.Add($disc)

    # Aether ring
    $ring = New-Object System.Windows.Shapes.Ellipse
    $rb = New-Object System.Windows.Media.LinearGradientBrush
    $rb.StartPoint = New-Object System.Windows.Point 0, 0
    $rb.EndPoint = New-Object System.Windows.Point 1, 1
    $rb.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FFE6F6FF'), 0.0))
    $rb.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF6FC2FF'), 0.5))
    $rb.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF3E5FD8'), 1.0))
    $ring.Stroke = $rb
    $ring.StrokeThickness = [Math]::Max(1.0, $s * 0.045)
    $ring.Margin = New-Object System.Windows.Thickness ($s * 0.045)
    [void]$grid.Children.Add($ring)

    # Four-pointed star with a soft glow (the wordmark's ✦)
    $star = New-Object System.Windows.Shapes.Path
    $star.Data = [System.Windows.Media.Geometry]::Parse('M 50,4 C 53,36 64,47 96,50 C 64,53 53,64 50,96 C 47,64 36,53 4,50 C 36,47 47,36 50,4 Z')
    $star.Stretch = 'Uniform'
    $starFill = New-Object System.Windows.Media.RadialGradientBrush
    $starFill.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FFFFFFFF'), 0.0))
    $starFill.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FFDDF4FF'), 0.45))
    $starFill.GradientStops.Add((New-Object System.Windows.Media.GradientStop (New-Color '#FF8ED6FF'), 1.0))
    $star.Fill = $starFill
    $star.Margin = New-Object System.Windows.Thickness ($s * 0.17)
    if ($size -ge 32) {
        $glow = New-Object System.Windows.Media.Effects.DropShadowEffect
        $glow.Color = New-Color '#FF7FD8FF'; $glow.ShadowDepth = 0; $glow.BlurRadius = $s * 0.12; $glow.Opacity = 0.95
        $star.Effect = $glow
    }
    [void]$grid.Children.Add($star)

    $grid.Measure((New-Object System.Windows.Size $s, $s))
    $grid.Arrange((New-Object System.Windows.Rect 0, 0, $s, $s))
    $grid.UpdateLayout()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($grid)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Render-Icon $size }
[System.IO.File]::WriteAllBytes($PreviewPng, $images[256])

# ICO container: header, directory, PNG payloads
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$size].Length); $w.Write([UInt32]$offset)
    $offset += $images[$size].Length
}
foreach ($size in $sizes) { $w.Write($images[$size]) }
$w.Flush()
New-Item -ItemType Directory -Force (Split-Path $OutIco) | Out-Null
[System.IO.File]::WriteAllBytes($OutIco, $ms.ToArray())
"wrote $OutIco ($($ms.Length) bytes, sizes $($sizes -join ','))"
