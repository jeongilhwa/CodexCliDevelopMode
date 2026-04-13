# ==========================================================================
# CodexDesk Build & Package Script
# Produces: artifacts/installer/CodexDeskSetup-{version}.exe
# ==========================================================================

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$NodeVersion = "22.15.0",
    [switch]$SkipPublish,
    [switch]$SkipDownload,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"  # Speed up Invoke-WebRequest

# ------- Paths -------
$RepoRoot      = Split-Path -Parent $PSScriptRoot
$SrcProject    = Join-Path $RepoRoot "src\CodexDesk.App\CodexDesk.App.csproj"
$ArtifactsDir  = Join-Path $RepoRoot "artifacts"
$PublishDir    = Join-Path $ArtifactsDir "publish"
$InstallerDir  = Join-Path $RepoRoot "installer"
$RedistDir     = Join-Path $InstallerDir "redist"
$OutputDir     = Join-Path $ArtifactsDir "installer"

# ------- Node.js MSI URL -------
$NodeMsiName   = "node-v${NodeVersion}-x64.msi"
$NodeMsiUrl    = "https://nodejs.org/dist/v${NodeVersion}/${NodeMsiName}"

# ------- WebView2 Bootstrapper URL -------
$WebView2Url   = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$WebView2File  = "MicrosoftEdgeWebview2Setup.exe"

# ------- Inno Setup -------
$InnoSetupCompiler = $null
$InnoPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
foreach ($p in $InnoPaths) {
    if (Test-Path $p) { $InnoSetupCompiler = $p; break }
}

# ==========================================================================
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CodexDesk Build & Package" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ------- Step 0: Validate prerequisites -------
Write-Host "[0/4] Checking prerequisites..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet CLI not found. Install .NET SDK first."
    exit 1
}

if (-not $SkipInstaller -and -not $InnoSetupCompiler) {
    Write-Error "Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php"
    exit 1
}

Write-Host "  dotnet: $(dotnet --version)" -ForegroundColor Gray
if ($InnoSetupCompiler) {
    Write-Host "  Inno Setup: $InnoSetupCompiler" -ForegroundColor Gray
}

# ------- Step 1: dotnet publish -------
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[1/4] Publishing CodexDesk ($Configuration, $Runtime)..." -ForegroundColor Yellow

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    dotnet publish $SrcProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    Write-Host "  Published to: $PublishDir" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[1/4] Skipping publish (--SkipPublish)" -ForegroundColor DarkGray
}

# ------- Step 2: Download redistributables -------
if (-not $SkipDownload) {
    Write-Host ""
    Write-Host "[2/4] Downloading redistributables..." -ForegroundColor Yellow

    if (-not (Test-Path $RedistDir)) {
        New-Item -ItemType Directory -Path $RedistDir -Force | Out-Null
    }

    # Node.js MSI
    $NodeMsiPath = Join-Path $RedistDir $NodeMsiName
    if (-not (Test-Path $NodeMsiPath)) {
        Write-Host "  Downloading Node.js v${NodeVersion} MSI..." -ForegroundColor Gray
        Invoke-WebRequest -Uri $NodeMsiUrl -OutFile $NodeMsiPath
        Write-Host "  Downloaded: $NodeMsiName" -ForegroundColor Green
    } else {
        Write-Host "  Node.js MSI already exists: $NodeMsiName" -ForegroundColor DarkGray
    }

    # WebView2 Bootstrapper
    $WebView2Path = Join-Path $RedistDir $WebView2File
    if (-not (Test-Path $WebView2Path)) {
        Write-Host "  Downloading WebView2 Bootstrapper..." -ForegroundColor Gray
        Invoke-WebRequest -Uri $WebView2Url -OutFile $WebView2Path
        Write-Host "  Downloaded: $WebView2File" -ForegroundColor Green
    } else {
        Write-Host "  WebView2 bootstrapper already exists." -ForegroundColor DarkGray
    }
} else {
    Write-Host ""
    Write-Host "[2/4] Skipping download (--SkipDownload)" -ForegroundColor DarkGray
}

# ------- Step 3: Verify redist files -------
Write-Host ""
Write-Host "[3/4] Verifying redist files..." -ForegroundColor Yellow

$NodeMsiPath = Get-ChildItem (Join-Path $RedistDir "node-v22.*.msi") -ErrorAction SilentlyContinue | Select-Object -First 1
$WebView2Path = Join-Path $RedistDir $WebView2File

if (-not $NodeMsiPath) {
    Write-Error "Node.js MSI not found in $RedistDir. Run without -SkipDownload."
    exit 1
}
if (-not (Test-Path $WebView2Path)) {
    Write-Error "WebView2 bootstrapper not found in $RedistDir. Run without -SkipDownload."
    exit 1
}

Write-Host "  Node.js MSI:  $($NodeMsiPath.Name)" -ForegroundColor Green
Write-Host "  WebView2:     $WebView2File" -ForegroundColor Green

# ------- Step 4: Build installer with Inno Setup -------
if (-not $SkipInstaller) {
    Write-Host ""
    Write-Host "[4/4] Building installer with Inno Setup..." -ForegroundColor Yellow

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }

    $IssFile = Join-Path $InstallerDir "CodexDesk.iss"

    & $InnoSetupCompiler $IssFile

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
        exit 1
    }

    $SetupFile = Get-ChildItem (Join-Path $OutputDir "CodexDeskSetup-*.exe") | Select-Object -First 1
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  BUILD COMPLETE" -ForegroundColor Green
    Write-Host "  Output: $($SetupFile.FullName)" -ForegroundColor Green
    Write-Host "  Size:   $([math]::Round($SetupFile.Length / 1MB, 1)) MB" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[4/4] Skipping installer build (--SkipInstaller)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
