$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "src\MediaController.GameBar\MediaController.GameBar.csproj"
$localDir = Join-Path $root ".local"
$artifactsDir = Join-Path $root "artifacts\GameBar"
$packageDir = Join-Path $artifactsDir "AppPackages"
$publisher = "CN=Music Switch GameBar Dev"
$packageName = "theolegitz.MediaController.GameBar"
$pfxPath = Join-Path $localDir "MusicSwitch.GameBar.Dev.pfx"
$cerPath = Join-Path $localDir "MusicSwitch.GameBar.Dev.cer"
$passwordPath = Join-Path $localDir "MusicSwitch.GameBar.Dev.password.txt"

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $candidate = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw @"
MSBuild from Visual Studio was not found.
Install Visual Studio 2022 or Build Tools with the "Universal Windows Platform development" workload,
then run this script again. The normal Media Controller WPF app does not require that workload;
it is needed only once to build the Xbox Game Bar companion.
"@
}

function Find-LatestUapSdkVersion {
    $platformRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Platforms\UAP"
    if (-not (Test-Path $platformRoot)) {
        return $null
    }

    $versions = Get-ChildItem $platformRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                [PSCustomObject]@{ Version = [version]$_.Name; Text = $_.Name }
            } catch { }
        } |
        Sort-Object Version -Descending

    $latest = $versions | Select-Object -First 1
    if (-not $latest) {
        return $null
    }

    return $latest.Text
}

function Find-SignTool {
    $rootPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $rootPath)) {
        throw "Windows SDK SignTool was not found. Install the Windows 10/11 SDK from Visual Studio Installer."
    }

    $tools = Get-ChildItem $rootPath -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        ForEach-Object {
            $versionText = Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf
            try { $version = [version]$versionText } catch { $version = [version]"0.0" }
            [PSCustomObject]@{ Path = $_.FullName; Version = $version }
        } |
        Sort-Object Version -Descending

    $tool = $tools | Select-Object -First 1
    if (-not $tool) {
        throw "Windows SDK SignTool x64 was not found. Install the Windows 10/11 SDK from Visual Studio Installer."
    }

    return $tool.Path
}

function Ensure-DevCertificate {
    New-Item -ItemType Directory -Force -Path $localDir | Out-Null

    if ((Test-Path $pfxPath) -and (Test-Path $cerPath) -and (Test-Path $passwordPath)) {
        $password = (Get-Content $passwordPath -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($password)) {
            throw "The local Game Bar signing password file is empty. Delete .local and run again."
        }

        $secure = ConvertTo-SecureString $password -AsPlainText -Force
        $existing = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $publisher } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        if (-not $existing) {
            Import-PfxCertificate -FilePath $pfxPath -Password $secure -CertStoreLocation Cert:\CurrentUser\My | Out-Null
        }

        Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
        return $password
    }

    Write-Host "Creating a local developer certificate for the Game Bar companion..."

    $randomBytes = New-Object byte[] 32
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try {
        $rng.GetBytes($randomBytes)
    } finally {
        $rng.Dispose()
    }
    $password = [Convert]::ToBase64String($randomBytes)
    Set-Content -Path $passwordPath -Value $password -Encoding ASCII
    $secure = ConvertTo-SecureString $password -AsPlainText -Force

    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $publisher `
        -FriendlyName "Music Switch Game Bar Development" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null

    return $password
}

function Get-MainAppPackage {
    param([string]$Directory)

    $packages = Get-ChildItem $Directory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Extension -eq ".appx" -or $_.Extension -eq ".msix") -and
            $_.FullName -notmatch "\\Dependencies\\" -and
            $_.Name -notmatch "\.symbols\."
        } |
        Sort-Object Length -Descending

    return $packages | Select-Object -First 1
}

function Get-DependencyPackages {
    param([string]$Directory, [string]$MainPackagePath)

    return @(Get-ChildItem $Directory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Extension -eq ".appx" -or $_.Extension -eq ".msix") -and
            $_.FullName -ne $MainPackagePath -and
            $_.FullName -match "\\Dependencies\\"
        })
}

if (-not (Test-Path $project)) {
    throw "Game Bar project not found: $project"
}

$msbuild = Find-MSBuild
$signtool = Find-SignTool
$sdkVersion = Find-LatestUapSdkVersion
$password = Ensure-DevCertificate

Write-Host ""
Write-Host "Music Switch - Xbox Game Bar Overlay" -ForegroundColor Magenta
Write-Host "MSBuild: $msbuild"
if ($sdkVersion) {
    Write-Host "Windows UAP SDK: $sdkVersion"
} else {
    Write-Warning "No UAP SDK platform folder was detected. MSBuild will use the project default SDK."
}
Write-Host ""

if (Test-Path $artifactsDir) {
    Remove-Item $artifactsDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

$msbuildArgs = @(
    $project,
    "/restore",
    "/m",
    "/p:Configuration=Release",
    "/p:Platform=x64",
    "/p:AppxBundle=Never",
    "/p:AppxPackageSigningEnabled=false",
    "/p:GenerateAppxPackageOnBuild=true",
    "/p:AppxPackageDir=$packageDir\"
)
if ($sdkVersion) {
    $msbuildArgs += "/p:TargetPlatformVersion=$sdkVersion"
}

Write-Host "Building the Game Bar package..." -ForegroundColor Cyan
& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    throw @"
Game Bar project build failed (exit code $LASTEXITCODE).
If MSBuild reports missing UAP/XAML targets or Windows SDK files, open Visual Studio Installer and add:
  - Universal Windows Platform development
  - a Windows 10/11 SDK
Then run Install Game Bar Overlay.cmd again.
"@
}

$package = Get-MainAppPackage -Directory $packageDir
if (-not $package) {
    throw "MSBuild completed but no .appx/.msix package was found under $packageDir"
}

Write-Host "Signing $($package.Name)..." -ForegroundColor Cyan
& $signtool sign /fd SHA256 /f $pfxPath /p $password $package.FullName
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE."
}

$dependencies = Get-DependencyPackages -Directory $packageDir -MainPackagePath $package.FullName
$dependencyPaths = @($dependencies | ForEach-Object { $_.FullName })

Write-Host "Installing Game Bar companion for the current Windows user..." -ForegroundColor Cyan
$installArgs = @{
    Path = $package.FullName
    ForceApplicationShutdown = $true
    ForceUpdateFromAnyVersion = $true
}
if ($dependencyPaths.Count -gt 0) {
    $installArgs.DependencyPath = $dependencyPaths
}

try {
    Add-AppxPackage @installArgs
} catch {
    Write-Warning "In-place package update failed. Reinstalling the local development package..."
    $installed = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
    foreach ($item in $installed) {
        Remove-AppxPackage -Package $item.PackageFullName
    }

    $fallbackArgs = @{ Path = $package.FullName }
    if ($dependencyPaths.Count -gt 0) {
        $fallbackArgs.DependencyPath = $dependencyPaths
    }
    Add-AppxPackage @fallbackArgs
}

$verify = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if (-not $verify) {
    throw "The package command completed, but Windows does not report the Game Bar companion as installed."
}

Write-Host ""
Write-Host "Game Bar companion installed successfully." -ForegroundColor Green
Write-Host "Package: $($verify.PackageFullName)"
Write-Host ""
Write-Host "ONE-TIME GAME BAR SETUP:" -ForegroundColor Yellow
Write-Host "  1. Keep Media Controller running."
Write-Host "  2. Press Win + G."
Write-Host "  3. Open the Widget menu and choose 'Music Switch'."
Write-Host "  4. Pin the Music Switch widget."
Write-Host "  5. If desired, enable Game Bar click-through for the pinned widget."
Write-Host ""
Write-Host "After that, fullscreen track notifications are routed to the pinned Game Bar widget automatically."
