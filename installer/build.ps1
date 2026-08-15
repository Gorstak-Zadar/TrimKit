# TrimKit Installer Build Script
# Usage: .\build.ps1

$ErrorActionPreference = "Stop"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "   TrimKit - Building Installer               " -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan

# 0. Read version from .csproj
$CsprojFile = Join-Path $PSScriptRoot "..\src\TrimKit\TrimKit.csproj"
$CsprojContent = Get-Content $CsprojFile -Raw
$VersionMatch = [regex]::Match($CsprojContent, '<Version>([^<]+)</Version>')
if (-not $VersionMatch.Success) {
    Write-Host "ERROR: Could not read version from TrimKit.csproj" -ForegroundColor Red
    Exit 1
}
$Version = $VersionMatch.Groups[1].Value
Write-Host "Version: $Version" -ForegroundColor Green

# 0.1 Stamp Inno Setup script with current version
$SetupScript = Join-Path $PSScriptRoot "setup.iss"
$issContent = Get-Content $SetupScript -Raw
$issContent = $issContent -replace 'AppVersion=.*', "AppVersion=$Version"
$issContent = $issContent -replace 'OutputBaseFilename=TrimKit-Setup-.*', "OutputBaseFilename=TrimKit-Setup-$Version"
Set-Content $SetupScript -Value $issContent -NoNewline
Write-Host "Stamped setup.iss with version $Version" -ForegroundColor Yellow

# 1. Clean previous build artifacts
$PublishDir = Join-Path $PSScriptRoot "..\publish"
$SrcDir = Join-Path $PSScriptRoot "..\src"
Write-Host "Cleaning bin/obj/publish..." -ForegroundColor Yellow

# Nuke bin and obj recursively
Get-ChildItem -Path $SrcDir -Include bin,obj -Directory -Recurse | ForEach-Object {
    Write-Host "  Deleting: $($_.FullName)" -ForegroundColor DarkGray
    Remove-Item $_.FullName -Recurse -Force
}

# Nuke publish folder entirely
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}

# Also clean releases output if stale
$ReleasesDir = Join-Path $PSScriptRoot "..\releases"
if (Test-Path $ReleasesDir) {
    Get-ChildItem -Path $ReleasesDir -Filter "TrimKit-Setup-*.exe" | Remove-Item -Force
    Write-Host "  Cleaned old installer(s) from releases/" -ForegroundColor DarkGray
}

# Run dotnet clean to flush any cached intermediate state
Write-Host "Running dotnet clean..." -ForegroundColor Yellow
& dotnet clean $CsprojFile -c Release --nologo -v q 2>$null
& dotnet clean $CsprojFile -c Debug --nologo -v q 2>$null

Write-Host "Clean complete." -ForegroundColor Green

# 2. Publish TrimKit (win-x64 self-contained)
Write-Host "Publishing TrimKit (win-x64 self-contained)..." -ForegroundColor Yellow
& dotnet publish $CsprojFile -c Release -r win-x64 --self-contained -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    Exit 1
}
Write-Host "Publish complete." -ForegroundColor Green

# 3. Locate Inno Setup Compiler (ISCC.exe)
Write-Host "Locating Inno Setup compiler..." -ForegroundColor Yellow
$DefaultIsccPaths = @(
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$IsccPath = $null
foreach ($Path in $DefaultIsccPaths) {
    if (Test-Path $Path) {
        $IsccPath = $Path
        break
    }
}

if (-not $IsccPath) {
    $IsccPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}

if (-not $IsccPath) {
    Write-Host "ERROR: Inno Setup compiler (ISCC.exe) not found." -ForegroundColor Red
    Write-Host "Install Inno Setup 6: https://jrsoftware.org/isdl.php" -ForegroundColor Red
    Exit 1
}

Write-Host "Found Inno Setup at: $IsccPath" -ForegroundColor Green

# 4. Compile the Installer
Write-Host "Compiling installer with Inno Setup..." -ForegroundColor Yellow
& $IsccPath $SetupScript
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Inno Setup compilation failed." -ForegroundColor Red
    Exit 1
}

Write-Host "==============================================" -ForegroundColor Green
Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "Installer: releases\TrimKit-Setup-$Version.exe" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
