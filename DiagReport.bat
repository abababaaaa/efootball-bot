@echo off
rem ============================================================
rem  eFootball Bot - System Diagnostic Collector (DiagReport.bat)
rem
rem  Collects OS / CPU / GPU / drivers / gamepad / event log /
rem  app log info into one folder + zip for remote analysis.
rem  READ-ONLY: changes nothing outside its own output folder.
rem
rem  NOTE: keep this file pure ASCII (same rule as build.bat),
rem  so it runs correctly under any system codepage.
rem  Ship this file next to EfootballBot.exe in publish\
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"
title eFootball Bot Diagnostic Collector

echo.
echo ==========================================================
echo  eFootball Bot diagnostic collector
echo  Read-only scan. Takes about 1-2 minutes.
echo  It creates a DiagReport_*.zip next to this script.
echo  Please send the whole zip back for analysis.
echo ==========================================================
echo.

rem ---------- locale-safe timestamp ----------
set "TS="
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "TS=%%i"
if not defined TS set "TS=run"

set "OUTDIR=%~dp0DiagReport_%TS%"
set "ZIP=%OUTDIR%.zip"
set "R=%OUTDIR%\report.txt"
set "DIAG_OUTDIR=%OUTDIR%"
set "DIAG_ZIP=%ZIP%"
set "DIAG_APPDIR=%~dp0"
set "DIAG_DX=%OUTDIR%\dxdiag.txt"

mkdir "%OUTDIR%" 2>nul
if exist "%OUTDIR%\" goto HAVEOUT
set "OUTDIR=%USERPROFILE%\Desktop\DiagReport_%TS%"
set "ZIP=%USERPROFILE%\Desktop\DiagReport_%TS%.zip"
set "R=%OUTDIR%\report.txt"
set "DIAG_OUTDIR=%OUTDIR%"
set "DIAG_ZIP=%ZIP%"
set "DIAG_DX=%OUTDIR%\dxdiag.txt"
mkdir "%OUTDIR%" 2>nul
:HAVEOUT
if exist "%OUTDIR%\" goto OUTOK
echo ERROR: cannot create output folder here. Put this script in a writable directory and run again.
pause
exit /b 1
:OUTOK

chcp 65001 >nul

> "%R%" echo eFootball Bot diagnostic report
>> "%R%" echo Generated  : %DATE% %TIME%
>> "%R%" echo Script dir : %~dp0
>> "%R%" echo note: report body is UTF-8 (chcp 65001); dxdiag.txt is kept exactly as dxdiag wrote it.
>> "%R%" echo note: this scan is read-only, nothing on the system was changed.

rem ---------- start dxdiag FIRST (slowest step, runs while we collect) ----------
echo [1/9] Starting dxdiag in background (result collected at the end)...
start "" /b dxdiag /t "%OUTDIR%\dxdiag.txt" 2>nul

call :sec "SECTION 1 - RUN ENVIRONMENT"
>> "%R%" echo ScriptDir=%~dp0
>> "%R%" echo ProcArch=%PROCESSOR_ARCHITECTURE%
>> "%R%" echo ProcId=%PROCESSOR_IDENTIFIER%
>> "%R%" ver 2>&1

call :sec "SECTION 2 - OS VERSION (registry)"
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ProductName 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v EditionID 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v DisplayVersion 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ReleaseId 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v CurrentBuildNumber 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v UBR 2>&1

call :sec "SECTION 3 - SYSTEMINFO (may take 10-30s; includes hotfixes, RAM, network, hypervisor)"
>> "%R%" systeminfo 2>&1

call :sec "SECTION 4 - HARDWARE SUMMARY (WMI)"
>> "%R%" echo [machine]
>> "%R%" powershell -NoProfile -Command "Get-CimInstance Win32_ComputerSystem | Format-List Manufacturer,Model,SystemFamily,PCSystemType,HypervisorPresent,TotalPhysicalMemory | Out-String -Width 200" 2>&1
>> "%R%" echo [cpu]
>> "%R%" powershell -NoProfile -Command "Get-CimInstance Win32_Processor | Format-List Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed | Out-String -Width 200" 2>&1
>> "%R%" echo [os / memory]
>> "%R%" powershell -NoProfile -Command "Get-CimInstance Win32_OperatingSystem | Format-List Caption,Version,BuildNumber,OSArchitecture,TotalVisibleMemorySize,FreePhysicalMemory,SizeStoredInPagingFiles | Out-String -Width 200" 2>&1

call :sec "SECTION 5 - GPU ADAPTERS (critical: hybrid graphics / driver version / WDDM)"
>> "%R%" echo [Win32_VideoController]
>> "%R%" powershell -NoProfile -Command "Get-CimInstance Win32_VideoController | Sort-Object Name | Format-List Name,VideoProcessor,AdapterCompatibility,DriverVersion,DriverDate,Status,PNPDeviceID,VideoModeDescription,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,AdapterRAM | Out-String -Width 200" 2>&1
>> "%R%" echo [registry adapter entries with VRAM (HardwareInformation.qMemorySize)]
>> "%R%" powershell -NoProfile -Command "Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue; if ($p.DriverDesc) { '{0}: {1} | VRAM={2}' -f $_.PSChildName, $p.DriverDesc, $p.'HardwareInformation.qMemorySize' } }" 2>&1
>> "%R%" echo [nvidia-smi if present]
set "NSMI="
for %%i in (nvidia-smi.exe) do set "NSMI=%%~$PATH:i"
if not defined NSMI if exist "%windir%\System32\nvidia-smi.exe" set "NSMI=%windir%\System32\nvidia-smi.exe"
if not defined NSMI if exist "%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe" set "NSMI=%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
if defined NSMI goto RUNNSMI
>> "%R%" echo nvidia-smi not found (no NVIDIA driver in PATH)
goto DONE5
:RUNNSMI
>> "%R%" "%NSMI%" --query-gpu=name,driver_version,memory.total,compute_cap --format=csv 2>&1
:DONE5

call :sec "SECTION 6 - PER-APP GPU PREFERENCE (Windows graphics preference registry)"
>> "%R%" reg query "HKCU\SOFTWARE\Microsoft\DirectX\UserGpuPreferences" 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\DirectX\UserGpuPreferences" 2>&1

call :sec "SECTION 7 - DISPLAYS AND DPI"
>> "%R%" echo [active screens]
>> "%R%" powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { '{0} | Primary={1} | Bounds={2} | WorkArea={3}' -f $_.DeviceName, $_.Primary, $_.Bounds, $_.WorkingArea }" 2>&1
>> "%R%" echo [system DPI (96 = 100 percent)]
>> "%R%" reg query "HKCU\Control Panel\Desktop\WindowMetrics" /v AppliedDPI 2>&1

call :sec "SECTION 8 - POWER PLAN"
>> "%R%" powercfg /getactivescheme 2>&1

call :sec "SECTION 9 - VIGEMBUS / HIDHIDE DRIVERS (virtual gamepad stack)"
>> "%R%" echo [sc query ViGEmBus]
>> "%R%" sc query ViGEmBus 2>&1
>> "%R%" echo [sc qc ViGEmBus]
>> "%R%" sc qc ViGEmBus 2>&1
>> "%R%" echo [sc query HidHide]
>> "%R%" sc query HidHide 2>&1
>> "%R%" echo [sc qc HidHide]
>> "%R%" sc qc HidHide 2>&1
>> "%R%" echo [driver sys file versions]
>> "%R%" powershell -NoProfile -Command "foreach ($n in @('ViGEmBus.sys','HidHide.sys','HidHideX.sys','xusb22.sys')) { $f = Join-Path $env:windir (Join-Path 'System32\drivers' $n); if (Test-Path -LiteralPath $f) { $i = Get-Item -LiteralPath $f; $v = $i.VersionInfo; '{0} | FileVersion={1} | Product={2} | Modified={3}' -f $f, $v.FileVersion, $v.ProductName, $i.LastWriteTime } else { '{0} | NOT FOUND' -f $n } }" 2>&1
>> "%R%" echo [running drivers matching vigem/hidhide/xusb]
( driverquery /v /fo csv 2>&1 | findstr /i "vigem hidhide xusb" ) >> "%R%"

call :sec "SECTION 10 - INPUT DEVICES (HID/USB present devices; look for gamepads)"
>> "%R%" powershell -NoProfile -Command "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Class -in @('HIDClass','USB','XboxPeripherals') } | Sort-Object Class | ForEach-Object { '{0} | {1} | {2}' -f $_.Class, $_.Status, $_.FriendlyName }" 2>&1

call :sec "SECTION 11 - RUNNING PROCESSES (efootball / bot / steam)"
( tasklist /fo csv 2>&1 | findstr /i "efootball steam" ) >> "%R%"

call :sec "SECTION 12 - ANTIVIRUS PRODUCTS (may interfere with capture / HID drivers)"
>> "%R%" powershell -NoProfile -Command "Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { '{0} | productState={1} | {2}' -f $_.displayName, $_.productState, $_.pathToSignedProductExe }" 2>&1

call :sec "SECTION 13 - .NET AND VC++ RUNTIMES"
>> "%R%" echo [dotnet runtimes]
>> "%R%" dotnet --list-runtimes 2>&1
>> "%R%" echo [.NET Framework 4.x release]
>> "%R%" reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2>&1
>> "%R%" echo [VC++ redistributes installed (64bit view)]
( reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Visual C++" /d 2>&1 | findstr /i "DisplayName" ) >> "%R%"
>> "%R%" echo [VC++ redistributes installed (32bit view)]
( reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Visual C++" /d 2>&1 | findstr /i "DisplayName" ) >> "%R%"

call :sec "SECTION 14 - RECENT ERRORS (7 days): display-driver resets and system/app errors"
>> "%R%" echo [TDR: display driver stopped responding (Event 4101), last 10]
>> "%R%" powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='System'; Id=4101} -MaxEvents 10 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1}' -f $_.TimeCreated, ($_.Message -replace '\s+', ' ') }" 2>&1
>> "%R%" echo [System log errors, last 25 in 7 days]
>> "%R%" powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='System'; Level=2; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 25 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1} | Id={2} | {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' ') }" 2>&1
>> "%R%" echo [Application log errors, last 25 in 7 days]
>> "%R%" powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 25 -ErrorAction SilentlyContinue | ForEach-Object { '{0} | {1} | Id={2} | {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, ($_.Message -replace '\s+', ' ') }" 2>&1

call :sec "SECTION 15 - STEAM AND GAME INSTALL LOCATION"
>> "%R%" reg query "HKCU\Software\Valve\Steam" /v SteamPath 2>&1
>> "%R%" reg query "HKLM\SOFTWARE\WOW6432Node\Valve\Steam" /v InstallPath 2>&1
>> "%R%" echo [steam libraries with eFootball presence check]
>> "%R%" powershell -NoProfile -Command "$steam = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath; 'SteamPath = ' + $steam; if ($steam) { $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'; if (Test-Path -LiteralPath $vdf) { $q = [char]34; Get-Content -LiteralPath $vdf | Where-Object { $_ -match 'path' } | ForEach-Object { $lib = ($_ -split $q)[-2]; if ($lib) { $game = Join-Path (Join-Path $lib 'common') 'eFootball'; 'Library: ' + $lib + ' | eFootball installed: ' + (Test-Path -LiteralPath $game) } } } }" 2>&1

call :sec "SECTION 16 - APP FOLDER (bot logs/config copied; onnxruntime/DirectML versions)"
if exist "%DIAG_APPDIR%logs" goto HASLOGS
>> "%R%" echo app 'logs' folder not found next to this script.
goto DONELG
:HASLOGS
mkdir "%OUTDIR%\app_logs" 2>nul
robocopy "%DIAG_APPDIR%logs" "%OUTDIR%\app_logs" *.log *.yml *.txt /S /MAXAGE:7 /MIN:64 /MAX:20971520 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
>> "%R%" echo app logs (last 7 days) copied to app_logs\
:DONELG
if exist "%DIAG_APPDIR%data" goto HASDATA
>> "%R%" echo app 'data' folder not found next to this script.
goto DONEDT
:HASDATA
mkdir "%OUTDIR%\app_data" 2>nul
robocopy "%DIAG_APPDIR%data" "%OUTDIR%\app_data" *.log *.json *.txt /S /MIN:1 /MAX:20971520 /XD tmp captures /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
>> "%R%" echo app data files (config/logs, no tmp) copied to app_data\
:DONEDT
>> "%R%" echo [ml/onnx related dll versions in app folder]
>> "%R%" powershell -NoProfile -Command "foreach ($n in @('onnxruntime.dll','onnxruntime_providers_shared.dll','onnxruntime_providers_dml.dll','DirectML.dll','ViGEmClient.dll')) { $f = Join-Path $env:DIAG_APPDIR $n; if (Test-Path -LiteralPath $f) { '{0} | FileVersion={1}' -f $n, (Get-Item -LiteralPath $f).VersionInfo.FileVersion } else { '{0} | not present' -f $n } }" 2>&1

call :sec "SECTION 17 - DXDIAG (full dxdiag.txt attached separately in this folder)"
set /a DFWAIT=0
:WAITDX
if exist "%DIAG_DX%" goto DXOK
ping -n 3 127.0.0.1 >nul
set /a DFWAIT+=1
if %DFWAIT% lss 30 goto WAITDX
>> "%R%" echo dxdiag.txt: TIMEOUT after ~90 seconds (skipped). System policy may block dxdiag.
echo [8/9] dxdiag timed out (not critical, report is still valid)
goto DXDONE
:DXOK
ping -n 6 127.0.0.1 >nul
for %%s in ("%DIAG_DX%") do >> "%R%" echo dxdiag.txt present, size: %%~zs bytes
:DXDONE

>> "%R%" echo.
>> "%R%" echo End of report.

echo [9/9] Creating zip archive...
rem NOTE: never redirect Compress-Archive output into report.txt itself -
rem its own append handle locks the file mid-zip (self-lock, seen in testing).
powershell -NoProfile -Command "try { Compress-Archive -LiteralPath $env:DIAG_OUTDIR -DestinationPath $env:DIAG_ZIP -Force; 'ZIP_OK' } catch { 'ZIP_FAIL: ' + $_.Exception.Message }"

if not exist "%ZIP%" goto NOZIP
echo.
echo ==========================================================
echo  DONE. Please send this file back for analysis:
echo  %ZIP%
echo ==========================================================
start "" "%OUTDIR%"
goto FINISH
:NOZIP
echo.
echo ==========================================================
echo  DONE, but zip creation failed.
echo  Please send the whole folder instead:
echo  %OUTDIR%
echo ==========================================================
start "" "%OUTDIR%"
:FINISH
echo.
pause
exit /b 0

rem ---------- helper: section header ----------
:sec
>> "%R%" echo.
>> "%R%" echo ============================================================
>> "%R%" echo  %~1
>> "%R%" echo ============================================================
echo   ... %~1
goto :eof
