@echo off
rem NOTE: keep this file pure ASCII - use %~dp0 only.
rem
rem Child-session BETA build = Release single-file EfootballBotBeta.exe deployed into publish-beta\.
rem The stable build (build.bat -> publish\EfootballBot.exe) is not touched at all:
rem   - different assembly/exe name  -> the two can run at the same time
rem   - different folder / data dir  -> beta settings, logs and captures are isolated
rem   - ChildSessionBeta=true        -> only this build references the child-session library
cd /d "%~dp0"

set STAGE=%~dp0build_local\publish-beta-stage

echo [1/3] dotnet publish (ChildSessionBeta, Release, single-file exe)...
dotnet publish src\EfootballBot\EfootballBot.csproj -c Release --nologo -v q -o "%STAGE%" -p:ChildSessionBeta=true
if errorlevel 1 goto :fail
if not exist "%STAGE%\EfootballBotBeta.exe" (
    echo ERROR: publish output not found: %STAGE%\EfootballBotBeta.exe
    goto :fail
)

echo [2/3] Deploy to publish-beta\ ...
if not exist "%~dp0publish-beta" mkdir "%~dp0publish-beta"
if not exist "%~dp0publish-beta\EfootballBotBeta.exe" goto :deploy
copy /y "%STAGE%\EfootballBotBeta.exe" "%~dp0publish-beta\EfootballBotBeta.exe" >nul 2>&1
if not errorlevel 1 goto :deployed

echo Target exe is locked - the beta is probably running. Renaming old exe aside...
set OLDBETA=EfootballBotBeta.old-%RANDOM%%RANDOM%.exe
ren "%~dp0publish-beta\EfootballBotBeta.exe" "%OLDBETA%"
if errorlevel 1 (
    echo ERROR: cannot rename the locked exe. Stop the beta and run this script again.
    goto :fail
)

:deploy
copy /y "%STAGE%\EfootballBotBeta.exe" "%~dp0publish-beta\EfootballBotBeta.exe" >nul 2>&1
if errorlevel 1 (
    echo ERROR: copy to publish-beta\ failed even after rename.
    goto :fail
)
rem Virtual display driver payload (WHQL signed, amyuni.com official package)
robocopy "%~dp0build_local\virtualdisplay\usbmmidd_v2" "%~dp0publish-beta\drivers\virtualdisplay" /e /nfl /ndl /njh /njs /np >nul 2>&1


:deployed
if defined OLDBETA echo Old beta exe kept as publish-beta\%OLDBETA%; delete it after the beta exits.
echo [3/3] Done. publish-beta\EfootballBotBeta.exe is the new virtual-display test build.
echo Stable build in publish\ was not touched.
echo.
pause
exit /b 0

:fail
echo.
echo Beta build/deploy FAILED.
pause
exit /b 1
