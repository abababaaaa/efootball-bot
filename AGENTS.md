# AGENTS.md —— 本仓库的写法锁定（读之前先读这一节）

> 给所有在本仓库写代码的人 / AI：**下面这些写法已经「敲死」在代码里**，不是建议。
> 违反会被构建期检查拦下（`dotnet build` 直接失败），也会被单测挡住。
> **只有在用户明确要求更换写法时**，才可以动这些约定；动了必须同步更新
> `tools/arch-guard/arch-guard.ps1` 的规则表、`docs/MODULES.md` 与本文件。

## 一、挂机代码的三层结构（锁定）

```
Core/Bot/Modules     注册表层：模块清单（id / 服务器 / 场景或工具 / 工厂 / 是否可接力）
Core/Bot/Match       共享层：界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、探针、弹窗
Core/Bot/Scenarios   模块层：每个模块自己的菜单流程与私有状态机
Core/Tools           工具层：赛前设置 / 打开路径 / 窗口防卡死（与挂机完全解耦）
```

- **注册表是模块的唯一来源**：加模块 = `ModuleIds` 加常量 + `BotModuleCatalog` 加一条记录。
  引擎、GUI 按钮接线、接力下拉候选、跨服校验全部由它派生，**不许**再出现第二份清单。
- **共享能力只能下沉到 `Core/Bot/Match`**：模块之间要共用什么，就加在共享层，
  不许 `using` 另一个模块的命名空间（只读借用 `*Brain.Classify` 认残留页面是唯一例外）。
- **比赛相关的一切只走 `MatchPlay`**：开赛/终场/超时判定、场内挂机节奏、场次记账。
  模块里出现 `Stats.MatchesDone++` 或自己算 `IdleTapMinMs` 随机间隔 = 违规。
- **引擎不认识具体场景**：场景由 `BotModuleCatalog` 造，模块复位走 `IModuleLifecycle`。
- **工具与挂机同构**：工具唯一清单是 `Core/Tools/ToolCatalog`，界面只按 `(ToolIds.X, ToolAction.Y, ctx)` 派发；
  工具层不许引用 `EfootballBot.Core.Bot`（公共件放 `Core/**`：`ServerRegion` / `LogLevel` / `GameProcesses`）。
- **界面层不起进程**：`MainWindow*.cs` 里不许出现 `Process.Start`/`ProcessStartInfo`，
  打开路径/URL/定位文件一律走 `ToolIds.Shell`。

## 二、构建期强制（arch-guard）

| 规则 | 含义 |
|---|---|
| `ARCH-MATCH-DEP` | 共享层不许反向依赖模块（`using ...Scenarios.*`） |
| `ARCH-SCENARIO-CROSS` | 模块之间不许引用别的模块的 `*Scenario`（白名单：`CnMyLeagueScenario->MyLeagueScenario` 继承） |
| `ARCH-ID-LITERAL` | 模块 id 字面量只许出现在 `Core/Bot/Modules`，别处用 `ModuleIds` 常量 |
| `ARCH-ENGINE-KNOWLEDGE` | `BotEngine.cs` 不许出现任何具体场景类型 |
| `ARCH-SCENARIO-CREATE` | 场景只许在注册表里 `new`（工厂唯一入口） |
| `ARCH-MATCHPLAY-OWNED` | 场次记账只许在 `Core/Bot/Match` |
| `ARCH-IDLE-OWNED` | 比赛内挂机节奏只许在 `MatchPlay.IdleTapAsync` |
| `ARCH-TOOL-DEP` | 工具层不许引用挂机层（`EfootballBot.Core.Bot`） |
| `ARCH-TOOL-ID-LITERAL` | 工具 id 字面量只许在 `Core/Tools/ToolIds.cs` |
| `ARCH-TOOL-CREATE` | 具体工具只许在 `ToolCatalog` 里实例化 |
| `ARCH-UI-NO-PROCESS` | `MainWindow*.cs` 不许直接 `Process.Start` |

实现：`tools/arch-guard/arch-guard.ps1`（11 条规则），由根目录 `Directory.Build.targets` 在 `CoreCompile` 前调用。
单测 `ArchitectureTests`（含「跑一次 arch-guard 脚本」）覆盖同一批规则，方便 IDE 内跑。

**唯一允许的例外流程**（用户明确要求更换写法时）：

1. 改规则表 `tools/arch-guard/arch-guard.ps1`（并在文件里写清「为什么改、什么时候」）；
2. 同步 `docs/MODULES.md` §7 与本文件；
3. 单次排障可 `dotnet build -p:SkipArchGuard=true`，**不要**把这个参数写进任何脚本或 CI。

## 三、界面代码（延续 v7.1.4 的约定）

见 `DESIGN.md`：改颜色只动 `Themes/Tokens.xaml` + `Core/UI/ThemePalette.cs`；
加页面 = `Views/XxxPage.xaml` + `MainWindow.Shell.cs` 一行路由；
页面里不写颜色字面量、不手写 FontSize。

## 四、提交前必跑

```powershell
dotnet build src\EfootballBot\EfootballBot.csproj -c Debug -v q      # 0 警告 0 错误（含 arch-guard）
dotnet test tests\EfootballBot.Tests                                 # 全量（含实机帧 OCR 用例）
```

现场核对模块清单：`EfootballBot.exe --worker --list-modules`（不建会话、不碰游戏）。