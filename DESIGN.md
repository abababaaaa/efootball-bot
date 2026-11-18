# Pitch Console —— 界面设计系统与结构

> 这份文档是界面的**施工规则**。目标只有一个：**改一个小东西，只动一个文件。**
> 以前「改个颜色 / 加个页签 / 挪个控件」要同时改 App.xaml、MainWindow.xaml、
> 两个 partial 类、导航墙和三套主题表，漏一处就是「点了没反应」或「某块颜色不跟主题走」。
> 现在按下面的分层做，每一次改动都有唯一落点。

---

## 1. 设计定位：Operate（操作台）

用户来这里是把活干完：开挂机、看状态、排故障。所以：

- **可扫读 > 好看**：字号阶梯固定、层级靠留白与发丝线，不靠彩色块。
- **状态必须一眼可读**：选中/悬停/按下/禁用四态齐全，选中态不许"若有若无"。
- **注意力只给动作**：一屏最多一个实心主按钮（PrimaryBtn）；危险操作永远描边（DangerOutlineBtn）。
- **动效克制**：切页 180ms 淡入 + 4px 上浮；按钮按压缩放到 0.97。没有弹跳、没有视差。

视觉语言（v7.3 起）：**Windows 11 Fluent**。云母/亚克力窗口材质 + 半透明分层 + 1px 发丝描边 + 4px（控件）/8px（卡片）圆角；
强调色是系统色（深色 #60CDFF / 浅色 #0067C0），文字用 Segoe UI Variable，中文回落雅黑 UI。
三套主题：暗色（默认）/ 亮色 / 深蓝，三套都必须给齐同一组 token。

---

## 2. 文件地图：哪一层负责什么

| 文件 | 职责 | 什么时候动它 |
|---|---|---|
| `App.xaml` | **只做合并**：WPF-UI Fluent 2 基底 → Tokens → Surfaces → Controls → Templates | 几乎不用动；加一层新字典才动 |
| `Themes/Tokens.xaml` | 颜色 + 阴影 + 渐变用的 `Color` 资源 | **改配色 / 加颜色** |
| `Themes/Surfaces.xaml` | 容器与文字：Card / Inset / Hairline / Chip / 全部 TextBlock 样式 | 改字号阶梯、加一种文字样式 |
| `Themes/Controls.xaml` | 交互控件：按钮 / 页签 / 勾选 / 输入框 / 下拉 / 滚动条 / 进度条 / ToolTip | **改控件观感** |
| `Themes/Templates.xaml` | 页面级局部样式：上栏页签 TopNavTab、横幅按钮 BannerBtn、网络表格行 NetRowStyle… | 改某个局部元素 |
| `MainWindow.xaml` | 外壳：顶栏 / 左栏导航 / 右栏实时辅助 / 中栏容器（3.3k 行，内容页正在陆续搬出） | 改外壳布局 |
| `Views/*.xaml` | 独立页面（UserControl），自带 code-behind | **加/改一个页面** |
| `MainWindow.Shell.cs` | **路由表**：一个页面在壳里的全部接线，一行数据 | **加一个页面 / 改导航** |
| `Core/UI/ThemePalette.cs` | 三套主题的 token 值（纯数据） | **改配色 / 加 token** |
| `Core/UI/ThemeManager.cs` | 应用主题、窗口材质、token 完整性自检 | 极少动 |

**一条硬规则**：`BasedOn="{StaticResource X}"` 只认同一个 ResourceDictionary 或上游字典里的 `X`。
所以互相 BasedOn 的样式必须同文件（例：`ModuleRadio` 的三个派生样式都在 Controls.xaml）。

---

## 3. 颜色与材质：只有 token，没有字面量

### 3.0 层级（v7.3 · Win11）

```
DWM 亚克力/云母          ← 系统层，窗外内容被模糊（WindowBackdropType="Acrylic"）
  WindowBg 95%           ← 根容器：整窗底色，材质从它后面透出来
    ContentBg            ← 内容层（保留 token；Win11 的做法是"外框玻璃 + 内容近实色"）
    SideLeftBg / SideRightBg  ← 左右栏：更暗的半透明层
      CardBg / Surface2  ← 卡片：5–8% 白叠加 + 1px CardBorder
        ControlFill      ← 控件底（按钮/输入/选中态）
```

材质开关：`Appearance.Backdrop`（默认 `Acrylic`，可 `Mica`/`Tabbed`/`Auto`/`None`）。

页面里**一律**写 `{DynamicResource AccentBg}` 这种 token 名，不写 `#FF0FA968`。
`#RRGGBB` 只允许出现在两个地方：`Themes/Tokens.xaml` 与 `Core/UI/ThemePalette.cs`。

### 3.1 命名规则

| 前缀/名字 | 用途 |
|---|---|
| `WindowBg` / `CardBg` / `Surface2` / `InsetBg` / `TrackBg` | 底色分层（外层 → 内层） |
| `SideLeftBg` / `SideRightBg` / `NavSectionBg` | 左右栏 |
| `TextPrimary` / `TextSecondary` / `TextMuted` / `LogInfo` | 文字三级 + 日志 |
| `Accent` / `AccentHover` / `AccentGlow` / `AccentLight` / `AccentBorder` / `AccentSoft` | 主题色（悬停 / 高亮 / 浅底 / 描边） |
| `Danger*` / `Warning` | 危险 / 警告 |
| `Info` / `Amber` / `Orange` / `Teal` / `Success` | 状态点与模块色条 |
| `Banner*` / `Hero*` / `Brand*` | 首页英雄区与横幅渐变 |
| `Divider` / `HairlineStrong` / `CardBorder` / `InputBorder` | 描边三级 |
| `Scrim` / `ScrimSoft` | 遮罩 |
| `*Color`（自动生成） | 同名颜色的 `Color` 版本，**渐变停点只能用这个** |

### 3.2 加一个 token（两步，缺一不可）

1. `Themes/Tokens.xaml` 加默认（暗色）值：
   `<SolidColorBrush x:Key="Xxx" Color="#FFxxxxxx"/>`
2. `Core/UI/ThemePalette.cs` 的 `DarkColors` / `LightColors` / `DeepBlueColors` **各加一行**。

漏第 2 步不会崩，但那个主题下这块区域会退回暗色值 —— 所以启动时会自检并写日志：
`主题 Token 不完整：亮色 缺 Xxx…`（见 `data\app.log`）。

### 3.3 渐变怎么写

`GradientStop.Color` 只吃 `Color`，所以：

```xml
<GradientStop Color="{DynamicResource BrandFromColor}" Offset="0"/>
```

`ThemeManager` 对每个 token 同时发布 `Xxx`（Brush）与 `XxxColor`（Color）两份资源，渐变因此跟着主题走。
不要在渐变里写死十六进制 —— 那正是旧版"换主题后首页标题还是绿的"的原因。

**允许颜色字面量的三个例外**（都写在注释里，别"顺手改成 token"）：

1. 主题选择覆盖层里的三张预览卡（`MainWindow.xaml` 末尾）—— 它们画的就是另外两套主题长什么样，必须固定；
2. 实心按钮顶部内高光 `#24FFFFFF / #00FFFFFF`（`Controls.xaml` PrimaryBtn）—— 与主题无关的中性高光；
3. 横幅里的白底绿字按钮 `BannerBtn`（`Templates.xaml`）—— 永远压在绿色横幅上，三套主题一致。

除此之外，页面/字典里出现 `#RRGGBB` 就是 bug。当前基线：`MainWindow.xaml` 24 处（全部在预览卡内），
`Surfaces/Controls/Templates/Views` 共 4 处（上面 2、3 两条）。

---

## 4. 字体阶梯（禁止页面里手写 FontSize）

| 样式 | 字号 / 字重 | 用途 |
|---|---|---|
| `PageTitle` | 19.5 / SemiBold | 页面主标题 |
| `PageSub` | 12.5 / Normal，TextMuted，LineHeight 19，MaxWidth 560 | 标题下一句用途说明 |
| `H2` | 14.5 / SemiBold | 段标题（配 `CardTitleBar` 竖条） |
| `CardTitle` | 13.5 / SemiBold | 小卡片标题 |
| `CardSub` | 11.5 / Normal，TextMuted | 卡片补充说明 |
| `GroupLabel` | 13 / SemiBold | 段标题（首页目录 / 运行设置…） |
| `SubHead` | 12.5 / SemiBold | 卡内小节 |
| `BodyText` | 12.5 / Normal，LineHeight 21 | 说明段落 |
| `SmallText` | 12 / Normal，LineHeight 19.5 | 表单说明、要点 |
| `BulletText` | 12 / Normal，LineHeight 19 | 要点行 |
| `MonoText` | 12 / Cascadia Mono, Consolas | 日志、路径 |
| `MetricValue` / `MetricLabel` | 20 SemiBold（等宽数字）/ 11.5 Muted | 统计格数字与标签 |
| `SectionLabel` | 11 / SemiBold，NavLabelFg | 左栏分组标题 |

> 2026-11-18 全线上调一档：1280×800 下 11~11.5px 的中文在 ClearType 里发糊，
> 正文/小字统一到 12~12.5、行高 19~21；标题从 18 到 19.5，层级靠字重与留白拉开，不靠加粗堆叠。

---

## 5. 控件清单（都在 `Themes/Controls.xaml`）

- **按钮层级**：`PrimaryBtn`（实心主题色，一屏一个）→ `GhostBtn`（透明描边）→ `LinkBtn`（文字）。
  危险：`DangerBtn`（运行中"一键结束"）、`DangerOutlineBtn`（重置/清空类）。
- **页签**：`SegTab` + `TabTrack`（一级分段）、`SubTab`（左栏三级页签，选中 = 主题色底 + 主题色描边）。
- **开关**：默认 `CheckBox` 是胶囊拨片（开 = 白轨 + 深滑块）；`CheckTick` 是方框对勾。
- **输入**：`TextBox`（聚焦亮边 1.5px）、`ComboBox`（自绘下拉面板 + 浮层阴影）。
- **容器**：`Card`（扁平分区：只留上发丝线，不画闭合框）、`Inset`（凹槽）、`RailCard`（操作栏）、`StatTile`（右栏统计格）、`Chip`（胶囊标签）。
- **滚动**：默认 `ScrollBar` 常显（日志用）；`PageScroll` 是悬浮细条（页面用，静止不可见）。
- **表格**：`NetRowStyle` / `NetHeaderStyle`（ListView）、`NetDataHeader` / `NetDataCell` / `NetDataRow`（DataGrid）。

**状态规则**：一个控件的默认/悬停/按下/选中/禁用**四态写在同一个 ControlTemplate 里**。
不要用触发器从外面改别人的属性（曾经的"点了没反应""悬停变白底看不见字"都出自这里）。

---

## 6. 加东西的配方（照抄即可）

### 6.1 改一个颜色
1. `Themes/Tokens.xaml`（默认 = 暗色）→ 2. `ThemePalette.cs` 同名列三套值。
页面不用动（都是 `DynamicResource`）。

### 6.2 改一个控件的观感
只动 `Themes/Controls.xaml` 里那一个 `Style`。全应用所有用到它的地方一起变。

### 6.3 加一个页面（以「关于」为样板）

1. **建视图**：`Views/XxxPage.xaml` + `.xaml.cs`（`UserControl`）。
   页面自己的命名元素、事件处理、局部样式全留在这里；**不要**回 `MainWindow.*.cs` 加代码。
   页内样式优先用 App 级样式（`Card` / `H2` / `BulletText`…），页面私有样式放 `UserControl.Resources`。
2. **登记路由**（`MainWindow.Shell.cs`，一行）：
   ```csharp
   new() {
       Mode = NavMode.Xxx, Section = TopSection.Tools,
       Panels = [Slot(PageXxx)],           // 互斥面板可以放多个 Slot(..., () => 条件)
       RailButton = BtnXxx,                // 左栏高亮
       SubMenus = [Slot(SubMenuXxx)],      // 有三级页签就加
       OnEnter = RefreshXxx,               // 进入时的一次性刷新
   },
   ```
3. **加枚举值**：`Shell/NavRoute.cs` 的 `NavMode`（要支持 `--page` 深链就在 `NavKey.Aliases` 加别名）。
4. **外壳里放容器**：`MainWindow.xaml` 中栏 `Grid.Row="2"` 放
   `<views:XxxPage x:Name="PageXxx" Grid.Row="2" Visibility="Collapsed"/>`。
   **不用**写 `Visibility` 之外的任何接线 —— 那是路由表的事。
5. **左栏按钮**：`<Button x:Name="BtnXxx" Style="{StaticResource NavToolBtn}" Click="BtnXxx_Click">`，
   `Click` 里只写 `SaveUiToConfig(); NavigateTo(NavMode.Xxx);`。

新增页面**不需要**：改 `NavigateTo` 的 Visibility 墙、改 `ShowByTag`、改 `SyncSubTabs`、改分区显隐。
（这些墙都已经删掉，换成表驱动了。）

### 6.4 加一个上栏一级分区
`Shell/NavRoute.cs` 的 `TopSection` 加值 → `MainWindow.Shell.cs` 的 `_sectionMembers` 加一行 →
`MainWindow.xaml` 顶栏加一个 `TopNavTab` 页签 → `TopNav_Checked` 加一个 `case`。

### 6.5 加一个设置项（目前仍是 4 处）
1. `Core/Config/AppConfig.cs`：字段 + 默认值；
2. `MainWindow.xaml`：控件（`ChkXxx` / `TxtXxx`），放在对应二级页；
3. `MainWindow.Settings.cs` → `LoadConfigToUi()`：`ChkXxx.IsChecked = _cfg.Xxx;`
4. `MainWindow.Settings.cs` → `SaveUiToConfig()`：`_cfg.Xxx = ChkXxx.IsChecked == true;`

> 这是本仓库最后一处"两堵平行的墙"（`LoadConfigToUi` / `SaveUiToConfig` 各约 190 行）。
> 下一步可改成 `SettingsBinder` 表驱动（一行登记一个绑定，Load/Save 自动遍历），
> 但要成片替换并逐项回归，别只改一半 —— 两种写法混在一个文件里更难维护。

---

## 7. 改完怎么验（必须做，别只看代码）

```powershell
# 1) 编译
dotnet build src\EfootballBot\EfootballBot.csproj -c Debug -v q

# 2) 深链截图：一条命令把任意页面开到指定状态（应用需要管理员权限，
#    所以不能靠模拟点击，只能靠 --page 内部跳转）
pwsh -File tools\ui-shots.ps1 -Exe "src\EfootballBot\bin\Debug\net10.0-windows10.0.22621.0\win-x64\EfootballBot.exe" `
     -OutDir "_reference\shots\check" -Pages "default,game,settings,network,about"

# 3) 局部放大看细节（选中态、裁切、对齐）
pwsh -File tools\ui-crop.ps1 -In "_reference\shots\check\page-settings.png" `
     -Out "_reference\shots\check\tabs.png" -X 240 -Y 58 -W 520 -H 40 -Scale 3
```

`--page` 认可的值：`home | game | settings | update | github | network | logs | feedback | about`，
可带二级段：`settings:display`、`settings:input`、`settings:daily`、`settings:dev`、
`network:2`、`github:1`、`update:1`、`feedback:1`、`game:cn`。

检查清单（每次改 UI 都过一遍）：
- [ ] 暗色 / 亮色 / 深蓝三个主题各看一遍（改 `data\config.json` 的 `Theme` 后重启）
- [ ] 文字没有被栏宽裁掉（左栏 212px、卡片最窄 ~380px）
- [ ] 一排页签里"当前在哪一页"一眼可辨
- [ ] 悬停/按下/禁用都有反馈，禁用态看得懂
- [ ] 窄窗（最小 1140×640）下不重叠、不被裁

---

## 8. 变更记录

### v7.3.0 换视觉世界：Windows 11 Fluent + 毛玻璃（纯视觉，功能零改动）

- **世界**：从自建的「Pitch Console（深蓝黑 + 翡翠绿）」换成 Windows 11 Fluent —— 系统材质（亚克力）+ 系统强调色 + Fluent 的控件几何。**保留**产品真相、页面结构、全部控件名与事件。旧世界只作为证据，不再打蜡。
- **材质**：窗口 `WindowBackdropType="Acrylic"`（`Appearance.Backdrop` 默认 `Acrylic`，可切 Mica/Tabbed/None）；运行时 `ThemeManager.ApplyWindowBackdrop` 把 `WindowBg` 换成 95% 不透明 + 系统亚克力底 → 「外框玻璃 + 内容可读」同时成立；左右栏再叠 25–40% 黑/白半透明层，卡片叠 5–8% 白。
- **颜色**：强调色换系统色（深 `#60CDFF` 上压深字 `#003544` / 浅 `#0067C0` 上压白字）；中性三档 `#FFFFFF / #C7C7C7 / #9A9A9A`（浅色 `#1A1A1A / #5C5C5C / #6E6E6E`）；分隔线 = 9–14% 白色（浅色 8% 黑）。新增 token：`ContentBg`/`GlassBg`/`GlassStroke`/`ControlFill`/`ControlStroke`。
- **几何**：控件圆角 4、卡片 8、胶囊 999；按钮 32 高（主按钮实心强调色、标准按钮 `ControlFill + ControlStroke`）；左栏导航 32 高 + 3px 强调色指示条（NavigationView 的家法）；顶栏/分段页签改 Win11 枢轴（选中 = 3px 强调色下划线，不再靠荧光绿认位）。
- **字体**：`Segoe UI Variable Display`（标题）/ `Segoe UI Variable Text`（正文，中文回落雅黑 UI）/ `Cascadia Mono`（数字与日志，等宽对齐）。
- **动效**：切页 180ms 淡入保留；按钮按下 0.97 缩放保留。没有新增装饰动画。
- 修：`tools/ui-shots.ps1` 增加「按进程找最大可见窗口 + 强制尺寸 + 置顶后截图」——旧脚本会截到压在窗口上的其它应用，看起来像「界面只有半张」。
- 验证：构建 0 错误、全量测试通过、impeccable 检测器 0 findings；三套主题截图核对。

### v7.2.1 界面重构（v7.2.0 之后，纯视觉；功能零改动）

**取景**：Operate（操作台）不变，深蓝黑 + 翡翠绿的世界保留；这次改的是「精度」——字号、间距、层级、状态。

- 颜色（`Tokens.xaml` + `ThemePalette.cs`）：底色再深一档（`WindowBg #0A0E13`），中性三级文字提对比（`TextMuted #8A94A4`，浅底 12px 小字≥4.5:1），翡翠绿提亮（`Accent #12B472`）；亮色/深蓝同步修 `TextMuted` 与分隔线
- 字体阶梯（`Surfaces.xaml`）：全线上调一档（见 §4），行高统一（正文 21 / 小字 19.5），新增 `MetricValue`/`MetricLabel`（等宽数字，位数不跳动）
- 间距资源：`PagePad`/`SectionGap`/`HeadGap`/`FieldGap` 四个 `Thickness` token；`Card` 上留白 15→20、段间距 16→24
- 首页重做：删掉标题上的 kicker 胶囊与渐变字（`craft-floor` 明令禁止），改成「一句话定位 + 两个动作 + 两栏模块目录（国际服 / 国服+工具）」的行式入口；版本号移到页脚右下
- 左栏：导航行统一 34px / 圆角 9；**删掉导航项左侧 3px 绿色竖条**（>1px 的彩色左边框是明确禁令），选中态改成主题色浅底；环境检测从「卡片里的列表」改成发丝线 + 状态行（左栏最贵的是垂直空间）
- 控件（`Controls.xaml`）：主按钮圆角 10、最小高度 38；幽灵/描边按钮 34；分段页签选中态 = `Surface2` 底 + 主题色描边 + 主文字色（不再靠荧光绿认位）；统计格加发丝描边、圆角 10；滚动条滑块加宽提亮
- 顶栏：加底部发丝线，一级页签选中 = 主文字色 + 2px 主题色下划线（原荧光绿文字改为下标线）
- 文案层：**全应用删掉「★」字符**（用 Unicode 符号冒充图标是禁令），推荐项靠位置与留白表达
- 修：字号上调后「作息调度」时间输入框裁字（56→72 / 46→58）
- 关于页：补页面标题与副标题，正文收窄到 760px（原来一行铺满 900+，读不成段）
- 验证：暗色/亮色/深蓝三套主题截图逐页对比；impeccable 检测器 0 findings；构建 0 错误、全量测试通过

### v7.2.0 挂机模块化（逻辑层；界面规则未变）
数据面在 `docs/MODULES.md`，这里只记「施工规则」的延伸：**界面之外也有唯一落点**。

- 模块清单唯一落点：`Core/Bot/Modules/BotModuleCatalog.cs`（一张表 = 国服/国际服分组 + 场景/工具 + 工厂 + 可接力）。
  改模块归属、加模块、调接力白名单，只动这张表和 `ModuleIds.cs`
- 共享能力唯一落点：`Core/Bot/Match/`（界面分类 / 共享决策 / 赛中匹配 `MatchPlay` / 流程记忆 / 像素探针 / 弹窗判定）。
  跨模块要用的东西一律下沉到这里，*不许*再 `using` 另一个模块的命名空间
- 界面侧同步：模块「开始」按钮的处理函数只写 `ModuleIds.Xxx`，不再出现 `"intl"`、`"cnweekly"` 之类字面量；
  设置页「全部完成后接力」的下拉候选由注册表算出（`BotModuleCatalog.HandoffCandidates`），不再手写两张表
- 自检：`ModuleCatalogTests` 挡结构性错误（重复 id / 场景缺工厂 / 接力目标跨服 / 常量未登记），
  与界面那边的「主题 token 完整性自检」是同一套思路
- **写法锁定（构建期强制）**：`Directory.Build.targets` 在编译前跑 `tools/arch-guard/arch-guard.ps1`，
  7 条分层规则违反即编译失败；三类层文件（注册表 / 共享层 / 模块层）文件头带「架构锁」横幅，
  写明本层职责与禁止事项。改写法必须先由用户明确要求，并同步 `AGENTS.md` 与 `docs/MODULES.md` §7

### v7.1.4 界面结构重构
- `App.xaml` 923 行 → 27 行合并入口；样式拆成 `Themes/{Tokens,Surfaces,Controls,Templates}.xaml`
- 主窗口 `Window.Resources` 里 9 个页面级样式搬到 App 级（UserControl 页也能用）
- 导航从「`NavigateTo` 里 120 行手写 Visibility」改成 `MainWindow.Shell.cs` 表驱动路由
- 「关于」页抽成 `Views/AboutPage.xaml`（新页面的样板）
- 调色板数据搬到 `Core/UI/ThemePalette.cs` + 三套主题 token 完整性自检
- 页面里 60+ 处硬编码颜色收口成 token（含渐变 `*Color` 资源），亮色/深蓝不再有"漏网的一块"
- 修：首页版本芯片长期写死 `v6.2`（改为程序集版本）
- 修：左栏环境检测长文案被 212px 栏宽裁掉（改 Grid + 换行）
- 修：设置页三级页签选中态与窗口底色同色 → 现在主题色底 + 描边
- 新增 `--page` 深链 + `tools/ui-shots.ps1` / `ui-crop.ps1`（截图回归）