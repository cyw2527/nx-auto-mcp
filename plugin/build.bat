@echo off
REM ================================================================
REM build.bat -- Compile the NX managed plugin
REM
REM Usage:
REM   build.bat              -> compile
REM   build.bat clean        -> delete build output
REM   build.bat run          -> compile + deploy (calls deploy.bat)
REM
REM Environment:
REM   .NET Framework 4.8 csc.exe (ships with Windows)
REM   NX SDK DLLs (<NX_ROOT>\NXBIN\managed\) -- detected by nx-env.bat,
REM   override with the NX_ROOT environment variable.
REM
REM NOTE: keep every line in this file pure ASCII. The console decodes
REM .bat files using the OEM codepage (cp936 on Chinese Windows); UTF-8
REM bytes get mangled and can swallow the line ending, merging the next
REM line into the current one. Keep the file CRLF-terminated too.
REM ================================================================

setlocal enabledelayedexpansion

set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

REM -- NX environment detection (NX_ROOT wins, else scan %ProgramFiles%\Siemens\NX*) --
call "%~dp0nx-env.bat" || exit /b 1
set SDK=%NX_SDK%

REM Absolute output path, so `build.bat clean` from the wrong directory
REM cannot delete a stray copy somewhere else.
set OUT=%~dp0bin\managed_plugin.dll

REM -- Check compiler --
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found at: %CSC%
    echo         Install the .NET Framework 4.8 Developer Pack.
    exit /b 1
)

REM -- Check SDK --
if not exist "%SDK%\NXOpen.dll" (
    echo [ERROR] NX SDK not found at: %SDK%\NXOpen.dll
    echo         Check that NX_ROOT points at your NX install root.
    exit /b 1
)

REM -- Version notice: this project targets NX 2412 --
for %%v in ("%NX_ROOT%") do set "NXV=%%~nxv"
echo %NXV% | findstr /C:"2412" >nul
if errorlevel 1 (
    echo [WARN] Detected %NXV%, but this project targets NX 2412.
    echo [WARN] NXOpen APIs differ between versions; build errors are expected.
    echo [WARN] To point at a specific version: set NX_ROOT=D:\Siemens\NX2412
    echo.
)

REM -- Signing resource: taken from the local NX install, not redistributed --
REM This file ships with NX under the UGOPEN folder. It is an official
REM Siemens build marker and must not be re-distributed by this repo.
set RES=%NX_ROOT%\UGOPEN\NXSigningResource.res
if not exist "%RES%" (
    echo [ERROR] Signing resource not found: %RES%
    echo         It ships with NX, under the UGOPEN folder of NX_ROOT.
    echo         Check that NX_ROOT points at the NX install root.
    exit /b 1
)

if "%1"=="clean" (
    echo Cleaning...
    if exist "%OUT%" del "%OUT%"
    if exist "%~dp0bin\*.pdb" del "%~dp0bin\*.pdb"
    echo Done.
    exit /b 0
)

REM csc writes a temporary Win32 resource file into the OUTPUT directory
REM while compiling. That directory must exist beforehand: on a fresh
REM checkout (no plugin\bin\) compilation would otherwise fail with
REM   error CS1567: Error generating Win32 resource:
REM                  The system cannot find the path specified.
if not exist "%~dp0bin" mkdir "%~dp0bin"

echo ================================================================
echo NX Plugin Build
echo ================================================================
echo Compiler: %CSC%
echo SDK:      %SDK%
echo Output:   %OUT%
echo.

REM Collect all C# sources under plugin/, skipping build/junk folders.
REM The skip filter must test the path RELATIVE to this folder. Testing the
REM absolute path would silently drop EVERY source file whenever the checkout
REM itself sits under a folder whose name contains one of the keywords below
REM (e.g. a clone in D:\journal\, or a user profile named "scripts").
pushd %~dp0
set "PLUGINDIR=%~dp0"
set SRCS=
for /r %%f in (*.cs) do (
    set "REL=%%f"
    set "REL=!REL:%PLUGINDIR%=!"
    echo !REL! | findstr /i "bridge.bak scripts node_modules extract-dll journal" >nul
    if errorlevel 1 set SRCS=!SRCS! "%%f"
)

REM SDK references
set REFS=
set REFS=%REFS% /r:"%SDK%\NXOpen.dll"
set REFS=%REFS% /r:"%SDK%\NXOpen.UF.dll"
set REFS=%REFS% /r:"%SDK%\NXOpen.Utilities.dll"
set REFS=%REFS% /r:"%SDK%\NXOpenUI.dll"
set REFS=%REFS% /r:"%SDK%\ManagedLoader.dll"
set REFS=%REFS% /r:"%SDK%\Newtonsoft.Json.dll"
set REFS=%REFS% /r:%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll
REM WPF assemblies -- ContextMenuHandler.cs uses System.Windows.Point/Automation
set REFS=%REFS% /r:%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll
set REFS=%REFS% /r:%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll
set REFS=%REFS% /r:%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll
set REFS=%REFS% /r:%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll

REM Compile (NXSigningResource embedded so SignDotNet can sign it)
echo Compiling %SRCS% ...
%CSC% /target:library /out:"%OUT%" /platform:x64 /nologo %REFS% /resource:"%RES%" %SRCS%
REM Capture the compiler status BEFORE popd, which resets ERRORLEVEL.
set CSC_ERR=%ERRORLEVEL%
popd

if not "%CSC_ERR%"=="0" (
    echo.
    echo ================================================================
    echo BUILD FAILED
    echo ================================================================
    exit /b 1
)

echo.
echo ================================================================
echo BUILD SUCCESS
echo ================================================================
dir "%OUT%"

if "%1"=="run" (
    echo.
    echo Running deploy.bat deploy-only...
    call "%~dp0deploy.bat" deploy-only
    if errorlevel 1 exit /b 1
)

endlocal & exit /b 0
