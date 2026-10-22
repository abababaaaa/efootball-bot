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
if defined OLDBAK echo Old exe kept as publish\%OLDBAK% if it was running; delete it after the bot exits.
echo [3/3] Done. publish\EfootballBot.exe is now the new build.
echo.
pause
exit /b 0

:fail
echo.
echo Build/deploy FAILED.
pause
exit /b 1
