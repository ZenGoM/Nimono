$source = "C:\Users\masanori\source\repos\Nimono\Assets\Square150x150Logo.png"
$destDir = "C:\Users\masanori\source\repos\Nimono\Nimono.Package\Images"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

Add-Type -AssemblyName System.Drawing

$img = [System.Drawing.Image]::FromFile($source)

function Resize-Image($w, $h, $name) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    
    # If it's a splash screen (620x300), center the 150x150 logo
    if ($w -eq 620 -and $h -eq 300) {
        $x = ($w - 150) / 2
        $y = ($h - 150) / 2
        $g.DrawImage($img, $x, $y, 150, 150)
    }
    # If it's wide logo (310x150), center the logo
    elseif ($w -eq 310 -and $h -eq 150) {
        $x = ($w - 150) / 2
        $y = ($h - 150) / 2
        $g.DrawImage($img, $x, $y, 150, 150)
    }
    else {
        $g.DrawImage($img, 0, 0, $w, $h)
    }
    
    $g.Dispose()
    $bmp.Save((Join-Path $destDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Resize-Image 150 150 "Square150x150Logo.png"
Resize-Image 44 44 "Square44x44Logo.png"
Resize-Image 310 150 "Wide310x150Logo.png"
Resize-Image 620 300 "SplashScreen.png"
Resize-Image 50 50 "StoreLogo.png"

$img.Dispose()
