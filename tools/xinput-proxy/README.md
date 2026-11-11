# tools/xinput-proxy —— 目标游戏进程内的 XInput1_3 代理（**测试内容**）

> 状态：**v6.5 起正式构建可用**（设置里可选「进程内代理（不占设备）」，默认仍是 ViGEmBus）。
> `PadBackendInfo.ProxyBackendEnabled` 保留为编译期急停位。投放脚本 `install.ps1` 不随产品分发，
> 需显式 `-Install -ConfirmProxy` 才会写游戏目录。

只对**目标游戏进程**生效的虚拟手柄通道：Worker 写共享内存，代理把 XInput slot 0 换成这份状态；
系统 XInput、真实手柄、前台其他应用完全不受影响。

设计文档：`docs/plans/2026-11-11-background-pad-xinput-proxy.md`

## 为什么是 xinput1_3（不是 1_4）

实测 `eFootball.exe`（Steam 国际服）静态导入 `XINPUT1_3.dll`，且**只按序号**：

```
XINPUT1_3.dll ILT: 0x8000000000000002 (XInputGetState), 0x8000000000000003 (XInputSetState)
```

全文没有 `XInputGetState` 名称字符串 → 按名字导出/按名字 hook 都行不通，必须用 `.def` 锁序号。

## 构建

本地没有 MSVC 时走 CI（`.github/workflows/xinput-proxy.yml`，windows-latest 自带 MSVC）：

```bat
build.bat                 :: 需要 x64 Native Tools Command Prompt
```

产物：`xinput1_3.dll`（x64，约 30–60 KB）+ `xinput_proxy.ini`。

## 安装（只投目标目录，不动 System32）

```
<游戏安装>\...\Binaries\Win64\xinput1_3.dll
<游戏安装>\...\Binaries\Win64\xinput_proxy.ini
```

`xinput_proxy.ini` 关键项：

| 键 | 含义 |
|---|---|
| `TargetProcess` | 进程名白名单，分号分隔；不命中则整体透传 |
| `TargetId` | 1=国际服（targetMask bit0）/ 2=国服（bit1），必须与 Worker 写侧一致 |
| `SharedName` | 共享内存名，默认 `Local\EfootballBot.Pad.v1` |
| `Enable` | 0 = 永远透传（对照实验） |
| `CounterHook` | 1 = 每秒复检并抢回被 Steam overlay 改写的导出入口 |
| `HeartbeatTimeoutMs` | Worker 心跳超时，超时立即透传（fail-safe） |
| `CallerScope` | 1（默认）= 只给**游戏自己目录里**的模块喂合成状态；Steam overlay（`gameoverlayrenderer64.dll`）等注入模块的轮询一律透传。0 = 关闭该门禁（仅排障用） |

## 卸载

删除这两个文件即可，无注册表、无服务、无驱动残留。

## 自检

1. 启动游戏后看同目录 `xinput_proxy.log`：应有"代理已加载：进程=命中"；
2. Worker 侧读 `proxyCalls` / `proxySeenMs`：>0 且持续刷新 = 游戏确实在读合成状态；
3. 打开 `joy.cpl`：不应出现任何多出来的手柄；
4. 切到前台记事本，让 bot 按键：系统层无任何反应。

## 已知坑

- **Steam overlay**：`gameoverlayrenderer.dll` 会把导出的 `XInputGetState` 改写成 JMP 到它自己的处理器，
  绕过代理 → `CounterHook` 必须开；日志里出现"反 hook：... 已指回本代理"即生效。
- **序号错位**：改 `.def` 时不要动 `@2..@8`，否则游戏加载阶段直接退出。
- **调试**：代理在 ini 缺失时透明转发，不会破坏游戏启动；先用 `Enable=0` 确认能正常进游戏，再开接管。

## 本机无 MSVC 也能构建/验证

本仓库自带便携 gcc（`tools/_toolchain/w64devkit`，仅构建用，不入发布）：

```powershell
& tools\_toolchain\w64devkit\w64devkit\bin\gcc.exe -shared -O2 -Wall `
  -o tools\xinput-proxy\xinput1_3.dll `
  tools\xinput-proxy\xinput1_3.c tools\xinput-proxy\xinput1_3.def -lkernel32 -luser32 -static-libgcc

powershell -File tools\xinput-proxy\verify-dll.ps1        # 序号校验（PASS/FAIL）
powershell -File tools\xinput-proxy\tests\run-selftest.ps1   # 13 项功能自测
powershell -File tools\xinput-proxy\install.ps1 -Install -ConfirmProxy   # 投放（缺省 DryRun）
```

2026-11-11 实测：gcc 16.2 出的 DLL 与 MSVC 语义一致（导出序号 #2..#8 + #100..#103），
自测 13/13 通过；`.def` 的显式序号 GNU ld 同样尊重。

## 文件

| 文件 | 说明 |
|---|---|
| `xinput1_3.c` | 代理实现（转发 + 合成 + 反 hook + 日志） |
| `xinput1_3.def` | 导出/序号锁定 |
| `pad_shared.h` | 共享内存 ABI v1（与 C# 写侧一致） |
| `csharp/SharedPadWriter.cs` | Worker 侧写侧参考实现（seqlock） |
| `xinput_proxy.ini.example` | 配置样例 |
| `build.bat` | 本地构建（MSVC） |
| `verify-dll.ps1` | 导出序号校验（不需要 dumpbin） |
| `install.ps1` | 投放/卸载到游戏目录（缺省 DryRun） |
| `tests/selftest.c` | 功能自测（伪造 Worker 共享内存 + LoadLibrary 按序号调用） |
| `tests/run-selftest.ps1` | 构建并运行自测 |