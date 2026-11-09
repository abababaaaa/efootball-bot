@echo off
rem Build vision-svc (Rust sidecar) and drop the exe into rust\bin\.
rem
rem CARGO_TARGET_DIR is forced to an ASCII path on purpose: the MinGW linker
rem cannot handle the non-ASCII project folder (B:\Efootball...), so linking
rem inside rust\target fails with "cannot find ...rcgu.o". Building into
rem %LOCALAPPDATA% and copying the exe back keeps the repo layout unchanged.
setlocal
cd /d "%~dp0"

if "%CARGO_TARGET_DIR%"=="" set CARGO_TARGET_DIR=%LOCALAPPDATA%\EfootballVision\target
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"

rem dlltool/ld/gcc live in the toolchain's self-contained dir (rustup's rust-mingw).
rem Without dlltool, crates needing import-library generation fail with
rem "error calling dlltool: program not found". No block syntax here on purpose:
rem %PATH% may contain parentheses (e.g. Chinese install dirs) and breaks if ( ).
for /f "delims=" %%S in ('rustc --print sysroot') do set "SYSROOT=%%S"
set "PATH=%PATH%;%SYSROOT%\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained"

where cargo >nul 2>&1
if errorlevel 1 (
    echo ERROR: cargo not found. Install Rust: https://rustup.rs
    exit /b 1
)

echo Building vision-svc (release) into %CARGO_TARGET_DIR% ...
cargo build --release
if errorlevel 1 (
    echo ERROR: cargo build failed.
    exit /b 1
)

if not exist "bin" mkdir bin
copy /y "%CARGO_TARGET_DIR%\release\vision-svc.exe" "bin\vision-svc.exe" >nul
if errorlevel 1 (
    echo ERROR: copy vision-svc.exe failed.
    exit /b 1
)
echo OK: rust\bin\vision-svc.exe
exit /b 0
