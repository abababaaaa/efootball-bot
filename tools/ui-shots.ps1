param([string]$Exe, [string]$OutDir, [string]$Pages, [int]$Wait = 8, [int]$WinW = 1280, [int]$WinH = 800)
$pageList = $Pages -split ","
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W32 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static List<IntPtr> WindowsOf(int pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, p) => {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp == (uint)pid && IsWindowVisible(h)) {
        RECT r; if (GetWindowRect(h, out r)) { if ((r.R-r.L) > 300 && (r.B-r.T) > 200) list.Add(h); }
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static IntPtr Largest(int pid) {
    IntPtr best = IntPtr.Zero; long area = 0;
    foreach (var h in WindowsOf(pid)) {
      RECT r; GetWindowRect(h, out r);
      long a = (long)(r.R-r.L) * (r.B-r.T);
      if (a > area) { area = a; best = h; }
    }
    return best;
  }
}
"@
[W32]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($pg in $pageList) {
  Get-Process EfootballBot -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 900
  if ($pg -eq "default") { $p = Start-Process $Exe -PassThru }
  else { $p = Start-Process $Exe -ArgumentList @("--page", $pg) -PassThru }
  Start-Sleep -Seconds $Wait
  # 取该进程最大的可见窗口（MainWindowHandle 在多窗口/托盘场景下会指错），
  # 然后强制摆到固定尺寸，保证截图可比。
  $h = [W32]::Largest($p.Id)
  if ($h -eq [IntPtr]::Zero) { "NO_WINDOW for " + $pg; continue }
  [W32]::ShowWindow($h, 9) | Out-Null
  # 先把窗口置顶再截：桌面上常有其它应用（浏览器/直播客户端）压在上面，
  # 截图会把它们的像素当成"我们的窗口"，看起来像半张界面/透出桌面。
  $HWND_TOPMOST = [IntPtr](-1)
  $HWND_NOTOPMOST = [IntPtr](-2)
  [W32]::SetWindowPos($h, $HWND_TOPMOST, 40, 40, $WinW, $WinH, 0x0040) | Out-Null
  Start-Sleep -Milliseconds 2600   # 等 WPF 重新布局完再截（否则会截到旧尺寸的半张界面）
  $r = New-Object W32+RECT
  [W32]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap($w, $hh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
  $g.Dispose()
  $name = ($pg -replace "[:\\/]","-")
  if ($name -eq "default") { $name = "home" } else { $name = "page-" + $name }
  $out = Join-Path $OutDir ($name + ".png")
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  [W32]::SetWindowPos($h, $HWND_NOTOPMOST, 0, 0, 0, 0, 0x0001) | Out-Null
  "shot " + $out + "  (" + $w + "x" + $hh + ")"
}
Get-Process EfootballBot -ErrorAction SilentlyContinue | Stop-Process -Force
"done"
