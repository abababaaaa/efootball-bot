# 挂机模块化：分层、注册表、共享层

> 目标只有一句：**改一个模块，只动那个模块的文件；加一个模块，只动注册表一行。**
> 以前改国服天梯的一个判据，要顺手看国服周活动/我的联赛是不是也复制了一份；
> 加一个模块要同时改引擎的 mode 白名单、cn 判定、工厂 switch、设置页的接力候选——
> 漏一处就是「按钮点了没反应」或「接力到另一个服」。

---

## 1. 分层地图

```
Core/Bot/
├── Modules/                    模块注册表（一层：谁存在、属于哪个服、怎么造、能不能被接力）
│   ├── ServerRegion.cs         Intl / Cn（一级分界：两服窗口与菜单互不通用）
│   ├── ModuleKind.cs           Scenario（挂机场景）/ Tool（本地工具页）
│   ├── ModuleIds.cs            全部模块 id 常量 + EngineModes.Observe（非模块模式）
│   ├── BotModuleDescriptor.cs  一个模块的身份证
│   ├── BotModuleCatalog.cs     ★唯一注册表 + 白名单 + 自检
│   └── IModuleLifecycle.cs     引擎→模块的回调（重启游戏后复位），引擎只认接口
│
├── Match/                      共享层（二层：谁都可以调，谁都不拥有）
│   ├── MatchBrain.Types.cs     界面/动作枚举 + 阈值常量（MatchScreen / MatchActionKind / MatchAction）
│   ├── MatchBrain.Classify.cs  界面分类（纯函数，可单测；判定顺序有硬约束）
│   ├── MatchBrain.Decide.cs    共享决策（界面+OCR+连续轮数 → 手柄动作）
│   ├── MatchPlay.cs            赛中匹配：开赛/终场/超时判定 + 场内挂机节奏 + 场次记账
│   ├── MatchFlowMemory.cs      流程帧记忆 + 场次跟踪器（MatchSessionTracker）
│   ├── MatchShared.cs          标题宽限 / 比赛上下文 / 掉线重登窗口 / 决策快照
│   ├── ScreenProbe.cs          整帧像素探针（角区内容、整页亮底、绿勾）
│   ├── CnAdvance.cs            ★国服推进键序：① RT（页面所印提示键）→ ② A → ③ 后台鼠标点击
│   └── PopupBrain.cs           弹窗高光/按钮判定（三模块共用）
│
└── Scenarios/                  模块层（三层：每个模块只认自己的界面与状态机）
    ├── Weekly/        国际服·周活动（WeeklyBrain + WeeklyEventScenario，按小活动拆 partial，见 §1.1）
    ├── AiLadder/      国际服·AI 天梯联赛
    ├── MyLeague/      国际服·我的联赛（+ 满级换人巡检），国服我的联赛继承它
    ├── CnWeekly/      国服·周活动（同样按小活动拆 partial，见 §1.1）
    ├── CnMyLeague/    国服·我的联赛（继承国际服基类，只覆写三个钩子）
    ├── PeakLadder/    国服·天梯巅峰赛
    ├── Stud/          国际服·种马（购买/培训）
    └── PostMatchActivity.cs   赛后活动地图状态机（跨模块可选能力）
```

### 谁可以依赖谁

| 层 | 可以依赖 | 不可以依赖 |
|---|---|---|
| 注册表 `Modules` | 仅 `IScenario` 契约 | 任何具体场景类型的行为细节 |
| 共享层 `Match` | 引擎公开接口（Pad/Ocr/Observation/Stats） | 任何模块（`Scenarios.*`） |
| 模块 `Scenarios.X` | 注册表契约 + 共享层 + 引擎 | **其他模块**（曾经的 `CnWeeklyScenario` 里直接用 `MyLeagueScenario.CornerHasContent` 就是反例） |

### 1.1 周活动子活动分工（partial 拆分）

周活动里其实是**好几个形态各异的小活动**（常规周活动 / 挑战活动 / 阵容挑战 Team Effort /
单场周活动；国服还多「晋级挑战（教练签下）」与「巡回赛主要奖励核验」）。它们以前挤在
一个 1800 行的文件里，改一个就会碰到另一个的字段和分支。现在按小活动拆成同类型 partial：

**国际服 `Scenarios/Weekly/`**

| 文件 | 装什么 | 改什么时候只动它 |
|---|---|---|
| `WeeklyBrain.cs` | 界面分类 `Classify` + 活动主页/活动列表/设置解析/主要奖励（所有小活动共用） | 共用判定 |
| `WeeklyBrain.Entry.cs` | 进活动：介绍页「进入/详情」焦点、比赛页磁贴扫描、锦标赛页/建议面板 | 进活动入口 |
| `WeeklyBrain.Pick.cs` | 选择页：赛事等级 / 难度档 / 「选择地区」「选择球队」/ Y 随机 | 单场周活动的选页 |
| `WeeklyBrain.Challenge.cs` | 挑战活动：解锁弹窗、挑战清单、卡扫描、锁/勾探针、卡号消失判定 | 挑战活动 |
| `WeeklyBrain.SquadPlan.cs` | 阵容挑战：主页/球员卡限制弹窗/阵容页侧边栏/比赛计划红点 | 阵容挑战 |
| `WeeklyEventScenario.cs` | 主循环、`WkPhase` 分发、导航、弹窗/按键通用件 | 状态机骨架 |
| `WeeklyEventScenario.SingleMatch.cs` | 活动设置核验、赛事等级下拉、单场周活动难度页（issue #1） | 单场周活动 |
| `WeeklyEventScenario.Challenge.cs` | 挑战活动子流程 + 地区/球队选择前置（含它自己的 `_ch*` / `_pick*` 字段） | 挑战活动 |
| `WeeklyEventScenario.SquadPlan.cs` | 阵容挑战「比赛计划失效」修复（`PlanStep` 状态机 + `_plan*` 字段） | 阵容挑战 |

**国服 `Scenarios/CnWeekly/`**

| 文件 | 装什么 |
|---|---|
| `CnWeeklyBrain.cs` / `CnWeeklyScenario.cs` | 分类 + 状态机骨架 + 活动列表/主页导航（共用） |
| `*.Challenge.cs` | 挑战型活动：挑战清单选卡、卡页完成判定、撤退重进 |
| `*.Coach.cs` | 晋级挑战的教练签下链（选教练 → 看属性 → 签下确认，含后台鼠标点击） |
| `*.Reward.cs` | 巡回赛主要奖励核验（主页勾 → i 信息弹窗 x/x）与撤退 |
| `*.Difficulty.cs` | 赛事等级/难度卡片行判定与确认 |

规则（拆完继续照这个走）：

- **加一个新小活动 = 加一对新文件**（`XxxBrain.<小活动>.cs` + `XxxEventScenario.<小活动>.cs`），
  主文件只加一个 `case`／一次委托，不去别的 partial 里改代码；
- **字段跟着小活动走**：`_ch*` / `_pick*` / `_plan*` 这类私有状态放在它自己的 partial 里，
  主文件只留跨小活动共用的（阶段、连击数、场次、memory）；
- 小活动之间**只通过主文件的公共方法/字段交互**；不要在一个 partial 里直接调另一个
  partial 的私有 helper（同类型能编过，但改动又会互相打扰）。

---

## 2. 模块清单（= 注册表内容）

### 国际服（`ServerRegion.Intl`，窗口进程 eFootball）

| id | 名称 | 类型 | 可接力 | 说明 |
|---|---|---|---|---|
| `intl` | 我的联赛 | 场景 | ✅ | 道具核验/难度选择/开赛/满级换人巡检 |
| `intlweekly` | 周活动 | 场景 | — | 活动列表/介绍页/挑战清单/阵容挑战（Team Effort，含赛后「无法进行比赛」→ Y 比赛计划修阵容）/赛后活动地图 |
| `intlladder` | AI天梯联赛 | 场景 | ✅ | 组别主页→比赛循环 |
| `intlstudbuy` | 种马购买 | 场景 | — | 合同页筛选/购买/到手确认 |
| `intlstudtrain` | 种马培训 | 场景 | — | 筛选 + 技巧训练 |
| `intlswaptest` | 满级换人测试 | 场景 | — | 只跑一次巡检即自动停（排障） |
| `intlprematch` | 赛前快捷设置 | 工具 | — | 启动本体/打开 Settings.exe/打开游戏目录 |

### 国服（`ServerRegion.Cn`，窗口进程 eFootballOnline）

| id | 名称 | 类型 | 可接力 | 说明 |
|---|---|---|---|---|
| `cndaily` | 天梯巅峰赛 | 场景 | ✅ | 主页→模式选择→参赛弹窗→单场循环（场次为 0 自动停） |
| `cnweekly` | 周活动 | 场景 | — | 杯赛/巡回/挑战/教练签下链 |
| `cnmyleague` | 我的联赛 | 场景 | ✅ | 难度页/开赛/跳过比赛（仅「对局获胜」可跳） |
| `cnprematch` | 赛前快捷设置 | 工具 | — | 同上，国服路径 |

> 接力白名单 = 「本服 + 可接力(HandoffEligible) + 是场景」，由注册表算出，
> 设置页的下拉候选、引擎的 `HandoffTo` 校验、管道协议的模式校验共用这一份。

---

## 3. 加一个模块（照抄即可）

以「国服·锦标赛」为例，只动两个文件：

1. `Modules/ModuleIds.cs` 加常量：
   ```csharp
   public const string CnTournament = "cntournament";
   ```
2. `Modules/BotModuleCatalog.cs` 的 `Registry` 加一条：
   ```csharp
   new()
   {
       Id = ModuleIds.CnTournament, Server = ServerRegion.Cn, Kind = ModuleKind.Scenario,
       DisplayName = "锦标赛", Summary = "国服锦标赛：报名→开赛→赛后奖励",
       Factory = (engine, log) => new CnTournamentScenario(engine, log),
   },
   ```
3. 写场景（`Scenarios/CnTournament/`）：`IScenario`，内部**只**做三件事：
   - 用共享分类 `MatchBrain.Classify` 拿 `MatchScreen`；
   - 先拦截「本模块自有界面」（自己的 `CnTournamentBrain` + 状态机），
     其余界面交给 `MatchBrain.Decide`；
   - 比赛相关**一律**走 `MatchPlay`：`MarkAtMenu()` / `Tick(...)` / `IdleTapAsync()`。
4. GUI 接线（如果要出按钮）：`MainWindow.xaml` 加按钮 →
   `MainWindow.ModuleRun.cs` 的 `_moduleRuns` 加一行 → 点击处理里用
   `ToggleScenarioAsync(BtnXxx, ModuleIds.CnTournament)`。

**不需要**再改：引擎的 mode 白名单、cn 判定、工厂 switch、接力候选表、跨服校验。
注册表 `SelfCheck()`（单测 `ModuleCatalogTests` 覆盖）会挡住重复 id／场景缺工厂／
工具页带工厂／接力目标跨服这些结构性错误。

---

## 4. 共享层提供了什么（模块需要什么就来这里找）

| 能力 | 入口 | 谁在用 |
|---|---|---|
| 界面分类 | `MatchBrain.Classify(dets, ocr, 角区, 比赛上下文, 亮底, 重登窗口)` | 全部模块 |
| 共享决策 | `MatchBrain.Decide(screen, ocr, streak, inMatchRecently, frame, allowMatchSkip)` | 全部模块 |
| 赛中场次 | `MatchPlay.Tick / IdleTapAsync / MarkAtMenu / Abort` | 全部 5 个打比赛模块 |
| 场内挂机节奏 | `MatchPlay.IdleTapAsync`（读设置里的挂机节奏区间） | 同上 |
| 流程帧记忆 | `MatchFlowMemory`（SawWithin / Record / Clear） | 全部模块 |
| 标题宽限 / 比赛上下文 / 掉线窗口 | `MatchShared.TitleGrace / MatchContext / LoginRecovery` | 全部模块 |
| 像素探针 | `ScreenProbe.CornerHasContent / IsMostlyLightPage / CountGreen` | 全部模块 |
| 弹窗按键判定 | `PopupBrain.Decide / DecideUpDown` | 我的联赛 / 种马 / 共享决策 |
| 国服「下一步/确认」推进 | `CnAdvance.AdvanceAsync / StepOf / FindButton`（① RT → ② A → ③ 后台鼠标；仅国服，国际服不吃鼠标） | 国服天梯巅峰赛 / 国服周活动 / 我的联赛（国服） |
| 赛后活动地图 | `PostMatchActivityController`（可选能力，模块自己决定要不要接） | 国际服周活动 / AI 天梯 / 我的联赛 |

「赛前快捷设置」不是引擎场景：它是 `ModuleKind.Tool`，只做启动游戏/打开设置工具/开目录，
不占虚拟手柄、不建捕获会话。

---

## 5. 这次搬家做了什么（对照表）

| 之前 | 现在 | 为什么 |
|---|---|---|
| `Scenarios/MyLeague/MyLeagueBrain.Classify/Decide` | `Match/MatchBrain.Classify.cs` / `MatchBrain.Decide.cs` | 国服天梯/周活动/AI天梯都在跨模块借「我的联赛的私有件」 |
| `enum MlScreen` / `MlAction` / `MlActionKind` | `MatchScreen` / `MatchAction` / `MatchActionKind` | 去掉「谁的名字」，变成共享词汇 |
| `MlFlowMemory.cs`（在 MyLeague 目录） | `Match/MatchFlowMemory.cs`（`MatchFlowMemory` + `MatchSessionTracker`） | 流程记忆/场次跟踪是全模块共用 |
| `ScenarioShared`（在 Scenarios 根） | `Match/MatchShared.cs` | 共享小流程归共享层 |
| `PopupBrain`（在 Scenarios 根） | `Match/PopupBrain.cs` | 三个模块共用弹窗判定 |
| `MyLeagueScenario.CornerHasContent/IsMostlyLightPage` | `Match/ScreenProbe.cs` | 别的模块调场景类 = 反向依赖 |
| 5 份复制粘贴的「场次计数 + 比赛挂机节奏」 | `Match/MatchPlay.cs` 一份 | 场次口径（可信/超时/冷却）只能有一处定义 |
| 引擎 `mode is "cndaily" or ...` / `cn is ...` / `CreateScenario` switch | `BotModuleCatalog` | 加模块不再改引擎 |
| 引擎 `(Scenario as MyLeagueScenario)?.OnGameRestarted(...)` | `(Scenario as IModuleLifecycle)?...` | 引擎不认识具体场景类型 |
| `BotEngine.IntlHandoffTargets / CnHandoffTargets` + 设置页两张手写表 | `BotModuleCatalog.IsHandoffTarget / HandoffCandidates` | 白名单只有一份 |

`MyLeagueBrain` 现在只剩我的联赛私有逻辑：道具购买行序与档位匹配、难度页探针与腿计划、
赛后经验值页球员数、联赛主页磁贴焦点与导航图——不再被任何其他模块引用。

---

## 6. 改完怎么验

```powershell
# 1) 编译（0 警告 0 错误）
dotnet build src\EfootballBot\EfootballBot.csproj -c Debug -v q

# 2) 注册表契约 + 共享层契约（加模块必跑）
dotnet test tests\EfootballBot.Tests --filter "FullyQualifiedName~ModuleCatalog|FullyQualifiedName~SharedMatchLayer"

# 3) 全量回归（含实机帧 OCR 用例）
dotnet test tests\EfootballBot.Tests
```

实机验证优先级（共享层改动波及全部模块）：

1. **赛前设置**（工具页，不接引擎）→ 只验按钮能开游戏/设置工具；
2. **我的联赛** 跑一场：开赛→终场→下一场（场次计数与「用时 mm:ss」是否正常）；
3. **周活动** 的「全部完成后接力」：下拉候选应与注册表一致（不跳转 / 本服可接力模块）；
4. **国服天梯** 跑到场次为 0 自动停（验证 MatchPlay 的停止条件未被共享层改坏）。
---

## 7. 架构护栏（构建期强制，不是建议）

这些约定已经「敲死」在代码里：**违反任一条，`dotnet build` 直接失败**。
单测只是第二道闸（方便 IDE 里跑），权威实现是 `tools/arch-guard/arch-guard.ps1`。

### 7.1 规则表（7 条）

| 规则 id | 断言 | 为什么 |
|---|---|---|
| `ARCH-MATCH-DEP` | `Core/Bot/Match/**` 不许出现 `EfootballBot.Core.Bot.Scenarios` | 共享层一旦引用模块就再也抽不出去 |
| `ARCH-SCENARIO-CROSS` | `Scenarios/X/*.cs` 不许出现别的模块的 `*Scenario`（白名单：`CnMyLeagueScenario->MyLeagueScenario` 继承） | 模块之间只允许只读借用 `*Brain.Classify` 认残留页面 |
| `ARCH-ID-LITERAL` | 模块 id 字面量只许出现在 `Core/Bot/Modules` | 一处漏改就是「按钮点了没反应」 |
| `ARCH-ENGINE-KNOWLEDGE` | `BotEngine.cs` 不许出现任何 `*Scenario` 类型名 | 加模块不用改引擎（复位走 `IModuleLifecycle`） |
| `ARCH-SCENARIO-CREATE` | `new XxxScenario(` 只许在注册表里 | 工厂唯一入口，别处 new 出来的场景不受接力/服校验约束 |
| `ARCH-MATCHPLAY-OWNED` | `Stats.MatchesDone++` / `KeyMatches++` 只许在 `Core/Bot/Match` | 场次口径只能一处定义（否则又会出现「76 场 vs 战绩 29 场」） |
| `ARCH-IDLE-OWNED` | 模块层不许读 `IdleTapMinMs/MaxMs` 自己算随机间隔 | 比赛内挂机节奏归 `MatchPlay.IdleTapAsync` |

### 7.2 怎么被强制执行

1. **构建期**：根目录 `Directory.Build.targets` 的 `ArchGuard` 目标在 `CoreCompile` 之前调用脚本，
   退出码非 0 → `error MSB3073`，编译失败（消息里直接给出「哪个文件哪一行违反了哪条规则」）。
2. **单测**：`ArchitectureTests`（4 条源码扫描 + 1 条「真的跑一次 arch-guard 脚本」），
   `ModuleCatalogTests`（注册表自检），`SharedMatchLayerTests`（共享层契约）。
3. **文件头**：`Modules/`、`Match/`、`Scenarios/` 全部文件、`BotEngine.cs`、`MainWindow.ModuleRun.cs`
   带「架构锁」横幅，写明本层职责、禁止事项与「改写法先问用户」。
4. **运行期**：引擎启动跑一次 `BotModuleCatalog.SelfCheck()`，有问题写 WARN 进 `data\app.log`；
   `EfootballBot.exe --worker --list-modules` 现场打印模块表与自检结果（不建会话、不碰游戏）。

### 7.3 唯一允许的例外流程

只有**用户明确要求更换写法**时才可改：

1. 改 `tools/arch-guard/arch-guard.ps1` 的规则表（并在文件头写清「为什么改、按谁的要求」）；
2. 同步本文件 §7 与根目录 `AGENTS.md`；
3. 单次排障可用 `dotnet build -p:SkipArchGuard=true`，**不要**写进任何脚本、CI 或提交说明。

> `AGENTS.md` 是这套锁的入口文档：任何在本仓库写代码的人（或 AI）先读它，再动手。
---

## 8. 工具模块（Core/Tools，与挂机同一套规则）

工具模块（赛前设置、打开路径、窗口防卡死）以前是写在 `MainWindow.xaml.cs` 里的三段私有方法，
每段都 `intl ? … : …` 分两服；窗口防卡死的入口在界面、实现在 `BackgroundParker`。
现在它们与挂机模块同构：

```
Core/Tools/
├── ToolIds.cs        工具 id 唯一来源（prematch / shell / windowrescue）
├── ToolAction.cs     动作枚举（LaunchGame / OpenSettingsTool / OpenGameDirectory / RescueWindow / OpenPath / OpenUrl / RevealPath）
├── ToolResult.cs     结果（Ok + 给人看的一句话 + 处理到的路径）
├── ToolContext.cs    调用上下文（分区 + 配置 + 日志回调 + 目标路径）
├── ITool.cs          工具契约（界面只认接口，不认识具体工具类型）
├── ToolDescriptor.cs 注册项（id + 实现 + 动作 + 是否按分区）
├── ToolCatalog.cs    ★唯一清单 + ExecuteAsync(id, action, ctx) + SelfCheck()
├── ToolPaths.cs      共享件：两服 Settings.exe / 游戏目录 / 本体定位 + 路径记忆
├── PreMatchTool.cs   赛前快捷设置（启动游戏 / 打开 Settings.exe / 打开游戏目录）
├── ShellTool.cs      打开路径 / 定位文件 / 打开 URL（界面层从此不再 Process.Start）
└── WindowRescueTool.cs 窗口防卡死（薄封装 BackgroundParker 的窗口操作）
```

| 工具 id | 名称 | 动作 | 分区 |
|---|---|---|---|
| `prematch` | 赛前快捷设置 | 启动游戏 / 打开设置工具 / 打开游戏目录 | 是（两服各自路径记忆） |
| `shell` | 打开路径 | 打开路径 / 定位文件 / 打开 URL | 否 |
| `windowrescue` | 窗口防卡死 | 拉回窗口 | 否 |

**界面接线只有一行**（`MainWindow.Tools.cs`）：

```csharp
private async void BtnLaunchGame_Click(object sender, RoutedEventArgs e)
    => await RunToolAsync(ToolIds.PreMatch, ToolAction.LaunchGame, ServerRegion.Intl, TxtPreMatchPath);
```

新增工具三步：`ToolIds` 加常量 → `ToolCatalog` 加一条 → `Core/Tools/XxxTool.cs` 写实现。
界面只需要一行派发；不需要改任何 `intl ? … : …` 分支（分区是 `ServerRegion` 参数）。

### 8.1 工具侧的架构锁（新增 4 条，总数 11）

| 规则 id | 断言 | 为什么 |
|---|---|---|
| `ARCH-TOOL-DEP` | `Core/Tools/**` 不许出现 `EfootballBot.Core.Bot` | 工具一旦依赖挂机，工具就被挂机绑架（`LogLevel`/`ServerRegion` 这类公共件已下沉到 `Core/`） |
| `ARCH-TOOL-ID-LITERAL` | 工具 id 字面量只许在 `ToolIds.cs` | 拼错 id 只会「点了没反应」 |
| `ARCH-TOOL-CREATE` | `new XxxTool(` 只许在 `ToolCatalog` | 工厂唯一入口 |
| `ARCH-UI-NO-PROCESS` | `MainWindow*.cs` 不许 `Process.Start` / `ProcessStartInfo` | 界面只渲染；起进程/开路径归工具层 |

### 8.2 顺手把「公共词汇」搬到了 Core

工具层不许依赖挂机层，所以两个被双方共用的类型搬出了 `Core.Bot`：

- `ServerRegion`（国服/国际服）：`Core/Bot/Modules/ServerRegion.cs` → `Core/ServerRegion.cs`
- `LogLevel`：`Core/Bot/Observation.cs` → `Core/LogLevel.cs`
- `GameProcesses`（两服进程名）：`Core/GameProcesses.cs`（引擎与工具共用，`BotEngine.CnProcessName` 也指向它）
