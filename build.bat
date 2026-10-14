@echo off
rem NOTE: keep this file pure ASCII (no hardcoded Chinese path) - use %~dp0 so the
rem script works no matter where the project folder lives or how the file is encoded.
cd /d "%~dp0"
dotnet build src\EfootballBot\EfootballBot.csproj -c Debug --nologo -v q
echo.
echo Build exit code: %ERRORLEVEL%
pause
