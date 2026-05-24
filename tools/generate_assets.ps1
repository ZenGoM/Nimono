Add-Type -AssemblyName PresentationCore, WindowsBase

function Resize-Image {
    param (
        [string]$SourcePath,
        [string]$DestinationPath,
        [int]$Width,
        [int]$Height,
        [bool]$KeepAspect = $true
    )
    
    $srcStream = [System.IO.File]::OpenRead($SourcePath)
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($srcStream, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $frame = $decoder.Frames[0]
    $srcStream.Close()

    if ($KeepAspect) {
        $aspectSrc = $frame.PixelWidth / $frame.PixelHeight
        $aspectDest = $Width / $Height
        
        if ($aspectSrc -gt $aspectDest) {
            $newW = $Width
            $newH = [math]::Round($Width / $aspectSrc)
        } else {
            $newH = $Height
            $newW = [math]::Round($Height * $aspectSrc)
        }
        
        $scaleX = $newW / $frame.PixelWidth
        $scaleY = $newH / $frame.PixelHeight
        $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap($frame, (New-Object System.Windows.Media.ScaleTransform($scaleX, $scaleY)))
        
        $visual = New-Object System.Windows.Media.DrawingVisual
        $context = $visual.RenderOpen()
        
        $offsetX = ($Width - $newW) / 2
        $offsetY = ($Height - $newH) / 2
        $context.DrawImage($resized, (New-Object System.Windows.Rect($offsetX, $offsetY, $newW, $newH)))
        $context.Close()
        
        $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Width, $Height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
        $target.Render($visual)
        
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target)) | Out-Null
    } else {
        $scaleX = $Width / $frame.PixelWidth
        $scaleY = $Height / $frame.PixelHeight
        $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap($frame, (New-Object System.Windows.Media.ScaleTransform($scaleX, $scaleY)))
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($resized)) | Out-Null
    }

    $destDir = [System.IO.Path]::GetDirectoryName($DestinationPath)
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }

    $destStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $encoder.Save($destStream)
    $destStream.Close()
    Write-Host "Generated: $DestinationPath ($Width x $Height)"
}

$baseImage = "C:\Users\masanori\.gemini\antigravity\brain\d2376b69-f71e-49ca-8766-6146dedd2bd9\app_logo_base_1779602279325.png"
$assetsDir = "c:\Users\masanori\source\repos\Nimono\Assets"

Resize-Image -SourcePath $baseImage -DestinationPath "$assetsDir\Square150x150Logo.png" -Width 150 -Height 150 -KeepAspect $false
Resize-Image -SourcePath $baseImage -DestinationPath "$assetsDir\Square44x44Logo.png" -Width 44 -Height 44 -KeepAspect $false
Resize-Image -SourcePath $baseImage -DestinationPath "$assetsDir\StoreLogo.png" -Width 50 -Height 50 -KeepAspect $false
Resize-Image -SourcePath $baseImage -DestinationPath "$assetsDir\Wide310x150Logo.png" -Width 310 -Height 150 -KeepAspect $true
Resize-Image -SourcePath $baseImage -DestinationPath "$assetsDir\BadgeLogo.png" -Width 24 -Height 24 -KeepAspect $false
