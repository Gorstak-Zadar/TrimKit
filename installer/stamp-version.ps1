# TrimKit - version stamping helper
# Reads <Version> from TrimKit.csproj, stamps setup.iss, and prints the version.
# Called by build.bat. Prints ONLY the version string to stdout on success.

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# Read version from .csproj
$CsprojFile = Join-Path $ScriptRoot "..\src\TrimKit\TrimKit.csproj"
$CsprojContent = Get-Content $CsprojFile -Raw
$VersionMatch = [regex]::Match($CsprojContent, '<Version>([^<]+)</Version>')
if (-not $VersionMatch.Success) {
    Write-Error "Could not read version from TrimKit.csproj"
    exit 1
}
$Version = $VersionMatch.Groups[1].Value

# Stamp Inno Setup script with current version
$SetupScript = Join-Path $ScriptRoot "setup.iss"
$issContent = Get-Content $SetupScript -Raw
$issContent = $issContent -replace 'AppVersion=.*', "AppVersion=$Version"
$issContent = $issContent -replace 'OutputDir=\.\.\\releases.*', "OutputDir=..\releases\$Version"
$issContent = $issContent -replace 'OutputBaseFilename=TrimKit-Setup-.*', "OutputBaseFilename=TrimKit-Setup-$Version"
Set-Content $SetupScript -Value $issContent -NoNewline

# Emit the version as the only stdout line so build.bat can capture it
Write-Output $Version
