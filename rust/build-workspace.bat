@echo off
rem Build the Rust workspace (vision-svc + shared crates + bot-ui Slint shell).
rem
rem Two toolchain paths:
rem   A) MSVC (preferred, required for Slint): C:\BuildTools + Windows SDK
rem   B) GNU fallback (shared crates only, no Slint): rust-lld linker + ASCII target
rem
rem On this machine the user profile has CJK chars (C:\Users\笔记本), which breaks
rem MinGW dlltool/ld for crates that generate import libs (windows-* used by Slint).
rem That is why Slint needs the MSVC toolchain.
setlocal
cd /d "%~dp0"

if "%CARGO_TARGET_DIR%"=="" set "CARGO_TARGET_DIR=C:\cargo-target\workspace-target"
if not exist "%CARGO_TARGET_DIR%" mkdir "%CARGO_TARGET_DIR%" >nul 2>&1

if exist "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat" goto :msvc
goto :gnu

:msvc
echo === Using MSVC toolchain (C:\BuildTools) ===
call "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
set "CARGO_BUILD_TARGET=x86_64-pc-windows-msvc"
goto :run

:gnu
echo === Using GNU fallback (rust-lld, shared crates only) ===
set "TC=%USERPROFILE%\.rustup\toolchains\stable-x86_64-pc-windows-gnu"
set "LLD=%TC%\lib\rustlib\x86_64-pc-windows-gnu\bin\rust-lld.exe"
set "PATH=%TC%\bin;%PATH%"
set "RUSTFLAGS=-C linker=%LLD%"
set "CARGO_BUILD_TARGET=x86_64-pc-windows-gnu"

:run
echo === cargo check ===
cargo check --workspace
if errorlevel 1 goto :fail
echo === cargo test ===
cargo test --workspace
if errorlevel 1 goto :fail
echo === OK ===
exit /b 0

:fail
echo ERROR: build failed.
exit /b 1
