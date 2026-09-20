@echo off
REM ================================================================
REM setup.bat -- one-time environment setup for nx-auto-mcp
REM
REM Does two things:
REM   1. Generates custom_dirs.dat (one line: this package's plugin\ folder)
REM   2. Sets the user-level environment variables
REM      UGII_CUSTOM_DIRECTORY_FILE and UGII_USER_DIR
REM
REM NX finds the plugin through these two variables. Without them: no menu,
REM no TCP 1977 listener, and no error from NX.
REM
REM WARNING: this script modifies permanent user-level environment
REM          variables via setx. Existing values are OVERWRITTEN.
REM WARNING: NX must be fully restarted afterwards.
REM
REM NOTE: keep every line in this file pure ASCII and CRLF-terminated.
REM        See the note in build.bat for why.
REM ================================================================

setlocal enabledelayedexpansion

set "PKG=%~dp0"
if "%PKG:~-1%"=="\" set "PKG=%PKG:~0,-1%"
set "PLUGIN=%PKG%\plugin"

echo ================================================================
echo nx-auto-mcp environment setup
echo ================================================================
echo.

REM -- Detect NX (reuses plugin\nx-env.bat, also validates the install) --
call "%~dp0plugin\nx-env.bat" || exit /b 1
echo NX_ROOT     = %NX_ROOT%
echo Package dir = %PKG%
echo Plugin dir  = %PLUGIN%
echo.

if not exist "%PLUGIN%\startup\managed_plugin.dll" (
    echo [ERROR] The plugin DLL has not been built yet.
    echo.
    echo         This repository ships source only, no prebuilt DLL.
    echo         Build it first:
    echo           cd plugin
    echo           build.bat run
    echo.
    echo         Requires the .NET Framework 4.8 csc.exe and the NX SDK.
    exit /b 1
)

REM -- 1. custom_dirs.dat --
> "%PKG%\custom_dirs.dat" echo %PLUGIN%
echo [1/2] Generated custom_dirs.dat
echo         content: %PLUGIN%

REM -- 2. User-level environment variables --
setx UGII_CUSTOM_DIRECTORY_FILE "%PKG%\custom_dirs.dat" >nul
if errorlevel 1 (
    echo [ERROR] setx UGII_CUSTOM_DIRECTORY_FILE failed.
    exit /b 1
)
setx UGII_USER_DIR "%PKG%" >nul
if errorlevel 1 (
    echo [ERROR] setx UGII_USER_DIR failed.
    exit /b 1
)
echo [2/2] Set UGII_CUSTOM_DIRECTORY_FILE and UGII_USER_DIR

echo.
echo ================================================================
echo Setup complete -- two more steps are required
echo ================================================================
echo   1. Fully exit NX (every window), then start it again.
echo      These variables are read only at NX startup.
echo   2. After restarting, verify:
echo        node "%~dp0scripts\check-plugin-status.js"
echo      Expect: port 1977 listening + tool count greater than 0
echo.
echo Note: if you had set these two variables before to point elsewhere,
echo       this run has overwritten them with the paths above.
echo ================================================================

endlocal & exit /b 0
