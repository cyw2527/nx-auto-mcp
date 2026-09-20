@echo off
REM ================================================================
REM nx-env.bat -- shared NX environment detection
REM   called by build.bat / deploy.bat / launch.bat
REM
REM Priority: NX_ROOT environment variable, else scan %ProgramFiles%\Siemens\NX*
REM Products: NX_ROOT / NX_SDK / NX_UGRAF / NX_SIGNTOOL
REM
REM Usage: call "%~dp0nx-env.bat"  ||  exit /b 1
REM NOTE:  this file deliberately does NOT use setlocal -- the variables
REM        must bubble up to the caller.
REM
REM NOTE: keep every line in this file pure ASCII and CRLF-terminated.
REM        See the note in build.bat for why.
REM ================================================================

if not "%NX_ROOT%"=="" goto :derive

REM dir returns entries in alphabetical order, so the last one is the
REM highest version (NX2412 > NX2306 > NX2206).
set "NX_ROOT="
for /d %%d in ("%ProgramFiles%\Siemens\NX*") do set "NX_ROOT=%%~fd"

if "%NX_ROOT%"=="" (
    echo [ERROR] No NX installation found.
    echo         Set the NX_ROOT environment variable to your NX install root, e.g.
    echo           set NX_ROOT=C:\Program Files\Siemens\NX2412
    exit /b 1
)

:derive
set "NX_SDK=%NX_ROOT%\NXBIN\managed"
set "NX_UGRAF=%NX_ROOT%\NXBIN\ugraf.exe"
set "NX_SIGNTOOL=%NX_ROOT%\NXBIN\SignDotNet.exe"

if not exist "%NX_SDK%\NXOpen.dll" (
    echo [ERROR] NX SDK not found: %NX_SDK%\NXOpen.dll
    echo         Check that NX_ROOT is correct and that this version ships
    echo         the managed API.
    exit /b 1
)

exit /b 0
