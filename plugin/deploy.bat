@echo off
REM ================================================================
REM deploy.bat -- Compile + sign + deploy the NX managed plugin
REM
REM Usage:
REM   deploy.bat              -> compile + deploy
REM   deploy.bat deploy-only  -> skip compile, only sign + copy
REM
REM == Single-DLL rule ==
REM The one and only deploy target is: plugin\startup\managed_plugin.dll
REM
REM Evidence: custom_dirs.dat points at this package's plugin\ folder, and
REM the NX startup syslog shows:
REM   ManagedLoader.Load: ...\startup\managed_plugin.dll
REM   Loaded assembly: managed_plugin ... from ...\startup\managed_plugin.dll
REM   DotNet author license is present. Authentication passed
REM So NX only loads from <custom_dir>\startup. Other locations
REM (plugin\application, %LOCALAPPDATA%\Siemens\NX<ver>\startup) are never read.
REM Extra copies cause dual instances and stale-code bugs.
REM Do NOT create managed_plugin.dll anywhere else.
REM
REM Signing: SignDotNet.exe is required. NX silently refuses to load an
REM unsigned DLL, with no error message. Note that SignDotNet rewrites the
REM DLL bytes, so the md5 changing after signing is normal.
REM
REM NOTE: keep every line in this file pure ASCII and CRLF-terminated.
REM See the note in build.bat for why.
REM ================================================================

setlocal enabledelayedexpansion

set BRIDGE_DIR=%~dp0
set DLL_SRC=%BRIDGE_DIR%bin\managed_plugin.dll

REM -- NX environment detection (also locates SignDotNet) --
call "%BRIDGE_DIR%nx-env.bat" || exit /b 1
set SIGNTOOL=%NX_SIGNTOOL%

if "%1"=="deploy-only" goto :deploy

REM ================================================================
REM Step 1: Compile
REM ================================================================
echo ================================================================
echo DEPLOY: Step 1/3 - Compiling...
echo ================================================================
call "%BRIDGE_DIR%build.bat"
if errorlevel 1 (
    echo [ERROR] Compilation failed. Aborting deploy.
    exit /b 1
)

:deploy
REM ================================================================
REM Step 2: Sign (required on NX2412)
REM ================================================================
echo.
echo ================================================================
echo DEPLOY: Step 2/3 - Signing with SignDotNet...
echo ================================================================
if not exist "%DLL_SRC%" (
    echo [ERROR] DLL not found: %DLL_SRC%
    echo         Run deploy.bat without "deploy-only" to compile first.
    exit /b 1
)
if exist "%SIGNTOOL%" (
    "%SIGNTOOL%" "%DLL_SRC%"
    if errorlevel 1 (
        echo [ERROR] SignDotNet signing failed. Aborting.
        exit /b 1
    )
) else (
    echo [WARN] SignDotNet.exe not found: %SIGNTOOL%
    echo        NX may refuse to load the unsigned DLL. Continuing anyway...
)

REM ================================================================
REM Step 3: Deploy -- single target: plugin\startup\
REM ================================================================
echo.
echo ================================================================
echo DEPLOY: Step 3/3 - Copying to plugin\startup\ ...
echo ================================================================
if not exist "%BRIDGE_DIR%startup" mkdir "%BRIDGE_DIR%startup"
copy /Y "%DLL_SRC%" "%BRIDGE_DIR%startup\managed_plugin.dll"
if errorlevel 1 (
    echo [ERROR] Copy failed. Is NX still running and holding the DLL?
    echo         Try: taskkill /F /IM ugraf.exe /T
    exit /b 1
)

REM Rule metadata must ship next to the DLL: RuleEngine resolves
REM rules_config.json from the plugin folder at runtime. A missing config is
REM NOT fatal (the engine falls back to its hardcoded rules) -- but that
REM fallback is silent, so warn loudly here rather than ship a package whose
REM JSON never loads. This is a data file, not a second assembly.
set "RULES_LEAF=rules"
set "RULES_SUB=%RULES_LEAF%\rules_config.json"
set "RULES_SRC=%BRIDGE_DIR%%RULES_SUB%"
set "RULES_DST=%BRIDGE_DIR%startup\%RULES_SUB%"
if not exist "%RULES_SRC%" (
    echo [WARN] Rule config not found: %RULES_SRC%
    echo        RuleEngine will fall back to its hardcoded rules.
) else (
    if not exist "%BRIDGE_DIR%startup\%RULES_LEAF%" mkdir "%BRIDGE_DIR%startup\%RULES_LEAF%"
    copy /Y "%RULES_SRC%" "%RULES_DST%"
    if errorlevel 1 (
        echo [WARN] Rule config copy failed: %RULES_DST%
        echo        RuleEngine will fall back to its hardcoded rules.
    )
)

echo.
echo ================================================================
echo DEPLOY COMPLETE
echo ================================================================
echo Source: %DLL_SRC%
echo Target: plugin\startup\managed_plugin.dll   (single-DLL rule)
echo.
echo Next: fully exit NX (taskkill /F /IM ugraf.exe /T as a fallback),
echo       then restart ugraf.exe.
echo Verify: node scripts\check-plugin-status.js
echo ================================================================

endlocal & exit /b 0
