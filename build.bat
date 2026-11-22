@echo off
rem NOTE: keep this file pure ASCII (no hardcoded Chinese path) - use %~dp0 so the
rem script works no matter where the project folder lives or how the file is encoded.
rem
rem Local build = Release single-file publish deployed straight into publish\.
rem If publish\EfootballBot.exe is locked (bot currently running), the old exe is
rem renamed aside (allowed while running, the old process keeps working) and the
rem new build takes its place. User data (data\ logs\ captures\) is never touched.
cd /d "%~dp0"

set STAGE=%~dp0build_local\publish-stage

echo [1/3] dotnet publish (Release, single-file exe)...
dotnet publish src\EfootballBot\EfootballBot.csproj -c Release --nologo -v q -o "%STAGE%"
if errorlevel 1 goto :fail
if not exist "%STAGE%\EfootballBot.exe" (
    echo ERROR: publish output not found: %STAGE%\EfootballBot.exe
    goto :fail
)

echo [2/3] Deploy to publish\ ...
if not exist "%~dp0publish\EfootballBot.exe" goto :deploy
copy /y "%STAGE%\EfootballBot.exe" "%~dp0publish\EfootballBot.exe" >nul 2>&1
if not errorlevel 1 goto :deployed

echo Target exe is locked - the bot is probably running. Renaming old exe aside...
set OLDBAK=EfootballBot.old-%RANDOM%%RANDOM%.exe
ren "%~dp0publish\EfootballBot.exe" "%OLDBAK%"
if errorlevel 1 (
    echo ERROR: cannot rename the locked exe. Stop the bot and run this script again.
    goto :fail
)

:deploy
copy /y "%STAGE%\EfootballBot.exe" "%~dp0publish\EfootballBot.exe" >nul 2>&1
if errorlevel 1 (
    echo ERROR: copy to publish\ failed even after rename.
    goto :fail
)

:deployed
rem Verify the deployed exe is the same size as the freshly published one.
rem 2026-11-21: an interrupted copy left publish\EfootballBot.exe at 124,518,400 B
rem (exactly 1900 x 64 KiB, a clean prefix of the 131,328,209 B build). The .NET
rem single-file host then aborts with "Failure processing application bundle ...
rem Arithmetic overflow" before any app code runs, so double-clicking the bot did
rem nothing: no window, no gui-error.log, only an Application event 1023 from
rem ".NET Runtime". Never leave a size-mismatched exe in publish\ - fail loudly.
for %%F in ("%STAGE%\EfootballBot.exe") do set "SRC_EXE_SIZE=%%~zF"
for %%F in ("%~dp0publish\EfootballBot.exe") do set "DST_EXE_SIZE=%%~zF"
if not "%SRC_EXE_SIZE%"=="%DST_EXE_SIZE%" (
    echo ERROR: publish\EfootballBot.exe is %DST_EXE_SIZE% bytes but the build is %SRC_EXE_SIZE% bytes.
    echo        That copy was truncated; the bot would fail to start with a bundle error.
    echo        Re-run this script; make sure nothing is writing publish\EfootballBot.exe.
    goto :fail
)

rem Prune old backups: keep only the single newest EfootballBot.old-*.exe.
rem Each backup is ~120-140MB, and rename-aside leaves one per build while the
rem bot is running; without this the folder grows by >100MB on every build.
rem Pure ASCII; dir /o-d sorts newest first, so skip=1 = "all but the newest".
rem (No delayed expansion needed, so no setlocal/enabledelayedexpansion change.)
for /f "skip=1 delims=" %%F in ('dir /b /o-d "%~dp0publish\EfootballBot.old-*.exe" 2^>nul') do (
    del /f /q "%~dp0publish\%%F" >nul 2>&1
)
if defined OLDBAK echo Old build kept as publish\%OLDBAK% (only the newest backup is retained).
rem --- Rust vision sidecar (optional, A/B plan) ---------------------------------
rem Build vision-svc.exe when a Rust toolchain is present, and drop it next to the
rem main exe so the "VisionSidecar.Enabled" switch can find it. Silent no-op when
rem cargo is missing: the bot then just keeps using the in-process capture chain.
set SIDECAR=%~dp0rust\bin\vision-svc.exe
echo [2b/3] Rust vision sidecar (optional)...
if not exist "%~dp0rust\build.bat" goto :sidecar_done
call "%~dp0rust\build.bat"
:sidecar_done
if exist "%SIDECAR%" (
    copy /y "%SIDECAR%" "%~dp0publish\vision-svc.exe" >nul 2>&1
    echo vision-svc.exe deployed to publish\.
) else (
    echo NOTE: vision-svc.exe not built - VisionSidecar stays disabled.
)

echo [3/3] Done. publish\EfootballBot.exe is now the new build.
echo.
pause
exit /b 0

:fail
echo.
echo Build/deploy FAILED.
pause
exit /b 1
