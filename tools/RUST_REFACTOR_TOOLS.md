# Rust 重构工具箱与调用路由

> 目标：把现有 C#/WPF 客户端逐步重构为 Rust（Slint 主壳 + 可选 gpui 预览窗）。
> 本文定义「装了什么 / 谁在什么时候调 / 怎么调」。仓库根目录：`D:\Efootball挂机软件`。

## 0. 本机前提

- Rust 工具链：`C:\Users\笔记本\.cargo\bin`（rustc/cargo/rustup **1.100.0**，2026-11-09）。
  PATH 里默认没有，脚本里先 `$env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path"`。
- Node（DSH 内置）：`C:\Users\笔记本\.dsh\dsh-runtimes\dsh-next-primary-runtime\dependencies\node\bin\node.exe`（v24.21.0）。
- DSH Web GUI：`http://127.0.0.1:62771`；本会话 profile 已授权 danger-full-access、免审批。

## 1. 已装 / 已备工具清单

| 工具 | 形态 | 安装位置 | 状态 |
|---|---|---|---|
| `cargo-deny` 0.22.1 | cargo 子命令 | `%USERPROFILE%\.cargo\bin\cargo-deny.exe` | ✅ 官方 MSVC 预编译 |
| `cargo-machete` 0.10.1 | cargo 子命令 | 同上 | ✅ 官方 MSVC 预编译 |
| `cargo-nextest` 0.9.152 | cargo 子命令 | 同上 | ✅ 官方 MSVC 预编译 |
| `rust-analyzer` 0.3.3130 | LSP server | `%USERPROFILE%\.cargo\bin\rust-analyzer.exe` | ✅ 官方 MSVC 预编译（补位 aptu-coder，见下） |
| `aptu-coder` 0.47.0 | MCP server | `tools/refs/aptu-coder-src`（源码）+ bundle `tools/dsh/aptu-coder-bundle` | ⚠️ 官方仅发 macOS/Linux 二进制（依赖 `nix`），Windows 无法原生运行；bundle 已备好，仅 Linux/WSL 可启用 |
| Slint VS Code 扩展 | VSIX 1.19.1 | `tools/vsix/slint-1.19.1.vsix` | ✅ 已下载（本机未装 VS Code，供团队分发） |
| `gpui-component` | 参考仓库 | `tools/refs/gpui-component`（3392 文件） | ✅ 浅克隆 |
| `yororen-ui` | 参考仓库 + Codex skills | `tools/refs/yororen-ui`（489 文件） | ✅ zip 下载 |
| `slint-template` | 参考仓库 | `tools/refs/slint-template`（649 文件） | ✅ zip 下载 |
| `aptu-coder-src` | 源码参考 | `tools/refs/aptu-coder-src`（2302 文件） | ✅ 供阅读其结构分析实现 |
| `impeccable` skill | DSH 本地 skill | 已加载 | 可直接 `skill` 调用 |
| `dsh-doc` skill | DSH 本地 skill | 已加载 | 插件/MCP bundle 改动时调用 |

### 本机 Rust 环境 caveat（重要）

本机工具链是 **GNU**（`x86_64-pc-windows-gnu`），`cargo install` 编译任何含 build script 的 crate 都会在链接阶段失败：MinGW 的 `ld`/`dlltool` 在中文用户名路径（`C:\Users\笔记本`）下无法写输出文件，且缺 `dlltool.exe`。**结论： cargo 工具一律用官方 MSVC 预编译二进制，不要 `cargo install`。** 下载缓存在 `C:\cargo-target\dl`，解压在 `C:\cargo-target\extract`。

未来跑 workspace 构建时建议先 `rustup toolchain install stable-x86_64-pc-windows-msvc` 并 `rustup default stable-x86_64-pc-windows-msvc`（需 VC Build Tools），否则项目本身也会撞同样的链接错误。

### 已验证的编译 workaround（rust-refactor/test 分支，S0 已落地）

构建 Rust workspace 必须用三项组合，缺一不可（详见 `rust/WORKSPACE.md`）：
1. `CARGO_TARGET_DIR=C:\cargo-target\workspace-target`（纯 ASCII；`%LOCALAPPDATA%` 也含中文，不能用）
2. `RUSTFLAGS="-C linker=<rust-lld 绝对路径>"`（系统 mingw-gcc 会把中文 sysroot 参数搞坏；rustc 自带 lld 接受 UTF-16 路径）
3. PATH 首位放真实 toolchain bin（`~/.cargo/bin` 的 rustup shim 在 DSH shell 下 `--print sysroot` 返回空）

一键脚本：`rust\build-workspace.bat`。S0 骨架已 18 tests 全绿。

## 2. 调用路由表（核心）

| 阶段 | 任务 | 首选工具 | 触发方式 | 产出 |
|---|---|---|---|---|
| S0 | 摸清 5 万行 C# 的依赖/调用链 | **aptu-coder MCP**（Linux/WSL）或本机 **rust-analyzer + grep/glob** | Linux 下调 `mcp__aptu-coder__analyze_directory` / `analyze_symbol`；本机用 `rust-analyzer` LSP + 内置 grep/glob | 可迁移边界清单 |
| S0 | 建 workspace、`bot-ipc`/`bot-state`/`bot-theme` | 手写 + `slint-template` 参考 | 读 `tools/refs/slint-template` | Rust workspace 骨架 |
| S0 起 | 每次 Rust 提交前依赖策略检查 | **cargo-deny** | `cargo deny check`（CI/本地） | 许可证/漏洞/advisory 门禁 |
| S0 起 | 清理无用依赖 | **cargo-machete** | `cargo machete` | 未使用 crate 列表 |
| S0 起 | 跑测试（替代 cargo test） | **cargo-nextest** | `cargo nextest run` | 并行测试报告 |
| S1 | 167 token + 9 面板结构落文档 | **impeccable** `document` | `skill impeccable` → `/document` | `DESIGN.md` + token 清单 |
| S1 | 抽 Slint 组件体系 | **impeccable** `extract` | `/extract src/EfootballBot/Themes` | `tokens.slint` 组件骨架 |
| S1 | .slint 实时预览/改名同步 | Slint VS Code 扩展 | 装 `tools/vsix/slint-1.19.1.vsix` | Live Preview + LSP |
| S1 | 界面质量闸门 | **impeccable** `audit`/`critique` | `/audit <panel>` | 无障碍/性能/视觉评审 |
| S2 | gpui 预览窗控件 | `gpui-component` / `yororen-ui` | 读 `tools/refs/gpui-component`、`tools/refs/yororen-ui`；其 Codex skills 改写为 DSH skill | 预览窗 UI |
| 全程 | 改 DSH 插件/MCP 配置 | **dsh-doc** skill | `skill dsh-doc` → 按 reference 操作 | bundle / MCP 连接 |

## 3. 各工具最小用法

### aptu-coder（仅 Linux/WSL）
```
mcp__aptu-coder__analyze_directory   path:/mnt/d/Efootball挂机软件/src/EfootballBot/Core  summary:true
mcp__aptu-coder__analyze_module      path:.../Core/Bot/Match            # 轻量函数/import 索引
mcp__aptu-coder__analyze_symbol      path:.../src/EfootballBot  symbol:BotEngine        # 调用链
```
推荐四步：根目录 `max_depth=2` 概览 → 钻进子包 → `analyze_module` 索引 → `analyze_symbol` 追调用。Windows 本机改用 `rust-analyzer` + DSH 内置 grep/glob 做同等的结构摸底。

### cargo 四件套（PATH 注入后）
```powershell
$env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path"
cargo deny check          # 依赖策略（0.22.1，MSVC 预编译）
cargo machete             # 无用依赖（0.10.1）
cargo nextest run         # 测试（0.9.152）
rust-analyzer --version   # LSP（0.3.3130，供编辑器/DSH lsp 使用）
```
本机 PATH 默认没有 cargo bin，任何脚本开头都要注入；工具均为预编译 exe，切勿 `cargo install`。

### impeccable skill
```
skill 工具加载 → 首次跑 <skill-base>/scripts/impeccable.cmd context
动 UI 前必读 reference/craft-floor.md
/slint 面板用 document → extract → audit 顺序
```
skill 基目录：`C:\Users\笔记本\.dsh\skills\impeccable`。

### Slint VS Code 扩展
将 `tools/vsix/slint-1.19.1.vsix` 分发给团队，VS Code：`Extensions: Install from VSIX...`。

## 4. 启用 aptu-coder MCP bundle

bundle 目录：`tools/dsh/aptu-coder-bundle`（已备好 package.json / cordis.patch.yml / locale / icon / docs）。

启用方式二选一：
1. DSH Web GUI → **Plugins** → **Add plugin** → 选该目录；
2. 由具备 `plugin_manager` 权限的会话执行 `install_bundle`，target 传该目录绝对路径。

bundle 用 stdio 连接 `C:\Users\笔记本\.cargo\bin\aptu-coder.exe`，`failOnStartupError: false`（服务未装好也不阻断启动）。**注意**：本机 Windows 无 aptu-coder 可执行二进制，bundle 装上也连不上；它只在 Linux/WSL（装好 `aptu-coder` 后）生效。Windows 本机用 `rust-analyzer` + grep/glob 顶替。

## 5. 参考仓库用途

- `tools/refs/gpui-component`：75+ GPUI 组件、AccessKit、headless UI 测试；S2 预览窗直接借鉴，注意它 pin 了 gpui 版本。
- `tools/refs/yororen-ui`：GPUI 组件库 + `codex-skills/*`（`yororen-ui-app-core` / `state-inputs` / `recipes`），可改造成 DSH skill。
- `tools/refs/slint-template`：Rust+Slint 脚手架（含设置面板/配置/数据库），S0 起骨架参考。
