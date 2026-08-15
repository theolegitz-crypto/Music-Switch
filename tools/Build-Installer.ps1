param(
    [string]$Version = "",
    [string]$RepositoryUrl = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\MediaController\MediaController.csproj"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$releases = Join-Path $artifacts "Releases"
$notes = Join-Path $root "RELEASE_NOTES.md"
$icon = Join-Path $root "src\MediaController\Assets\MediaController.ico"
$vpkVersion = "1.2.0"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content $project
    $defaultVersion = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ([string]::IsNullOrWhiteSpace($defaultVersion)) { $defaultVersion = "0.5.1" }
    $enteredVersion = Read-Host "Release version [$defaultVersion]"
    $Version = if ([string]::IsNullOrWhiteSpace($enteredVersion)) { $defaultVersion } else { $enteredVersion.Trim() }
}

if ($Version -notmatch '^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer, for example 0.5.1"
}

Write-Host ""
Write-Host "Media Controller - Build Installer" -ForegroundColor Magenta
Write-Host "Version: $Version"
Write-Host ""

if ([string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    $RepositoryUrl = Read-Host "GitHub repository URL for automatic updates (leave blank to build installer only)"
}

if (-not [string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    $RepositoryUrl = $RepositoryUrl.Trim().TrimEnd('/')
    if ($RepositoryUrl.EndsWith('.git', [System.StringComparison]::OrdinalIgnoreCase)) {
        $RepositoryUrl = $RepositoryUrl.Substring(0, $RepositoryUrl.Length - 4)
    }
    if ($RepositoryUrl -notmatch '^https://github\.com/[^/]+/[^/]+$') {
        throw "Repository URL must look like https://github.com/OWNER/REPO"
    }
    Write-Host "Update source: $RepositoryUrl" -ForegroundColor DarkGray
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK was not found. Install the .NET 8 SDK first."
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish | Out-Null
New-Item -ItemType Directory -Force -Path $releases | Out-Null

Write-Host "Publishing self-contained win-x64 build..." -ForegroundColor Cyan
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish "/p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# The runtime app reads this file. CI does the same, so no token or private credential is embedded.
Set-Content -Path (Join-Path $publish "update-source.txt") -Value $RepositoryUrl -Encoding UTF8

Write-Host "Ensuring Velopack CLI $vpkVersion is installed..." -ForegroundColor Cyan
& dotnet tool update -g vpk --version $vpkVersion
if ($LASTEXITCODE -ne 0) {
    & dotnet tool install -g vpk --version $vpkVersion
    if ($LASTEXITCODE -ne 0) { throw "Could not install Velopack CLI." }
}

$vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (-not (Test-Path $vpk)) {
    $vpkCommand = Get-Command vpk -ErrorAction SilentlyContinue
    if ($null -eq $vpkCommand) { throw "vpk executable was not found after installation." }
    $vpk = $vpkCommand.Source
}

if (-not [string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    Write-Host "Trying to download the previous GitHub release for delta generation..." -ForegroundColor Cyan
    & $vpk download github --repoUrl $RepositoryUrl --outputDir $releases
    if ($LASTEXITCODE -ne 0) {
        Write-Host "No previous release was downloaded. This is normal for the first release." -ForegroundColor DarkYellow
        $global:LASTEXITCODE = 0
    }
}

Write-Host "Creating Setup.exe and update packages..." -ForegroundColor Cyan
$args = @(
    "pack",
    "--packId", "MediaController.Desktop",
    "--packVersion", $Version,
    "--packDir", $publish,
    "--mainExe", "MediaController.exe",
    "--packTitle", "Media Controller",
    "--icon", $icon,
    "--runtime", "win-x64",
    "--outputDir", $releases
)

if (Test-Path $notes) {
    $args += @("--releaseNotes", $notes)
}

& $vpk @args
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed." }

$setup = Get-ChildItem $releases -Filter "*-Setup.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host "Done." -ForegroundColor Green
if ($setup) {
    Write-Host "Installer: $($setup.FullName)" -ForegroundColor Green
}
if ([string]::IsNullOrWhiteSpace($RepositoryUrl)) {
    Write-Host "Automatic online updates are disabled in this installer because no GitHub repository URL was supplied." -ForegroundColor DarkYellow
}
Write-Host ""
Start-Process explorer.exe $releases
