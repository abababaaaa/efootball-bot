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

视觉语言（v7.4 起）：**两个可选「外观世界」，共用同一套版式**。

- **Fluent（默认）**：Windows 11 云母/亚克力 + 系统强调蓝（深色 #60CDFF / 浅色 #0067C0）——
  v7.3 的原始观感，老配置没有 `World` 字段时一律落在这里，不偷偷换皮肤；
- **Glass**：Apple 黑玻璃 —— 近纯黑毛玻璃 + 中性浅灰选中（不靠色相）+ 玻璃渐变卡片 + 抖色噪点。

版式、字体阶梯、圆角、间距、响应式布局、页签体系两个世界**共用**（v7.4 的统一优化）；
只有颜色 / 材质 / 投影 / 噪点 / 窗口不透明度随世界变。亮度仍三套：暗色（默认）/ 亮色 / 深蓝，
所以 token 表是 **2 世界 × 3 亮度 = 6 张**。
入口：左栏底部「主题与外观」→ 主题卡 + 外观风格卡；落盘为 `config.json` 的 `Theme` 与 `Appearance.World`。

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
| `Core/UI/ThemePalette.cs` | 6 张 token 表（2 世界 × 3 亮度）+ 世界维度派生（`ApplyWorldTokens`） | **改配色 / 加 token** |
| `Core/UI/ThemeManager.cs` | 应用 (主题, 世界)：写 token、重建渐变/投影/噪点、窗口材质与不透明度、完整性自检 | 加一个世界才动 |

**一条硬规则**：`BasedOn="{StaticResource X}"` 只认同一个 ResourceDictionary 或上游字典里的 `X`。
所以互相 BasedOn 的样式必须同文件（例：`ModuleRadio` 的三个派生样式都在 Controls.xaml）。

---

## 3. 颜色与材质：只有 token，没有字面量

### 3.0 层级（v7.4 · 两世界共用）

```
DWM 亚克力/云母          ← 系统层，窗外内容被模糊（WindowBackdropType="Acrylic"）
  WindowBg               ← 根容器：Fluent 0xF2 不透明（云母薄透）/ Glass 0x8C（黑玻璃明显透出）
    根 Grid = Transparent ← 不再叠第二层窗口底色，否则两层叠加会把透明度吃掉
      DitherNoiseBrush   ← 抖色噪点（仅 Glass）：打散 8bit 深色渐变的色带，不参与命中测试
    ContentBg            ← 内容层（保留 token；Win11 的做法是"外框玻璃 + 内容近实色"）
    TopBarBg / SideLeftBg / SideRightBg  ← 顶栏 / 左右栏：Glass 用同一张淡黑玻璃 ChromeGlass，Fluent 顶栏透明
      GlassFill（渐变）/ CardBg / Surface2  ← 卡片：Fluent 三停同色 = 平铺底色；Glass 顶亮底透 + CardShadowEffect
        SelectFill / ControlFill         ← 控件底与选中态
```

材质开关：`Appearance.Backdrop`（默认 `Acrylic`，可 `Mica`/`Tabbed`/`Auto`/`None`）。
Glass 世界强制 Acrylic（Mica 只有"纸感"，做不出透过去的黑玻璃），不支持的机器照旧回退纯色。

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
| `SelectFill` / `SelectFillStrong` / `SelectBorder` / `SelectFg` / `SelectBar` | 选中态（底 / 强底 / 描边 / 文字 / 指示条）——**世界维度** |
| `SubTabSelFill` / `SubTabSelStroke` / `ChipFill` / `ChipStroke` / `ComboBg` / `ComboPopupBg` | 页签选中、Chip、下拉选择卡 |
| `PrimaryBtnFrom/Mid/To` / `TextOnPrimary` / `PrimaryBtnStroke` / `BtnFlash` | 主按钮的渐变 / 文字 / 顶高光 / 悬停层——**世界维度** |
| `GlassFillTop/Mid/Bottom` / `CardShadowEffect` / `DitherNoiseBrush` / `TopBarBg` | 玻璃渐变 / 卡片投影 / 噪点 / 顶栏蒙版——**世界维度** |
| `Scrim` / `ScrimSoft` | 遮罩 |
| `*Color`（自动生成） | 同名颜色的 `Color` 版本，**渐变停点只能用这个** |

### 3.2 加一个 token（三步，缺一不可）

1. `Themes/Tokens.xaml` 加默认（Glass 暗色）值：
   `<SolidColorBrush x:Key="Xxx" Color="#FFxxxxxx"/>`
2. `Core/UI/ThemePalette.cs` 里 **Fluent 与 Glass 各自三张表**（共 6 张）各加一行。
3. 如果它只跟世界走（不需要每个亮度单独调色），别写 6 遍 —— 加进 `ApplyWorldTokens`，
   Fluent 侧由既有 token 派生、Glass 侧给常数，各写一次就够。

漏第 2 步不会崩，但那个主题下这块区域会退回默认值 —— 所以启动时会自检并写日志：
`主题 Token 不完整：Apple Glass（黑玻璃)/亮色 缺 Xxx…`（见 `data\app.log`）。

### 3.3 渐变怎么写

`GradientStop.Color` 只吃 `Color`，所以：

```xml
<GradientStop Color="{DynamicResource BrandFromColor}" Offset="0"/>
```

`ThemeManager` 对每个 token 同时发布 `Xxx`（Brush）与 `XxxColor`（Color）两份资源，渐变因此跟着主题走。
不要在渐变里写死十六进制 —— 那正是旧版"换主题后首页标题还是绿的"的原因。

**允许颜色字面量的三个例外**（都写在注释里，别"顺手改成 token"）：

1. 主题选择覆盖层里的三张亮度预览卡（`MainWindow.xaml` 末尾）—— 它们画的就是另外两套主题长什么样，必须固定；
2. 横幅里的白底绿字按钮 `BannerBtn`（`Templates.xaml`）—— 永远压在绿色横幅上，两世界一致。
3. 玻璃噪点图 `Assets/dither-noise.png` 本身（不是颜色，是资源）。

主按钮的顶高光描边、悬停压暗层、卡片投影这些**以前是字面量的**，v7.4 已全部 token 化
（`PrimaryBtnStroke` / `BtnFlash` / `CardShadowEffect`），换世界时不会有一块"还是白玻璃"。

### 3.4 视觉世界（World）

| 概念 | 值 | 影响范围 |
|---|---|---|
| 亮度 `Theme` | `Dark`（默认）/ `Light` / `DeepBlue` | 全部颜色 token |
| 世界 `Appearance.World` | `Fluent`（默认）/ `Glass` | 颜色 token + 渐变 + 投影 + 噪点 + 窗口不透明度 + 材质选型 |

- 两个世界**共用一份** `Controls/Surfaces/Templates/MainWindow.xaml`。所以样式里只准引用 token：
  写死 `#FF1C1C1E`（玻璃按钮上的深字）或 `Background="#FF9A9AA0"`（悬停层）都会让另一个世界变脏。
- `ThemeManager.ApplyTheme(theme, world)` 负责一次下发全部：颜色 Brush+Color、`GlassFill` 与
  `PrimaryBtnFill`（运行时按三停重建）、`CardShadowEffect`（Fluent 换成零强度投影）、
  `DitherNoiseBrush`（Fluent 换成透明）。
- 窗口不透明度在 `ApplyWindowBackdrop(window, backdrop, world)` 里：Fluent 0xF2 / Glass 0x8C。
- 想知道"当前是什么世界"：`ThemeManager.IsGlassWorld(world)`，别自己比字符串。


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

- **按钮层级**：`PrimaryBtn`（一屏一个：Fluent 实心强调色 / Glass 白玻璃渐变，文字与描边也是 token）→ `GhostBtn`（透明描边）→ `LinkBtn`（文字）。
  危险：`DangerBtn`（运行中"一键结束"）、`DangerOutlineBtn`（重置/清空类）。
- **页签**：`SegTab` + `TabTrack`（一级分段）、`SubTab`（功能分类页签，选中 = `SubTabSelFill` 灰色磨砂方框）。
- **选中态**：统一语言 = `SelectFill` 底 + `SelectBorder` 描边 + `SelectBar` 左侧细指示条 + `SelectFg` 文字。
  Fluent 下 `SelectBar` = 强调蓝、玻璃里 = 与文字同色的白条，所以两个世界都不用改样式。
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
1. `Themes/Tokens.xaml`（默认值）→ 2. `ThemePalette.cs` 同名列（Fluent / Glass 各三张）。
只跟世界走的颜色 → 只改 `ApplyWorldTokens`。页面不用动（都是 `DynamicResource`）。

### 6.1.1 加一个视觉世界
1. `ThemeManager` 加 `WorldXxx` 常量与 `Worlds` 数组；`IsGlassWorld` 改为按世界查表；
2. `ThemePalette` 加这个世界的三张表 + `ApplyWorldTokens` 里的分支（渐变/投影/噪点/主按钮）；
3. `ApplyWindowBackdrop` 里给它一个外壳不透明度与材质选型；
4. `MainWindow.xaml` 主题覆盖层加一张世界卡（`CardWorldXxx` + `HighlightWorldCard`）。
样式层**不用动** —— 前提是样式里没有颜色字面量。

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

### 2026-11-21 修「问题反馈弹窗整张白」

- **现象**：点「发现问题手动停止」→ 弹「问题反馈（可选补充说明）」窗口，整窗白底，只有输入框里一根光标，两个按钮也看不见；用户提了截图。
- **根因**：`NotePromptWindow` 直接 `new Window()`。WPF 默认 `Window.Background = SystemColors.WindowBrush`（= 白），而 App.xaml 的深色世界只落在显式写了 `Background` 的窗口（`MainWindow` / `ConnectionOverlay`）上。这个弹窗既没设 `Background` 也没设 `Foreground`，正文 `TextBlock` 又显式写了 `FontSize`，于是「白底 + 12.5px 白字」→ 只剩光标可见；两个 `Button` 同样吃了 `Controls.xaml` 里不设默认态颜色的模板，白底白字。
- **修法**（不改设计系统，只让这个窗口进入系统）：`Background` 绑 `WindowBg`、`Foreground` 绑 `TextPrimary`、`FontFamily` 绑 `UiFont`；标题用 `PageTitle`（压到 15）、正文用 `BodyText`、按钮用 `PrimaryBtn` / `GhostBtn`；窗口类换成 `Wpf.Ui.Controls.FluentWindow`（标题栏跟 WPF-UI Dark 基底走）。
- **顺手补的交互**：打开即聚焦输入框；回车 = 生成反馈包（`IsDefault`）、Esc = 取消；输入框内容顶对齐（原来是居中，多行文字浮在框中间）。
- **排查记录（只验证了编译与单测，未做真机截图）**：本机桌面截图通道对本程序无效——主窗口在 `CopyFromScreen` 下全白，`PrintWindow(PW_RENDERFULLCONTENT)` 全白（本机开了 DWM 毛玻璃外壳，抓不到 DWM 合成后的内容）；UIA 也探不到任何按钮（WPF-UI `FluentWindow` 关闭了默认可达性）。真机视觉回归要走 `tools/ui-shots.ps1` 的置顶+强制尺寸路线。
- **验证**：`dotnet build` 0 错误（3 个历史可空警告）、`dotnet test` 全量通过（含 `ArchitectureTests`）。本机 `arch-guard` 没法跑：脚本是 UTF-8 无 BOM，Windows PowerShell 5.1 按 GBK 解析会报语法错（要用 `pwsh` 7，本机 PowerShell 7 没装）；`Directory.Build.targets` 里写死调用 `pwsh`，本机必然 MSB3073/9009。这是工具链问题，不是规则违规；本次只改了 `NotePromptWindow.cs` 与本文档，不触任何 arch-guard 规则。

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