$ErrorActionPreference = "Stop"

$packageName = "theolegitz.MediaController.GameBar"
$packages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)

if ($packages.Count -eq 0) {
    Write-Host "Music Switch Game Bar Overlay is not installed." -ForegroundColor Yellow
    exit 0
}

foreach ($package in $packages) {
    Write-Host "Removing $($package.PackageFullName)..."
    Remove-AppxPackage -Package $package.PackageFullName
}

Write-Host "Music Switch Game Bar Overlay removed." -ForegroundColor Green
