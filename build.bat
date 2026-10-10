@echo off
setlocal EnableDelayedExpansion
title Build Tool - Antigravity Hub

cd /d "%~dp0"

echo =======================================================================
echo    ANTIGRAVITY HUB - BUILD TOOL
echo =======================================================================
echo.
echo   [1] Build Ban Portable (.EXE chay luon - Single File 64-bit)
echo   [2] Build Bo Cai Dat (.EXE Setup Installer Windows)
echo   [3] Build CA HAI (Portable + Setup Installer)
echo   [4] Thoat
echo.
echo =======================================================================
set /p CHOICE="Nhap lua chon (1/2/3/4) [Mac dinh la 3]: "

if "%CHOICE%"=="" set CHOICE=3
set "FIRST_CHAR=%CHOICE:~0,1%"
if "%FIRST_CHAR%"=="4" goto :EOF
if "%FIRST_CHAR%"=="1" goto :BUILD_PORTABLE
if "%FIRST_CHAR%"=="2" goto :BUILD_SETUP
if "%FIRST_CHAR%"=="3" goto :BUILD_ALL

echo Lua chon khong hop le. Thoat.
goto :EOF

:BUILD_ALL
set DO_PORTABLE=1
set DO_SETUP=1
goto :START_BUILD

:BUILD_PORTABLE
set DO_PORTABLE=1
set DO_SETUP=0
goto :START_BUILD

:BUILD_SETUP
set DO_PORTABLE=0
set DO_SETUP=1
goto :START_BUILD

:START_BUILD
echo.
echo [1/3] Dang tat ung dung cu neu dang mo...
powershell -NoProfile -Command "Get-Process AntigravityHub, ProfileSwitchChrome -ErrorAction SilentlyContinue | Stop-Process -Force" >nul 2>&1
echo       [OK] Da san sang ghi de file.

if not exist "dist" mkdir "dist"
if not exist "dist\Portable" mkdir "dist\Portable"
if not exist "dist\Setup" mkdir "dist\Setup"

:: -------------------------------------------------------------------------
:: 1. BUILD PORTABLE EXE
:: -------------------------------------------------------------------------
if "%DO_PORTABLE%"=="0" goto :SKIP_PORTABLE

echo.
echo [2/3] Dang dong goi Ban Portable (.EXE chay luon)...
echo       Cau hinh: Release - win-x64 - Self-Contained
echo.

dotnet publish "ProfileSwitchChrome.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "dist\Portable"

if errorlevel 1 (
    echo.
    echo [LOI] Qua trinh dotnet publish that bai!
    pause
    goto :EOF
)

if exist "dist\Portable\AntigravityHub.pdb" del /q "dist\Portable\AntigravityHub.pdb" >nul 2>&1
echo.
echo       [OK] Da tao xong: dist\Portable\AntigravityHub.exe

:SKIP_PORTABLE

:: -------------------------------------------------------------------------
:: 2. BUILD SETUP INSTALLER EXE
:: -------------------------------------------------------------------------
if "%DO_SETUP%"=="0" goto :SKIP_SETUP

echo.
echo [3/3] Dang chuan bi ban Portable moi nhat cho bo cai...
echo       Dang dong goi ma nguon moi nhat...
dotnet publish "ProfileSwitchChrome.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "dist\Portable"
if exist "dist\Portable\AntigravityHub.pdb" del /q "dist\Portable\AntigravityHub.pdb" >nul 2>&1

echo       Dang tim trinh bien dich Inno Setup ISCC...
set "ISCC="
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if defined ISCC goto :FOUND_ISCC
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if defined ISCC goto :FOUND_ISCC
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if defined ISCC goto :FOUND_ISCC

echo       Chua co Inno Setup. Dang cai dat qua winget...
winget install JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements --silent
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

:FOUND_ISCC

if not defined ISCC (
    echo [LOI] Khong tim thay ISCC.exe! Vui long cai Inno Setup 6.
    pause
    goto :EOF
)

echo       [OK] Trinh bien dich: "!ISCC!"
echo       Dang dong goi bo cai Windows Setup...
"!ISCC!" "installer.iss" /Qp

if errorlevel 1 (
    echo.
    echo [LOI] Dong goi Inno Setup that bai!
    pause
    goto :EOF
)

echo.
echo       [OK] Da tao xong: dist\Setup\AntigravityHub_Setup_v2.5.exe

:SKIP_SETUP

:: -------------------------------------------------------------------------
:: HOAN TAT
:: -------------------------------------------------------------------------
echo.
echo =======================================================================
echo   THANH CONG! CAC FILE DA DUOC TAO:
echo =======================================================================
if exist "dist\Portable\ProfileSwitchChrome.exe" echo   [1] Ban Portable (Chay Luon): dist\Portable\ProfileSwitchChrome.exe
if exist "dist\Setup\ProfileSwitchChrome_Setup_v2.5.exe" echo   [2] Bo Cai Dat (Setup Installer): dist\Setup\ProfileSwitchChrome_Setup_v2.5.exe
echo =======================================================================
echo.
echo Dang mo thu muc dist...
start "" explorer.exe "%~dp0dist"

echo Nhan phim bat ky de thoat...
pause >nul
