# rust/ workspace — Rust 重构测试版（分支 rust-refactor/test）

## 最终交付（UI 复刻 + 单 exe + 无命令行）

**运行**：`rust\bin\eFootballBot-Rust.exe`（300 MB，双击即开，**不需要任何 DLL**，**没有命令行黑框**）。

本次按用户要求重做：
- **忠实复刻正式版 UI**：1260×780、顶栏 logo + 「eFootball 挂机助手 / 自动化控制台」、4 分区 tab、左栏 196px（国服/国际服/窗口防卡死 + 工具 7 项 + 主题）、首页 hero「把比赛跑完，剩下的交给它」+ 两列模块目录（国际服 4 项/国服 3 项+工具 2 项，真实文案与描述）+ 页脚 QQ 群 1128646247。文案全部从 `MainWindow.xaml` 导出。
- **去命令行**：三个 bin 全部加 `#![cfg_attr(windows, windows_subsystem = "windows")]`。窗口枚举验证：进程只有 `Window Class`（主窗）和 `Winit Thread Event Target`（winit 内部消息窗），无 `ConsoleWindowClass`。
- **单 exe**：PE import 只依赖 Windows 系统 DLL（kernel32/user32/dwrite/opengl32 等），无 libgcc/libwinpthread。已实测把 76 个 DLL 全部移走后 exe 照常启动。

截图：`rust\bin\preview-ui.png`（用户可用看图工具打开核对）。

> 下面 S0–S3 是过程记录；最终形态以上述为准。

## 现状（S0 完成）

在原 `vision-svc` 单个 crate 的 workspace 上扩成 5 个成员：

```
rust/
├── Cargo.toml              workspace + 依赖方向约定
├── build-workspace.bat     本机可用的一键 check + test（见下方 caveat）
├── WORKSPACE.md            本文
├── vision-svc/             原 Rust 视觉 sidecar（未改动）
├── crates/
│   ├── bot-ipc/            GUI↔Worker 管道协议（JSON Lines），对齐 C# PipeProtocol.Msg
│   ├── bot-state/          模块清单 + 导航深链，从 C# ModuleIds/NavRoute 导出
│   ├── bot-theme/          两世界×三亮度主题 token，build.rs 从 Tokens.xaml 生成
│   └── bot-ui/             未来 Slint 主壳（S0 占位，验证共享层接线）
└── crates/bot-ui/tests/arch_guard.rs   Rust 版架构守卫（4 条规则）
```

## 依赖方向（CI 强制，见 arch_guard.rs）

```
bot-theme  ← 无依赖（纯常量；Slint/gpui 共用）
bot-ipc    ← serde/serde_json（协议层）
bot-state  ← bot-ipc（模块/导航；不认识 UI）
bot-ui     ← 三者（唯一允许装 Slint 的 crate）
vision-svc ← 独立 sidecar（自带 proto.rs，不依赖上面任何 crate）
```

规则：一个面板只属于一个 UI 框架；跨 crate 只走消息/共享内存，不共享 GPU 设备或 HWND。

## 本机构建 caveat（必读）

本机 Rust 是 **GNU 工具链**，用户目录含中文（`C:\Users\笔记本`）。三处坑，已逐一解决：

1. **链接器**：系统 mingw-gcc 会把中文 sysroot 参数搞坏 → 用 rustc 自带的 `rust-lld`（能正常读中文路径的 rlib）。
2. **dlltool/as**（Slint 的 `windows-*` 依赖需要）：rustup self-contained 只有 dlltool、缺 as → 下载 winlibs 便携 MinGW 到纯 ASCII 路径 `C:\mingw64-portable\mingw64\bin`，提供完整的 gcc/as/dlltool。
3. **target 目录**：`%LOCALAPPDATA%` 也含中文 → `C:\cargo-target\workspace-target`。

最终可用组合（两个工具链各取所长）：

```powershell
$tc = "$env:USERPROFILE\.rustup\toolchains\stable-x86_64-pc-windows-gnu"
$lld = "$tc\lib\rustlib\x86_64-pc-windows-gnu\bin\rust-lld.exe"
$mingw = 'C:\mingw64-portable\mingw64\bin'
$env:CARGO_TARGET_DIR='C:\cargo-target\workspace-target'
$env:PATH="$mingw;$tc\bin;$env:PATH"   # as/dlltool 来自便携 MinGW
$env:RUSTFLAGS="-C linker=$lld"         # 链接用 rust-lld
& "$tc\bin\cargo.exe" build -p bot-ui
```

产物 `bot-ui.exe`（约 273 MB，含 Slint 静态资源）在 `C:\cargo-target\workspace-target\debug`，已拷到 `rust\bin\bot-ui-test.exe` 并附带 MinGW 运行 DLL（`rust\bin\*.dll`）。

`cargo config`（`~/.cargo/config.toml`）已切到 `rsproxy-sparse` 镜像解决 crates.io SSL 吊销失败。

**当前结果：18 tests passed，0 failed，0 warnings；bot-ui Slint 日志页编译成功并已启动。**

永久解法（可选）：装 MSVC toolchain（VS Build Tools + Win11 SDK），之后无需上述 workaround。本机试装时 bootstrapper 走 clash 代理卡住，已放弃。

## 单一真源

| Rust 侧 | C# 侧来源 |
|---|---|
| `bot-ipc::Msg` 字段 | `src/EfootballBot/Ipc/PipeProtocol.cs` |
| `bot-state::ModuleIds` / `ALL_MODULES` | `Core/Bot/Modules/ModuleIds.cs`（12 个 id） |
| `bot-state::NavMode` / `NavKey` | `Shell/NavRoute.cs`（9 页 / 13 别名） |
| `bot-theme` token | `Themes/Tokens.xaml`（build.rs 自动生成）+ `Core/UI/ThemePalette.cs`（世界派生逻辑） |

改 C# 侧这些文件时，Rust 侧单测会点名漂移（`count_matches_csharp_catalog` / `alias_count` / `tokens_generated_from_xaml`）。

## S1 完成内容（日志页）

- `bot-ui` 已引入 `slint` 1.19.1 + `slint-build`，`build.rs` 编译 `ui/log_viewer.slint`。
- `ui/log_viewer.slint`：Glass 暗色基线，三段分色日志行（时间灰 / 标签蓝 / 正文按级别），顶栏 + 清空按钮 + 条数。
- `src/main.rs`：演示消息流走 `bot_ipc::Msg::ev_log` → `to_line()` → `Msg::from_line()` → `LogEntry` 的**真实管道处理链**，S2 换数据源即可，UI 层不动。
- 启动自检打印 `12 modules / 13 nav aliases / N theme tokens`，证明三个共享 crate 已正确链接。

**测试版窗口**：`rust\bin\bot-ui-test.exe`（pid 11772 已启动，标题 `EfootballBot · 测试版 (Rust/Slint)`，响应中，~178 MB）。

## S2 完成内容（9 页主壳）

- `ui/app.slint`：4 分区（首页/挂机/工具/关于）× 9 页路由，结构映射 C# `Shell/NavRoute.cs`；顶栏分区 tab + 左栏导航轨道 + 中栏互斥页面。
- `ui/pages.slint`：9 个页面组件，文案从 C# `MainWindow.xaml` 导出（国服/国际服、设置/更新/GitHub/网络监测/日志历史/反馈/关于）。
- `ui/theme.slint`：Glass 暗色基线 token global（S3 会改成从 bot-theme build.rs 生成 .slint）。
- `--page=<alias>` 深链：`bot_state::NavKey::parse` 解析 → 同时 set page + section，验证 `--page=network` 正确跳到网络页。
- 共享层自检：`12 modules / 13 nav aliases / 105 theme tokens`（105 = Tokens.xaml 里纯颜色 token 数）。

**测试版窗口**：`rust\bin\bot-ui-test.exe`（当前 pid 33372，标题 `EfootballBot · 测试版 (Rust/Slint · S2)`，响应当中，~300 MB 含 Slint 静态资源）。

## S3 完成内容（主题六表 / 实时日志 / gpui 预览）

三件一次性做完：

1. **主题切换真做**：
   - Python 从 C# `ThemePalette.cs` 导出六表到 `crates/bot-theme/themes.json`（77/77/77/88/88/88 项，union 88）。
   - `bot-ui/build.rs` 读它生成 `ui/theme_generated.slint`（`ThemeColors` struct + `Palette` global 六属性）。
   - `ui/theme.slint` 的 `Theme.apply()` 按 `(world, appearance)` 选表，语义别名（window-bg/text-primary/accent…）全部绑定到 `colors.*`。
   - 左栏"主题与外观"按钮循环：3 亮度一循环，每循环一次切世界。顶栏状态栏显示当前主题。

2. **实时日志窗**（独立 bin `bot-log-window`）：
   - `src/log_window.rs` 连 C# Worker 命名管道（`\\.\pipe\NAME`），`BufReader::lines()` 读 JSON Lines，`bot_ipc::Msg::from_line` 解析 `ev=log`。
   - UI 线程用 Timer 轮询 mpsc channel（不跨线程碰 Slint）。
   - 无 `--pipe` 或连接失败自动走演示流，保证窗口总能看。
   - 用法：`bot-log-window.exe --pipe \\.\pipe\EfootballBot-Vision-xxx` 或 `--demo`。

3. **gpui 预览窗**（独立 crate `bot-preview` + 独立进程）：
   - gpui-ce 0.3.0 + gpui_ce_platform 0.3.0，最小窗口显示"EfootballBot 预览窗"骨架。
   - 边界立好：独立进程、独立 binary，S4 只需把占位 div 换成 `vision-svc` 帧环的 DX11 纹理。
   - 编译需 `-L native=C:\cargo-toolchain-bin\libs`（空 `libktmw32.a` 补 GNU 工具链缺的 Windows SDK 库）。

**三个测试版窗口**（均已启动验证）：
- `rust\bin\bot-ui-test.exe` — 主壳 S3（pid 31312，`EfootballBot · 测试版 (Rust/Slint · S3 · 主题六表)`）
- `rust\bin\bot-preview.exe` — gpui 预览（pid 31224，`EfootballBot · 预览 (gpui)`）
- `rust\bin\bot-log-window.exe` — 实时日志（按需 `--demo` / `--pipe` 启动）

全量测试：18 passed / 0 failed / exit 0。

## 下一步（S4）

1. 预览窗接 `vision-svc` 帧环：在 `bot-preview` 里用 `windows-rs` 映射共享内存（`shm.rs` 64 字节头 + N 槽），DX11 纹理上传替换占位 div。
2. 实时日志接真实 Worker：主壳工具区加入口按钮拉起 `bot-log-window --pipe`。
3. 把 `theme.slint` 的硬编码语义别名进一步收敛（bot-theme 直接生成完整 global）。
