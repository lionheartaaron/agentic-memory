# Regenerates the icon assets beside this script.
#
# The output is checked in, so a release never depends on this running — it needs GDI+ and
# therefore Windows, while the icons are consumed by the Linux and macOS packaging too. This
# exists so the assets are reproducible and adjustable rather than being three binaries nobody
# can edit. Run it after changing anything below:
#
#     pwsh Packaging/icons/generate-icons.ps1
#
# Replacing the mark entirely is a supported thing to do: drop your own agentic-memory.ico and
# agentic-memory-512.png in this folder and delete this script. Nothing else reads it.

param(
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = $PSScriptRoot

# Indigo to violet, the dashboard's own accent range.
$from = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)
$to   = [System.Drawing.Color]::FromArgb(255, 139, 92, 246)

# Three satellites around a hub: a memory and the things it is linked to. Angles in degrees,
# measured clockwise from straight up.
$satellites = @(0, 130, 230)

function New-Mark([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # Rounded square, inset slightly so antialiasing at the corners is not clipped by the edge.
        $inset  = [double]$size * 0.02
        $side   = [double]$size - (2 * $inset)
        $radius = $side * 0.22

        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc([float]$inset, [float]$inset, [float]$d, [float]$d, 180, 90)
        $path.AddArc([float]($inset + $side - $d), [float]$inset, [float]$d, [float]$d, 270, 90)
        $path.AddArc([float]($inset + $side - $d), [float]($inset + $side - $d), [float]$d, [float]$d, 0, 90)
        $path.AddArc([float]$inset, [float]($inset + $side - $d), [float]$d, [float]$d, 90, 90)
        $path.CloseFigure()

        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, 0)),
            (New-Object System.Drawing.Point($size, $size)),
            $from, $to)
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()

        $centreX = [double]$size / 2
        $centreY = [double]$size / 2
        $orbit   = [double]$size * 0.27
        $hub     = [double]$size * 0.115
        $node    = [double]$size * 0.072
        $stroke  = [double]$size * 0.052

        # Links first, so the nodes are drawn over the ends of them and no seam shows.
        $pen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(210, 255, 255, 255), [float]$stroke)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

        $points = @()
        foreach ($angle in $satellites) {
            $radians = ($angle - 90) * [Math]::PI / 180
            $x = $centreX + ($orbit * [Math]::Cos($radians))
            $y = $centreY + ($orbit * [Math]::Sin($radians))
            $points += , @($x, $y)
            $g.DrawLine($pen, [float]$centreX, [float]$centreY, [float]$x, [float]$y)
        }
        $pen.Dispose()

        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        foreach ($p in $points) {
            $g.FillEllipse($white, [float]($p[0] - $node), [float]($p[1] - $node),
                                   [float]($node * 2), [float]($node * 2))
        }
        $g.FillEllipse($white, [float]($centreX - $hub), [float]($centreY - $hub),
                               [float]($hub * 2), [float]($hub * 2))
        $white.Dispose()
    }
    finally {
        $g.Dispose()
    }
    return $bitmap
}

# A 32-bit DIB icon entry: BITMAPINFOHEADER, the BGRA pixels bottom-up, then the AND mask. The
# mask is unused at 32bpp — the alpha channel carries transparency — but it is still part of the
# format and readers that skip it are the exception rather than the rule, so it is written.
function ConvertTo-DibEntry([System.Drawing.Bitmap] $bitmap) {
    $w = $bitmap.Width
    $h = $bitmap.Height

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $maskStride = [int](([int](($w + 31) / 32)) * 4)

        $writer.Write([uint32]40)      # biSize
        $writer.Write([int32]$w)       # biWidth
        $writer.Write([int32]($h * 2)) # biHeight: colour and mask stacked, per the icon format
        $writer.Write([uint16]1)       # biPlanes
        $writer.Write([uint16]32)      # biBitCount
        $writer.Write([uint32]0)       # biCompression = BI_RGB
        $writer.Write([uint32](($w * $h * 4) + ($maskStride * $h)))
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)

        for ($y = $h - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $w; $x++) {
                $c = $bitmap.GetPixel($x, $y)
                $writer.Write([byte]$c.B); $writer.Write([byte]$c.G)
                $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
            }
        }
        $writer.Write((New-Object byte[] ($maskStride * $h)))

        $writer.Flush()
        # Leading comma: returning an array from a PowerShell function otherwise enumerates it into
        # the pipeline and the caller gets an Object[] of boxed bytes, which no BinaryWriter.Write
        # overload accepts. It bound to something else and wrote a single byte per image instead of
        # failing, so the icon came out structurally valid with every offset pointing past the end.
        return ,$stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function ConvertTo-PngBytes([System.Drawing.Bitmap] $bitmap) {
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray() # see the note in ConvertTo-DibEntry
    }
    finally {
        $stream.Dispose()
    }
}

# The large sizes go in as PNG and the small ones as uncompressed DIBs. An uncompressed 256x256
# entry is 256 KB by itself, which is most of the icon for the size nothing renders at; below
# 128 the saving is small and a DIB is what the oldest readers understand.
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$entries = foreach ($size in $sizes) {
    $bitmap = New-Mark $size
    try {
        [pscustomobject]@{
            Size = $size
            Data = [byte[]] $(if ($size -ge 128) { ConvertTo-PngBytes $bitmap } else { ConvertTo-DibEntry $bitmap })
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$icoPath = Join-Path $here 'agentic-memory.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0)                 # reserved
    $writer.Write([uint16]1)                 # type: icon
    $writer.Write([uint16]$entries.Count)

    # Directory entries are fixed width, so every offset is known before a byte of image data is
    # written: header, then 16 bytes per entry, then the images end to end.
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $writer.Write([byte]($entry.Size % 256)) # 256 is encoded as 0
        $writer.Write([byte]($entry.Size % 256))
        $writer.Write([byte]0)                   # palette size, 0 for true colour
        $writer.Write([byte]0)                   # reserved
        $writer.Write([uint16]1)                 # colour planes
        $writer.Write([uint16]32)                # bits per pixel
        $writer.Write([uint32]$entry.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Data.Length
    }
    foreach ($entry in $entries) { $writer.Write([byte[]] $entry.Data, 0, $entry.Data.Length) }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

# The sources the macOS .icns and the AppImage icon are derived from at package time. 1024 exists
# because an .icns wants a 512@2x entry, and letting sips upscale 512 to fill it would ship a soft
# icon on exactly the displays that show it largest.
foreach ($size in @(1024, 512, 256, 128, 64, 48, 32, 16)) {
    $bitmap = New-Mark $size
    try {
        $bitmap.Save((Join-Path $here "agentic-memory-$size.png"),
                     [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

Write-Host "Wrote $icoPath and agentic-memory-{512,256,128,64,48,32,16}.png"
