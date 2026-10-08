@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ============================================================
rem  NetCheck (C#/WPF) - one-click publish
rem
rem  Double-click this file, or run it from any directory:
rem
rem      build.bat               both editions
rem      build.bat standalone    standalone edition only
rem      build.bat lite          lite edition only
rem      build.bat help          show usage
rem
rem  Output (under NetCheckCSharp\publish\), one single-file exe each:
rem      NetCheck-standalone\NetCheck.exe   .NET runtime bundled in, runs
rem                                         anywhere, ~63 MB
rem      NetCheck-lite\NetCheck.exe         needs the .NET 8 Desktop
rem                                         Runtime on the target, ~0.5 MB
rem
rem  To hand one to someone else, just send the exe. Nothing else is needed:
rem  publish is single-file, so the folder contains exactly one file.
rem
rem  WHY THIS FILE IS PURE ASCII
rem      cmd.exe parses a .bat file using the active OEM code page, and it
rem      re-reads the file by byte offset as it runs. Non-ASCII text here
rem      turns into mojibake on a machine with a different locale, and a
rem      stray byte in the wrong place can stop the script from running at
rem      all. Keeping every byte in ASCII is the only encoding-proof option
rem      for a .bat file. The Chinese-language equivalent with the same
rem      behaviour is NetCheckCSharp\build.py.
rem
rem  WHY ONLY publish\ GETS DELETED
rem      bin\ and obj\ are MSBuild's own scratch space; dotnet publish
rem      recreates whatever it needs. Deleting them breaks the e2e and
rem      screenshot scripts, which look for bin\Release\NetCheck.exe, and a
rem      large delete can trip environment-level delete protection. So only
rem      the folder that would otherwise keep a stale copy of the app goes.
rem ============================================================

set "APP_DIR=%~dp0NetCheckCSharp"
set "PROJECT=%APP_DIR%\NetCheck.csproj"
set "OUT_ROOT=%APP_DIR%\publish"
set "EXE_NAME=NetCheck.exe"

rem ------------------------------------------------------------
rem  Parse arguments
rem ------------------------------------------------------------
set "ARG=%~1"

if /i "%ARG%"=="help"   goto :usage
if /i "%ARG%"=="-h"     goto :usage
if /i "%ARG%"=="--help" goto :usage
if /i "%ARG%"=="/?"     goto :usage

set "DO_STANDALONE="
set "DO_LITE="
if "%ARG%"=="" (
    set "DO_STANDALONE=1"
    set "DO_LITE=1"
)
if /i "%ARG%"=="all" (
    set "DO_STANDALONE=1"
    set "DO_LITE=1"
)
if /i "%ARG%"=="standalone" set "DO_STANDALONE=1"
if /i "%ARG%"=="lite"       set "DO_LITE=1"

if not defined DO_STANDALONE if not defined DO_LITE (
    echo [ERROR] Unknown option: %ARG%
    echo         Run "build.bat help" for usage.
    goto :fail
)

rem ------------------------------------------------------------
rem  Banner
rem ------------------------------------------------------------
echo ============================================================
echo   NetCheck (C#/WPF) - publish
echo ============================================================
echo.

if not exist "%PROJECT%" (
    echo [ERROR] Project file not found:
    echo         %PROJECT%
    goto :fail
)

rem ------------------------------------------------------------
rem  [1/3] .NET SDK
rem ------------------------------------------------------------
echo [1/3] Checking .NET SDK ...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo       [ERROR] "dotnet" is not on PATH.
    echo               Install the .NET 8 SDK:
    echo               https://dotnet.microsoft.com/download
    goto :fail
)
set "SDK_VER="
for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "SDK_VER=%%v"
if not defined SDK_VER set "SDK_VER=unknown"
echo       found: %SDK_VER%

rem ------------------------------------------------------------
rem  [2/3] Clean old output
rem ------------------------------------------------------------
echo [2/3] Cleaning old output ...
if exist "%OUT_ROOT%" rmdir /s /q "%OUT_ROOT%" 2>nul
if exist "%OUT_ROOT%" (
    echo       [WARN] could not fully remove publish\
    echo              Old files may remain; publish overwrites them anyway.
    echo              If publishing fails, close any running %EXE_NAME% and retry.
) else (
    echo       done
)

rem ------------------------------------------------------------
rem  [3/3] Publish
rem ------------------------------------------------------------
echo [3/3] Publishing (the first run takes 1-2 minutes) ...

set "FAILED="
set "OK_STANDALONE="
set "OK_LITE="
set "SZ_STANDALONE="
set "SZ_LITE="

if defined DO_STANDALONE (
    call :publish_one standalone "NetCheck-standalone" "standalone - no runtime needed"
    if errorlevel 1 set "FAILED=1"
)
if defined DO_LITE (
    call :publish_one lite "NetCheck-lite" "lite - needs .NET 8 Desktop Runtime"
    if errorlevel 1 set "FAILED=1"
)

rem ------------------------------------------------------------
rem  Summary
rem ------------------------------------------------------------
echo.
echo ============================================================
if defined FAILED (
    echo   RESULT: finished WITH ERRORS - see messages above
) else (
    echo   RESULT: OK
)
echo ============================================================
if defined SZ_STANDALONE echo   standalone   !SZ_STANDALONE! MB   -^> publish\NetCheck-standalone
if defined SZ_LITE       echo   lite         !SZ_LITE! MB   -^> publish\NetCheck-lite
echo.
echo   Each folder holds a single self-contained .exe - just send that file.
echo   If you do not know whether the other machine has the .NET 8 Desktop
echo   Runtime installed, send the standalone edition.
echo.

if defined FAILED (
    echo Publish finished with errors.
    call :hold
    exit /b 1
)

echo Publish finished successfully.
call :hold
exit /b 0


rem ============================================================
rem  :hold - keep the window open when double-clicked, but stay
rem          non-interactive when run from a script.
rem          Set NETCHECK_NO_PAUSE=1 to skip the prompt.
rem ============================================================
:hold
if not defined NETCHECK_NO_PAUSE pause
exit /b 0


rem ============================================================
rem  :publish_one  <key> <folder-name> <label>
rem    Runs dotnet publish into publish\<folder-name> and records the
rem    resulting folder size in SZ_<KEY> / success in OK_<KEY>.
rem ============================================================
:publish_one
set "PKEY=%~1"
set "PDIR=%~2"
set "PLABEL=%~3"
set "POUT=%OUT_ROOT%\%PDIR%"

echo.
echo ------------------------------------------------------------
echo   Publishing: %PLABEL%
echo ------------------------------------------------------------

set "EXTRA="
if /i "%PKEY%"=="standalone" set "EXTRA=--self-contained true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true"
if /i "%PKEY%"=="lite"       set "EXTRA=--self-contained false"

echo   $ dotnet publish NetCheck.csproj -c Release -r win-x64 -p:PublishSingleFile=true !EXTRA!
echo.

rem  NOTE: inside these blocks every variable is expanded with !name! rather
rem  than %name%. A %name% is substituted while the block is still being
rem  parsed, so a value containing a ')' would close the block early and the
rem  lines after it would run unconditionally. Delayed expansion happens
rem  after parsing, so it cannot break the block structure.
dotnet publish "%PROJECT%" -c Release -r win-x64 -p:PublishSingleFile=true -o "%POUT%" !EXTRA!
if errorlevel 1 (
    echo.
    echo   [ERROR] publish failed: !PLABEL!
    exit /b 1
)

if not exist "!POUT!\%EXE_NAME%" (
    echo   [ERROR] %EXE_NAME% not found in !POUT!
    exit /b 1
)

rem  Build the full path into a variable first. Passing
rem  "!POUT!\%EXE_NAME!" straight to call does not work: when a %VAR% is
rem  immediately followed by a '!', the percent sign is swallowed and the
rem  argument arrives as the literal text "EXE_NAME" instead of the value.
rem  Assigning it on its own line (where the %VAR% ends at a quote) is fine.
set "PEXE=!POUT!\%EXE_NAME%"
call :exe_size_mb "!PEXE!" PSIZE
set "SZ_%PKEY%=%PSIZE%"
set "OK_%PKEY%=1"

echo.
echo   OK -^> %POUT%\%EXE_NAME%
echo        size: %PSIZE% MB
exit /b 0


rem ============================================================
rem  :exe_size_mb  <file> <result-var>  - size of the published exe,
rem  rounded to one decimal, 0 on any failure.
rem
rem  Get-Item is used directly rather than piping Get-ChildItem into
rem  Measure-Object. A '|' inside for /f's backticks has to be written as
rem  '^|', and the escaped form does not survive the trip into PowerShell -
rem  the pipeline silently returns nothing and the size comes out as 0.
rem ============================================================
:exe_size_mb
set "_mb=0"
for /f "usebackq delims=" %%s in (`powershell -NoProfile -Command "$x=Get-Item -LiteralPath '%~1' -ErrorAction SilentlyContinue; if($x){[math]::Round($x.Length/1MB,1)}else{0}"`) do set "_mb=%%s"
set "%~2=%_mb%"
exit /b 0


rem ============================================================
rem  :usage
rem ============================================================
:usage
echo NetCheck publish script
echo.
echo   build.bat               both editions
echo   build.bat standalone    standalone edition only
echo   build.bat lite          lite edition only
echo   build.bat help          this text
echo.
echo Editions:
echo   standalone   .NET runtime bundled in. Runs anywhere on 64-bit
echo                Windows with no install. ~63 MB.
echo   lite         Program only. Needs the .NET 8 Desktop Runtime,
echo                otherwise double-clicking does nothing. ~0.5 MB.
echo.
echo Output goes to NetCheckCSharp\publish\, one single-file exe per edition.
echo.
call :hold
exit /b 0


rem ============================================================
:fail
echo.
echo Publish aborted.
call :hold
exit /b 1
