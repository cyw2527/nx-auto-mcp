@echo off
REM ================================================================
REM launch.bat -- One-click: compile + deploy + launch NX
REM
REM Usage:
REM   launch.bat              -> full: deploy + launch NX
REM   launch.bat no-deploy    -> skip deploy, just launch NX
REM   launch.bat debug        -> launch with NX debug logging
REM
REM Prerequisites:
REM   - NX installed (detected by nx-env.bat, override with NX_ROOT)
REM   - deploy.bat handles compile + deploy
REM
REM NOTE: keep every line in this file pure ASCII and CRLF-terminated.
REM        See the note in build.bat for why.
REM ================================================================

setlocal enabledelayedexpansion

REM -- NX environment detection --
call "%~dp0nx-env.bat" || exit /b 1
set NX_PATH=%NX_UGRAF%

if not "%1"=="no-deploy" (
    echo ================================================================
    echo LAUNCH: Step 1/2 - Deploying...
    echo ================================================================
    call "%~dp0deploy.bat"
    if errorlevel 1 (
        echo [ERROR] Deploy failed. Aborting launch.
        exit /b 1
    )
)

REM ================================================================
REM Step 2: Launch NX
REM ================================================================
if not exist "%NX_PATH%" (
    echo [ERROR] NX not found at: %NX_PATH%
    echo         Verify NX is installed and NX_ROOT points at it.
    exit /b 1
)

echo.
echo ================================================================
echo LAUNCH: Step 2/2 - Starting NX...
echo ================================================================

tasklist /FI "IMAGENAME eq ugraf.exe" 2>NUL | find /I /N "ugraf.exe" >NUL
if not errorlevel 1 (
    echo [WARN] NX ^(ugraf.exe^) appears to be already running.
    echo [WARN] The DLL will NOT be reloaded until NX restarts.
    echo.
    choice /C YN /M "Launch another instance anyway?"
    if errorlevel 2 exit /b 0
)

if "%1"=="debug" (
    echo [DEBUG MODE] Launching with debug logging...
    set UGII_DEBUG=1
)

echo Starting: %NX_PATH%
echo.
echo DLL loaded by NX (single-DLL rule -- only this one):
echo   %~dp0startup\managed_plugin.dll
echo.
echo TcpServer listens on 127.0.0.1:1977.
echo.

start "" "%NX_PATH%"

echo NX launched. Waiting for initialization...
echo.
echo Once NX is fully loaded, verify:
echo   - Context menu shows the correct plugin version
echo   - netstat -an ^| findstr 1977   ^(port should be LISTENING^)
echo   - node "%~dp0..\scripts\check-plugin-status.js"
echo.

endlocal & exit /b 0
