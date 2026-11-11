@echo off
rem 构建 xinput1_3.dll 代理。需要 VS 2019/2022 Build Tools（x64 Native Tools）。
rem 本地没有 MSVC 时走 CI：.github/workflows/xinput-proxy.yml（windows-latest 自带 MSVC）
setlocal
cl /nologo /O2 /MT /LD /W4 /GS- /D_CRT_SECURE_NO_WARNINGS ^
   xinput1_3.c /link /DEF:xinput1_3.def /OUT:xinput1_3.dll kernel32.lib user32.lib
if errorlevel 1 exit /b 1
echo built: %CD%\xinput1_3.dll
