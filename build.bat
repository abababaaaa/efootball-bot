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
