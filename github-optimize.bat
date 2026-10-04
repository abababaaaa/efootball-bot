@echo off
setlocal enableextensions
chcp 65001 >nul
title GitHub 直连优化工具
cd /d "%~dp0"

rem ---------- 检查管理员权限（修改 hosts 必需） ----------
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] 修改 hosts 需要管理员权限，正在重新请求...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ==============================================
echo   GitHub 直连优化工具
echo   多 DNS 解析 + TCP/443 测速，选最快 IP 写入 hosts
echo ==============================================
echo.

echo [1/3] 解析 GitHub 域名并测速（约 10~30 秒，请稍候）...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0github-optimize.ps1"
echo.

echo [2/3] 刷新 DNS 缓存...
ipconfig /flushdns >nul
echo [3/3] 完成。
echo.
echo 提示：这是优化直连的网络路由，本质仍是裸连（非代理）。
echo      若完全无法访问，可能是所在网络对 GitHub 主站阻断，需借助镜像或代理。
echo.
pause