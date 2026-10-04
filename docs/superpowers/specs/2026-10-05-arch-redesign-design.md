# eFootball Bot 架构重构设计：页面图谱框架 + 单 exe 双模式

日期：2026-10-05
状态：已获用户批准（分节确认）
执行策略：绞杀者模式（方案 A），用户另开任务并行做全量重写（方案 C），本设计的页面图谱与探针资产可被方案 C 直接复用

## 1. 背景与痛点

现有架构为单进程 WPF + BotEngine，三个场景（CnDaily 1231 行 / IntlWeekly 753 行 / MyLeague 508 行）继承 ScenarioBase（1004 行），共享 Screens.cs 的 37 值 GameScreen 扁平枚举 + OCR 关键词加权分类器（ScreenCatalog，639 行）。真实运行中反复出现的事故类别：

1. **分类器补丁摞补丁**：弹窗/加载/断线与主页/比赛中是平级枚举，导致强制 Loading 优先、奖励弹窗强制 MyLeagueHome、InMatch HUD 门控等一系列硬编码例外，新增页面就要回忆所有冲突规则。
2. **导航无验证**：动作执行后不验证落点，焦点错位盲按 A 误入商店形成死循环（v1.1.4 修复的「兑换积分」事故）。
3. **弹窗清扫与焦点归位逻辑散落三处**，修复一处忘改另外两处。
4. **单进程无隔离**：池化崩溃、死循环刷日志（68 秒 73 万行）会直接拖死整个软件。
5. **误判排查靠读日志猜**：无法直观看到"当前判成哪页、哪个关键词送的分"。

## 2. 已确认的关键决策

| 决策点 | 结论 |
|---|---|
| 重构方式 | 方案 C 拆分模块为子程序，由轻量框架统管 |
| 隔离粒度 | **单 exe 双模式**：GUI 壳 + `--runner` 无头子进程，进程级崩溃隔离，发布形态不变（自包含单文件约 72MB） |
| 页面图谱表示 | **代码内声明式**：页面 = C# 类实现 IPage（识别探针/动作/去向/补救），编译期检查 |
| 判断框架 | **探针评分制**：保留 OCR 关键词思路但结构化为探针，逐步迁移现有 37 种页面 |
| 可视化 | **只读实时图谱面板**：从代码声明自动生成节点/边图，运行时当前页高亮、路径发光、探针得分可查；不做可拖拽积木编辑器 |
| 迁移策略 | **绞杀者模式**：每步可编译、可发布、可回滚、可真机实测 |

## 3. 进程架构（第 1 节，已批准）

```
EfootballBot.exe（单文件，自包含，发布命令与现网不变）
│
├── 默认启动 = GUI 壳（现有 WPF 界面保留）
│     ├─ 开始 → Process.Start(自身, "--runner --scenario=<myleague|intlweekly|cndaily>")
│     ├─ 看门狗：每 2s 检查子进程心跳；无心跳/进程退出 → 按配置自动重启或停止
│     ├─ IPC：命名管道，JSON Lines 行协议
│     │     子进程 → 壳：日志行、统计、当前页面状态包、预览帧（JPEG）
│     │     壳 → 子进程：停止、配置热更新
│     └─ 全局热键 / 设置 / 自动更新 留在壳内（壳不持有手柄）
│
└── --runner 模式 = 无头工作进程（WindowStyle.Hidden）
      ├─ Core 层：WindowCapture / OcrRecognizer / Pad / FrameAnalyzer（现有代码原样复用）
      ├─ 图谱引擎：页面注册表 + 探针分类器 + 导航寻路 + 统一未知恢复
      └─ 场景模块：MyLeague / IntlWeekly / CnDaily（独立文件夹，仅依赖框架抽象）
```

要点：

- **崩溃隔离**：子进程崩溃/死循环 → 看门狗强杀重启，壳不死，最多损失一局。
- **调试路径**：`EfootballBot.exe --runner --scenario=myleague --cli` 控制台直跑单模块，日志走 stdout。
- **预览**：子进程降采样帧编码 JPEG 经管道发壳，壳预览 UI 复用现有代码。
- **输入设备**：仅子进程持有 ViGEm 手柄，壳完全不碰，避免双进程抢 slot；全局停止热键在壳侧注册，触发后经管道下发停止。
- **发布命令不变**：`dotnet publish src/EfootballBot -c Release -r win-x64 -f net10.0-windows10.0.19041.0 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=PdbOnly -o publish`；发布前先 Stop-Process 旧实例。

## 4. 页面图谱与探针引擎（第 2 节，已批准）

### 4.1 双层观察模型（核心纠偏）

```
一次观察 Observation = ( BasePage 底层页, Overlay? 覆盖层 )

底层页（约 15 个，互斥）：Title / Home / MatchHub / EventHub / ChallengeList /
  ChallengeHome / MyLeagueHome / ActivityDetail / Schedule / PreMatch /
  InMatch / HalfTime / Result / Rewards / Shop / Disconnected ...

覆盖层（可叠加于任何底层页，独立探针、独立处置）：
  Dialog（确定/OK/下一步）   Loading（转圈/黑屏）
  RewardPopup（积分/收件箱） KeyMatch（关键比赛）
  Expired（物品已过期）
```

覆盖层命中时，底层页分类自动屏蔽被覆盖区域，「弹窗盖住主页但 HUD 图标外露误判 InMatch」这类事故在结构上消失。

### 4.2 探针模型

```csharp
interface IProbe { double Score(in Observation o); }

// 内置探针类型（覆盖现有全部判断手段）：
OcrProbe(text, weight, region?, exact?)     // OCR 关键词，可限定归一化区域、可精确匹配
ColorRatioProbe(filter, roi, min, weight)   // 颜色占比（草皮 hue 68~160、绿勾、白按钮…）
PixelProbe(...)                             // 逐像素小图标（手柄/AI 图标，沿用现有阈值）
NotProbe(inner)                             // 排除探针：命中则该页直接出局（替代门控补丁）
```

探针阈值/区域数值直接迁移 project_memory 中已实测校准的值（草皮 0.13、手柄白度 0.12、AI 0.06、绿勾 0.08、白按钮 0.30/蓝条 0.45 等），不重新发明。

### 4.3 页面声明（识别 + 动作 + 去向 + 补救）

```csharp
class MyLeagueHomePage : Page
{
    public override string Id => "MyLeague.Home";

    // 识别：必填全中 + 可选评分 ≥ 阈值；排除项命中即出局
    public override PageIdentity Identity => new(
        Required: [ Ocr("前往比赛", region: LeftPanel) ],  // 限定区域，杜绝磁贴预览文字误命中
        Optional: [ Ocr("下一场",2.5), Ocr("联赛排名",2.5), Ocr("赛程表",2), Ocr("阵容",2) ],
        MinScore: 2.5,
        Excluded: [ Overlay.Is(RewardPopup) ]);            // 有积分弹窗时不算干净主页

    // 边 = 操作 + 预期落点 + 超时 + 失败补救（内建落点验证）
    public override NavEdge[] Edges =>
    [
        new("GoToMatch",  do: ResetFocus().Then(Press.A),
             to: "Schedule", timeout: 8s,
             verify: OnArrive.Else(IfLanded("Shop", Press.B).Retry(2).GiveUp(Stop))),
        new("OpenShop",   do: ResetFocus().Then(Down,Right,Press.A), to: "Shop", timeout: 8s),
        new("OpenDetail", do: ResetFocus().Then(Right,Press.A),      to: "ActivityDetail", timeout: 5s),
    ];

    // 补救：进入该页面前自动执行（弹窗清扫最多8轮，关键比赛=Up→A；焦点归位 Left×2→Up×2）
    public override Recovery Recovery => Recovery.SweepPopups(maxRounds: 8).Then(FocusHome.LeftUp);
}
```

结构性解决三类历史事故：

1. 「下一场/排名」误命中比赛菜单磁贴 → 关键词限定 LeftPanel 区域 + 必填探针；
2. 买道具后焦点未归位盲按 A 进商店死循环 → 边内建 ResetFocus + 落点验证，落入 Shop 自动 B 退出，重试 2 次失败则停止本轮并报错；
3. 焦点归位/弹窗清扫全项目只写一份（页面声明），所有场景共享。

### 4.4 导航寻路

- 全部页面的 Edges 构成有向图，框架 BFS 最短路径寻路。
- 场景代码从硬编码按键序列变为目标声明：

```csharp
await Nav.GoTo("Schedule");   // Home→MatchHub→MyLeagueHome→Schedule 自动找路
await Nav.Do("GoToMatch");    // 当前页执行指定边，自动验证落点
```

- 某边连续失败 → 临时标记断开 → 重新寻路绕路 → 仍无路 → 全局恢复（回主页重进）。场景代码不关心。

### 4.5 统一未知恢复

Unknown 收编为框架唯一入口（逻辑等同现 HandleUnknownAsync，全项目只此一份）：
覆盖层命中 → 处置覆盖层（Dialog 按确定、Loading 等待、Expired 立即 OK…）→ 未命中 → 等待 4 轮 → 按 1 次 B → 仍不行 → 回主页。比赛中/匹配中绝不主动按 B 的规则保留在覆盖层处置里。

## 5. 只读实时图谱面板（第 2 节补充，已批准）

- **位置**：GUI 壳新增「图谱」Tab，不动现有预览/日志/设置页。
- **数据来源**：runner 启动时把页面注册表序列化为 JSON 经管道发壳（节点 Id+类型、边、探针清单）；运行时每轮观察推送轻量状态包（当前页 Id、Top3 页面得分、最后执行的边、落点验证结果）。图谱永远与代码同步，无需手工维护。
- **绘制**：WPF Canvas 自绘（不引 WebView2，保住单文件体积）。布局按从 Home 出发的 BFS 深度分层；底层页实线节点，覆盖层虚线框。
- **运行时表现**：当前页绿色高亮；覆盖层命中红色角标；最近 20 步路径边发光渐隐；点击节点 → 侧栏显示该页探针实时得分明细（各探针得分、距阈值差值）。
- **误判排查动线**：看到高亮在错误节点 → 点击 → 看到是哪个探针送分 → 回代码改该探针阈值/区域。

## 6. 场景模块新形态与绞杀迁移（第 3 节，已批准）

### 6.1 场景新形态

场景 = 独立文件夹，仅依赖框架抽象（INavigator / IPad / IObservation），只编排目标。比赛循环、赛后结算、弹窗清扫下沉为框架共享组件（MatchRunner、PopupSweeper），场景只传参数（是否允许弃权、目标场次）。预计 MyLeague 从 508 行降至 150 行以内。

### 6.2 迁移步骤（每步单独可发布、可真机实测）

| 步骤 | 内容 | 实测方式 |
|---|---|---|
| 0 | 双模式骨架：现有 BotEngine 原样搬进 runner，壳经管道控制 + 看门狗 | 三模式各完整跑一局；验证崩溃重启 |
| 1 | 图谱面板 v1：节点来自旧枚举，只显示当前判定 + 得分 | 挂机目检图谱高亮是否正确 |
| 2 | 探针引擎 + 新页面注册表**影子模式**：新旧分类器并行，新的只记录不生效，日志逐帧对比差异 | 对局后分析差异日志，调探针至一致 |
| 3 | 逐页迁移 MyLeague 相关约 10 页（Home/MatchHub/MyLeagueHome/Schedule/PreMatch/InMatch/Result/Shop/覆盖层） | 我的联赛完整跑通 |
| 4 | MyLeague 场景改写为目标编排式，删旧导航代码 | 我的联赛长跑（含买道具、关键比赛） |
| 5 | IntlWeekly、CnDaily 同法迁移 | 各跑一轮活动 |
| 6 | 删除旧 ScreenCatalog/分类器/散落恢复逻辑 | 三模式回归 |

步骤 2 影子模式是关键安全网：新探针体系先在真实对局陪跑，日志确认判定一致后才接管。

### 6.3 测试策略

- 沿用既有习惯：每步改完重新发布单 exe，真机双击 publish\EfootballBot.exe 实测。
- 步骤 2/3 以影子模式差异日志为客观依据。
- 步骤 0 即验证：全局停止热键（Ctrl+Shift+Q）、手柄输入活性自检、看门狗崩溃重启。

## 7. 不变量（重构中不得破坏的现有约束）

- 自包含单文件发布（见第 3 节发布命令）；CI build.yml 同步。
- 防检测节奏：非必须立即的连点走 Pad.HumanWait(min,max)，OCR 明确识别的按钮不随机化（仅 ±HumanJitterMs）。
- WGC 捕获帧高 ≤720 降采样、InMatch 帧节流 2000ms、预览最小化暂停。
- 帧引用计数生命周期规则（局部借用显式释放，Observation 自持帧 GC 管理）。
- 配置落盘 %APPDATA%\EfootballBot\config.json；老默认迁移 MigrateIdleTapDefaults 保留。
- 自动更新模块（Updater.cs + GitHub Releases + 批处理替换）不改动，只随壳迁移。
- 全局停止热键可配置、全屏可响应、触发 = engine.Stop() + ResetButtons()（壳侧转发给 runner）。
