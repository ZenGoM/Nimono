# Publish script for Microsoft Store MSIX package
# Usage: powershell -ExecutionPolicy Bypass -File .\publish.ps1
# Option: Use -Sign to self-sign for local testing.

param (
    [switch]$Sign = $false
)

$ErrorActionPreference = "Stop"

# Configuration
$projectName = "Nimono"
$publishRuntime = "win-x64"
$publishConfig = "Release"
$sourceDir = $PSScriptRoot
$projectFile = Join-Path $sourceDir "$projectName.csproj"
$manifestFile = Join-Path $sourceDir "AppxManifest.xml"
$assetsDir = Join-Path $sourceDir "Assets"

# Output directory configuration
$publishOutputDir = Join-Path $sourceDir "bin\$publishConfig\net9.0-windows\$publishRuntime\publish"
$distDir = Join-Path $sourceDir "dist"
$msixPath = Join-Path $distDir "$projectName.msix"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Starting MSIX packaging process" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. dotnet publish
Write-Host "[1/4] Building release binary..." -ForegroundColor Yellow
if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}

dotnet publish $projectFile -c $publishConfig -r $publishRuntime --self-contained true -p:PublishReadyToRun=true

# 2. Copy manifest and assets
Write-Host "[2/4] Copying manifest and assets to publish directory..." -ForegroundColor Yellow

if (-not (Test-Path $manifestFile)) {
    throw "AppxManifest.xml not found."
}
if (-not (Test-Path $assetsDir)) {
    throw "Assets directory not found."
}

Copy-Item -Path $manifestFile -Destination $publishOutputDir -Force

$destAssetsDir = Join-Path $publishOutputDir "Assets"
if (Test-Path $destAssetsDir) {
    Remove-Item -Path $destAssetsDir -Recurse -Force
}
Copy-Item -Path $assetsDir -Destination $publishOutputDir -Recurse -Force

# 3. Packaging with makeappx.exe
Write-Host "[3/4] Packaging to MSIX..." -ForegroundColor Yellow

$sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeappx = Get-ChildItem -Path $sdkRoot -Filter "makeappx.exe" -Recurse | Where-Object { $_.FullName -like "*\x64\*" } | Select-Object -First 1 -ExpandProperty FullName

if (-not $makeappx) {
    throw "makeappx.exe not found. Make sure Windows SDK is installed."
}
Write-Host "Using makeappx: $makeappx" -ForegroundColor Gray

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}
if (Test-Path $msixPath) {
    Remove-Item -Path $msixPath -Force
}

& $makeappx pack /d $publishOutputDir /p $msixPath /o
Write-Host "MSIX package created at: $msixPath" -ForegroundColor Green

# 4. Self-signing (Optional)
if ($Sign) {
    Write-Host "[4/4] Creating test certificate and signing MSIX..." -ForegroundColor Yellow
    
    $signtool = Get-ChildItem -Path $sdkRoot -Filter "signtool.exe" -Recurse | Where-Object { $_.FullName -like "*\x64\*" } | Select-Object -First 1 -ExpandProperty FullName
                
    if (-not $signtool) {
        Write-Warning "signtool.exe not found. Skipping signing."
        return
    }

    $publisherSubject = "CN=E2338FF3-89FC-4D73-967C-BC126F7A8857"
    $pfxPath = Join-Path $distDir "NimonoTest.pfx"
    $passwordText = "NimonoTestPassword123"
    $password = ConvertTo-SecureString $passwordText -AsPlainText -Force

    Write-Host "Creating self-signed certificate..." -ForegroundColor Gray
    Get-ChildItem -Path "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $publisherSubject } | Remove-Item -ErrorAction SilentlyContinue

    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisherSubject -KeyUsage DigitalSignature -FriendlyName "Nimono Test Certificate" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null
    
    Write-Host "Signing the package..." -ForegroundColor Gray
    & $signtool sign /fd SHA256 /a /f $pfxPath /p $passwordText $msixPath
    
    Write-Host "Signing completed successfully." -ForegroundColor Green
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "To install this signed MSIX locally for testing:" -ForegroundColor Yellow
    Write-Host "1. Right click '$pfxPath' and select 'Install PFX'."
    Write-Host "2. Select 'Local Machine' as the Store Location."
    Write-Host "3. Place all certificates in the following store: 'Trusted Root Certification Authorities'."
    Write-Host "==========================================" -ForegroundColor Cyan
} else {
    Write-Host "[4/4] Signing skipped. Ready for store upload." -ForegroundColor Green
}

Write-Host "Publish process completed successfully!" -ForegroundColor Green
Write-Host "Package Path: $msixPath" -ForegroundColor Green
