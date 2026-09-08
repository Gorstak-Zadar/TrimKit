@echo off
REM TrimKit Installer Build Script
REM Usage: build.bat
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
REM Strip trailing backslash
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

echo ==============================================
echo    TrimKit - Building Installer
echo ==============================================

REM 0. Read version from .csproj and stamp setup.iss (regex handled by PowerShell)
for /f "usebackq delims=" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%\stamp-version.ps1"`) do set "VERSION=%%V"
if not defined VERSION (
    echo ERROR: Could not read version from TrimKit.csproj
    exit /b 1
)
echo Version: %VERSION%
echo Stamped setup.iss with version %VERSION%

set "CSPROJ=%SCRIPT_DIR%\..\src\TrimKit\TrimKit.csproj"
set "SRC_DIR=%SCRIPT_DIR%\..\src"
set "PUBLISH_DIR=%SCRIPT_DIR%\..\publish"
set "RELEASES_DIR=%SCRIPT_DIR%\..\releases"
set "VERSION_RELEASE_DIR=%RELEASES_DIR%\%VERSION%"

REM 1. Clean previous build artifacts
echo Cleaning bin/obj/publish...
for /d /r "%SRC_DIR%" %%D in (bin obj) do (
    if exist "%%D" (
        echo   Deleting: %%D
        rmdir /s /q "%%D"
    )
)

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

REM Clean this version's release folder if stale (other versions are preserved)
if exist "%VERSION_RELEASE_DIR%" (
    rmdir /s /q "%VERSION_RELEASE_DIR%"
    echo   Cleaned old release folder: releases\%VERSION%
)
mkdir "%VERSION_RELEASE_DIR%"

REM Run dotnet clean to flush cached intermediate state
echo Running dotnet clean...
call dotnet clean "%CSPROJ%" -c Release --nologo -v q >nul 2>&1
call dotnet clean "%CSPROJ%" -c Debug --nologo -v q >nul 2>&1
echo Clean complete.

REM 2. Publish TrimKit (win-x64 self-contained)
echo Publishing TrimKit (win-x64 self-contained)...
call dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)
echo Publish complete.

REM 3. Locate Inno Setup Compiler (ISCC.exe)
echo Locating Inno Setup compiler...
set "ISCC="
for %%P in (
    "C:\Program Files\Inno Setup 7\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) do (
    if not defined ISCC if exist %%P set "ISCC=%%~P"
)
if not defined ISCC (
    for %%I in (ISCC.exe) do set "ISCC=%%~$PATH:I"
)
if not defined ISCC (
    echo ERROR: Inno Setup compiler ^(ISCC.exe^) not found.
    echo Install Inno Setup 6: https://jrsoftware.org/isdl.php
    exit /b 1
)
echo Found Inno Setup at: %ISCC%

REM 4. Compile the Installer
echo Compiling installer with Inno Setup...
call "%ISCC%" "%SCRIPT_DIR%\setup.iss"
if errorlevel 1 (
    echo ERROR: Inno Setup compilation failed.
    exit /b 1
)

echo ==============================================
echo Build completed successfully!
echo Installer: releases\%VERSION%\TrimKit-Setup-%VERSION%.exe
echo ==============================================

endlocal
