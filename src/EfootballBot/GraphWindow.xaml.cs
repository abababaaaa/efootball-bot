using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Text;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Vision;

namespace EfootballBot;

/// <summary>
/// 实时图谱窗口：总分总结构。
/// 总：顶部编号步骤链（① 启动评估 → ② 寻路进入 → …），当前步骤绿色脉冲；
/// 分：点击任一步骤卡片，中部展开该步骤细看（目的/画面/动作/验证/失败处理）；
/// 总：底部实时判定依据（当前画面、探针候选、导航、比赛阶段）。
/// 画面事件按 GameScreen → 步骤映射驱动高亮；手动点击后 8 秒内不自动跳转详情。
/// </summary>
public partial class GraphWindow : Window
{
    /// <summary>流程步骤定义（一个步骤 = 一组画面 + 细看说明）。</summary>
    private sealed class StepDef
    {
        public string Title = "";
        public string Goal = "";
        public string[] Screens = Array.Empty<string>();
        public string[] Actions = Array.Empty<string>();
        public string[] Verify = Array.Empty<string>();
        public string[] Fallback = Array.Empty<string>();
        public GameScreen[] Map = Array.Empty<GameScreen>();
    }

    private readonly List<(StepDef Step, Border Card)> _cards = new();
    private readonly Dictionary<GameScreen, StepDef> _screenMap = new();
    private StepDef? _currentStep;
    private StepDef? _selectedStep;
    private DateTime _manualSelectAt = DateTime.MinValue;
    private string _mode = "myleague";
    private bool _suppressModeEvent;

    public GraphWindow()
    {
        InitializeComponent();
        BuildSteps();
    }

    // ---------------- 步骤定义（总分总的"分"） ----------------

    /// <summary>我的联赛模块：完整八个步骤 + 回环。</summary>
    private static StepDef[] LeagueSteps() => new[]
    {
        new StepDef
        {
            Title = "启动评估",
            Goal = "点击开始后先观察识别当前游戏内状态（什么画面/比赛阶段/流程），再决定入口，绝不盲目寻路。",
            Screens = new[] { "任意启动画面（探针+OCR+AI图标+遮罩综合判定）" },
            Actions = new[] { "观察一帧 → 输出启动状态报告（状态/探针/证据）", "未知画面等 2.5 秒二次确认，仍不明才进入寻路" },
            Verify = new[] { "比赛流程（赛前/比赛中/中场/全场结束/结果/奖励）→ 状态机从当前阶段直接接管", "我的联赛内部页面（主页/赛程表/兑换积分/商店/弹窗）→ 状态机直接接管" },
            Fallback = new[] { "完全未识别 → 步骤② 寻路", "比赛中误判被赛后门控/afterMatchEnd 词表否决" },
            Map = Array.Empty<GameScreen>(),
        },
        new StepDef
        {
            Title = "寻路进入",
            Goal = "把画面从任意外部位置（标题/主菜单/活动页）导航到我的联赛主页。已在联赛内则跳过。",
            Screens = new[] { "标题画面", "主页", "比赛菜单", "活动详情" },
            Actions = new[] { "B 逐层退出 + 方向导航 + 页面验证，逐步推进" },
            Verify = new[] { "到达：探针 MyLeagueHome 且主页双卡词齐（前往比赛+赛程表）" },
            Fallback = new[] { "导航超时/未到达 → 状态机从当前页面接管兜底" },
            Map = new[] { GameScreen.TitleScreen, GameScreen.Home, GameScreen.MatchHub, GameScreen.ActivityDetail },
        },
        new StepDef
        {
            Title = "主页检查",
            Goal = "回到我的联赛主页后先清弹窗、再检查道具是否齐全——检查没跑完绝不允许开赛。",
            Screens = new[] { "我的联赛主页", "赛程表", "各类弹窗（物品过期/奖励/确认）" },
            Actions = new[] { "弹窗：移光标到确认按钮再点击（识别到 OK/确定先移过去）", "首轮道具检查：RsFlick Right 切「使用的物品」面板 → 逐项判定缺口" },
            Verify = new[] { "主页判定：前往比赛+赛程表双词齐 且 无弹窗遮罩", "边沿触发：上一轮不是主页 → 重置检查状态重新查" },
            Fallback = new[] { "检查失败重试 ≤3 次；画面被搅乱 → 状态机重新判页", "缺道具 → 步骤④ 兑换积分" },
            Map = new[] { GameScreen.MyLeagueHome, GameScreen.Schedule, GameScreen.Dialog },
        },
        new StepDef
        {
            Title = "兑换积分",
            Goal = "从主页进入兑换积分购买缺失道具。导航全程审核焦点卡，确认无误才点击。",
            Screens = new[] { "我的联赛主页（焦点审核）", "兑换积分落地页（我的联赛积分+租借球员）", "商店（我的联赛物品）", "购买确认" },
            Actions = new[] { "锚定「前往比赛」（Up×3+Left 纯夹紧）→ Down 到赛程表", "循环审核：FocusCardDetector 识别焦点卡 → 确认「兑换积分」才按 A", "商店内：焦点移至目标物品（杰出=Down×2 / 4倍经验=Down×5 / 主教练强化=Down×12）→ A → 确认" },
            Verify = new[] { "落地页双词验证：我的联赛积分+租借球员（OCR 异体字折叠）", "商店页验证：我的联赛物品/进入商店" },
            Fallback = new[] { "进错页按 B 退回主页重试 ≤3 次；绝不用「页面有变化」当成功", "主页闸门：画面已是干净主页时终止一切推进防误点前往比赛" },
            Map = new[] { GameScreen.Shop, GameScreen.ShopConfirm },
        },
        new StepDef
        {
            Title = "开赛流程",
            Goal = "道具齐全后从主页进入比赛，直到比赛正式开始。",
            Screens = new[] { "我的联赛主页（前往比赛）", "赛前准备页（开始比赛/难度/首发阵容）", "关键比赛徽章" },
            Actions = new[] { "锚定高光回「前往比赛」（Up×3+Left）→ A", "难度确认 → 首发阵容页（比赛开始时显示的内容）→ 蓝按钮开始比赛" },
            Verify = new[] { "PreMatch 必须命中赛前专属词（开始比赛/选择比赛难度/比赛条件/选手操作/首发阵容）", "蓝按钮检测（y≈0.93/0.98）先移光标再点击" },
            Fallback = new[] { "探针判 PreMatch 但无专属词 → 不采信（防主页残留词误判开赛）" },
            Map = new[] { GameScreen.PreMatch, GameScreen.KeyMatchBadge },
        },
        new StepDef
        {
            Title = "比赛中",
            Goal = "AI 代打自动踢完上下半场，收集比赛信息并防卡死。",
            Screens = new[] { "比赛中", "中场" },
            Actions = new[] { "AI 代打：检测顶栏蓝横幅（RT），丢失时重发指令", "跳过比赛：HUD「跳过比赛」绿勾像素检测", "智能启停：进球/死线/比分驱动节奏 A 与方向" },
            Verify = new[] { "OCR 间隔自适应（赛中词变化驱动轮询快慢）" },
            Fallback = new[] { "中场自动继续下半场" },
            Map = new[] { GameScreen.InMatch, GameScreen.HalfTime },
        },
        new StepDef
        {
            Title = "全场结算",
            Goal = "比赛结束画面：锚定左上「下一步」逐步推进，走完整场结算链。",
            Screens = new[] { "全场结束（下一步/建议/球员评价）", "比赛结果" },
            Actions = new[] { "每轮 上×2+左×1 夹紧回「下一步」初始位 → A 推进（RS 翻页/球员评价/经验页）" },
            Verify = new[] { "左列双卡专属词（下一步+建议+球员评价共现）强判 FullTime" },
            Fallback = new[] { "此页面及之后一律忽略 AI 图标、绝不记为比赛中（智能启停同受控）" },
            Map = new[] { GameScreen.FullTime, GameScreen.Result },
        },
        new StepDef
        {
            Title = "赛后奖励",
            Goal = "奖励/经验页推进 → 清赛后弹窗 → 回主页，循环回步骤③重新检查道具。",
            Screens = new[] { "奖励页", "赛后过渡（回放/弹窗/获得积分/收件箱）" },
            Actions = new[] { "奖励页推进 → 赛后过渡门控：无词画面有蓝按钮按 A 跳过、无则等待", "弹窗显式识别：移光标到确认按钮再点击" },
            Verify = new[] { "回主页验证（前往比赛+赛程表）→ 门控复位，道具检查重新开始" },
            Fallback = new[] { "门控期间一律不判比赛中（图标与探针 hold 不采信）；3 分钟超时兜底" },
            Map = new[] { GameScreen.Rewards, GameScreen.Loading },
        },
    };

    /// <summary>国际服活动模块：七步。</summary>
    private static StepDef[] WeeklySteps() => new[]
    {
        new StepDef
        {
            Title = "启动评估", Goal = "点击开始先观察识别当前画面状态，再决定入口。",
            Screens = new[] { "任意启动画面" },
            Actions = new[] { "观察一帧 → 按状态分流；未知二次确认后才寻路" },
            Verify = new[] { "外部画面 → 寻路；活动页/比赛 → 直接接管" }, Fallback = Array.Empty<string>(),
            Map = Array.Empty<GameScreen>(),
        },
        new StepDef
        {
            Title = "寻路进入", Goal = "导航到国际服活动中心。",
            Screens = new[] { "标题画面", "主页", "比赛菜单" },
            Actions = new[] { "B 逐层退出 + 方向导航 + 页面验证" },
            Verify = new[] { "到达活动中心（EventHub）" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.TitleScreen, GameScreen.Home, GameScreen.MatchHub },
        },
        new StepDef
        {
            Title = "活动检查", Goal = "进入活动中心确认目标活动与入口。",
            Screens = new[] { "活动中心", "活动详情", "无活动" },
            Actions = new[] { "确认活动入口 → 进入赛事" },
            Verify = new[] { "活动信息词验证" }, Fallback = new[] { "无活动 → 结束本轮" },
            Map = new[] { GameScreen.EventHub, GameScreen.EventInfo, GameScreen.NoEvent, GameScreen.ActivityDetail },
        },
        new StepDef
        {
            Title = "开赛流程", Goal = "难度确认与开赛。",
            Screens = new[] { "赛前准备页" },
            Actions = new[] { "确认难度 → 开始比赛" },
            Verify = new[] { "PreMatch 专属词" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.PreMatch, GameScreen.KeyMatchBadge },
        },
        new StepDef
        {
            Title = "比赛中", Goal = "AI 代打自动踢完比赛。",
            Screens = new[] { "比赛中", "中场" },
            Actions = new[] { "蓝横幅/跳过绿勾/智能启停" },
            Verify = new[] { "OCR 间隔自适应" }, Fallback = new[] { "中场自动继续" },
            Map = new[] { GameScreen.InMatch, GameScreen.HalfTime },
        },
        new StepDef
        {
            Title = "全场结算", Goal = "全场结束/结果推进。",
            Screens = new[] { "全场结束", "比赛结果" },
            Actions = new[] { "锚定「下一步」→ A 推进" },
            Verify = new[] { "赛后词优先于 AI 图标" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.FullTime, GameScreen.Result },
        },
        new StepDef
        {
            Title = "活动奖励", Goal = "活动板块（CampaignBoard）领奖后回活动中心循环。",
            Screens = new[] { "活动板块" },
            Actions = new[] { "推进领奖 → 回活动中心" },
            Verify = new[] { "回到 EventHub 验证" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.CampaignBoard, GameScreen.Rewards, GameScreen.Loading },
        },
    };

    /// <summary>国服每日模块：七步。</summary>
    private static StepDef[] CnSteps() => new[]
    {
        new StepDef
        {
            Title = "启动评估", Goal = "点击开始先观察识别当前画面状态，再决定入口。",
            Screens = new[] { "任意启动画面" },
            Actions = new[] { "观察一帧 → 按状态分流；未知二次确认后才寻路" },
            Verify = new[] { "外部画面 → 寻路；挑战页/比赛 → 直接接管" }, Fallback = Array.Empty<string>(),
            Map = Array.Empty<GameScreen>(),
        },
        new StepDef
        {
            Title = "寻路进入", Goal = "导航到国服每日挑战列表。",
            Screens = new[] { "标题画面", "主页", "活动中心" },
            Actions = new[] { "B 逐层退出 + 方向导航 + 页面验证" },
            Verify = new[] { "到达挑战列表（ChallengeList）" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.TitleScreen, GameScreen.Home, GameScreen.EventHub },
        },
        new StepDef
        {
            Title = "挑战检查", Goal = "确认每日挑战目标与奖励状态。",
            Screens = new[] { "挑战列表", "挑战主页", "每日奖励" },
            Actions = new[] { "领取已达成奖励 → 选择可打挑战" },
            Verify = new[] { "挑战页词验证" }, Fallback = new[] { "无可打挑战 → 结束" },
            Map = new[] { GameScreen.ChallengeList, GameScreen.ChallengeHome, GameScreen.DailyRewards, GameScreen.ActivityDetail },
        },
        new StepDef
        {
            Title = "开赛流程", Goal = "赛事设置确认与开赛。",
            Screens = new[] { "赛事设置", "赛前准备页" },
            Actions = new[] { "确认设置 → 开始比赛" },
            Verify = new[] { "PreMatch 专属词" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.EventSettings, GameScreen.PreMatch, GameScreen.KeyMatchBadge },
        },
        new StepDef
        {
            Title = "比赛中", Goal = "AI 代打自动踢完比赛。",
            Screens = new[] { "比赛中", "中场" },
            Actions = new[] { "蓝横幅/跳过绿勾/智能启停" },
            Verify = new[] { "OCR 间隔自适应" }, Fallback = new[] { "中场自动继续" },
            Map = new[] { GameScreen.InMatch, GameScreen.HalfTime },
        },
        new StepDef
        {
            Title = "全场结算", Goal = "全场结束/结果推进。",
            Screens = new[] { "全场结束", "比赛结果" },
            Actions = new[] { "锚定「下一步」→ A 推进" },
            Verify = new[] { "赛后词优先于 AI 图标" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.FullTime, GameScreen.Result },
        },
        new StepDef
        {
            Title = "奖励回环", Goal = "领奖后回挑战列表继续下一个挑战。",
            Screens = new[] { "奖励页" },
            Actions = new[] { "推进领奖 → 回挑战列表" },
            Verify = new[] { "回到 ChallengeList 验证" }, Fallback = Array.Empty<string>(),
            Map = new[] { GameScreen.Rewards, GameScreen.Loading },
        },
    };

    private static readonly Dictionary<string, Func<StepDef[]>> ModeSteps = new()
    {
        ["myleague"] = LeagueSteps,
        ["intlweekly"] = WeeklySteps,
        ["cndaily"] = CnSteps,
    };

    /// <summary>回环说明（总收尾）：模块 → 循环回哪一步。</summary>
    private static readonly Dictionary<string, (int StepNo, string Text)> ModeLoop = new()
    {
        ["myleague"] = (3, "↺ 每场比赛结束回到「③ 主页检查」，重新清弹窗+查道具，再开下一场（直到目标场次）"),
        ["intlweekly"] = (3, "↺ 每场结束回「③ 活动检查」，继续下一个活动赛事"),
        ["cndaily"] = (3, "↺ 每场结束回「③ 挑战检查」，继续下一个每日挑战"),
    };

    // ---------------- 构建（总） ----------------

    private void BuildSteps()
    {
        StepsHost.Children.Clear();
        DetailHost.Children.Clear();
        _cards.Clear();
        _screenMap.Clear();
        _currentStep = null;
        _selectedStep = null;

        var steps = ModeSteps[_mode]();
        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            foreach (var s in step.Map) _screenMap[s] = step;
            _cards.Add((step, MakeStepCard(i, step)));

            if (i > 0) StepsHost.Children.Add(MakeArrowText());
            StepsHost.Children.Add(_cards[^1].Card);
        }

        // 总收尾：回环徽章（短文字保单页显示，完整说明放 ToolTip）
        if (ModeLoop.TryGetValue(_mode, out var loop))
        {
            StepsHost.Children.Add(MakeArrowText());
            var badge = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1.5),
                ToolTip = loop.Text,
                Child = new TextBlock
                {
                    Text = $"↺ 回{char.ConvertFromUtf32(0x2460 + loop.StepNo - 1)}",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "CardBg");
            badge.SetResourceReference(Border.BorderBrushProperty, "ModuleDot");
            badge.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            StepsHost.Children.Add(badge);
        }

        // 默认展开第一步
        SelectStep(steps[0], manual: false);
    }

    private Border MakeStepCard(int index, StepDef step)
    {
        var number = char.ConvertFromUtf32(0x2460 + index); // ①②③…
        var card = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Tag = step,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = number, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = " " + step.Title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                                    VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        card.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        card.MouseLeftButtonUp += (_, _) => SelectStep(step, manual: true);
        return card;
    }

    private static TextBlock MakeArrowText() => new()
    {
        Text = "→",
        FontSize = 12,
        Margin = new Thickness(3, 0, 3, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------- 分：详情展开 ----------------

    private void SelectStep(StepDef step, bool manual)
    {
        _selectedStep = step;
        if (manual) _manualSelectAt = DateTime.Now;

        // 卡片样式：选中加亮；当前运行步骤绿脉冲
        foreach (var (s, card) in _cards)
        {
            bool selected = s == step;
            card.BorderBrush = (Brush)FindResource(selected ? "AccentGlow" : "CardBorder");
            card.BorderThickness = new Thickness(selected ? 2.2 : 1.5);
            card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentLight" : "CardBg");
        }

        DetailHost.Children.Clear();
        var sb = new StringBuilder();
        sb.AppendLine($"〔{step.Title}〕{step.Goal}");
        AddSection(sb, "画面", step.Screens);
        AddSection(sb, "动作", step.Actions);
        AddSection(sb, "验证", step.Verify);
        AddSection(sb, "失败处理", step.Fallback);

        var head = new TextBlock
        {
            Text = sb.ToString(),
            FontSize = 12.5,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        DetailHost.Children.Add(head);
    }

    private static void AddSection(StringBuilder sb, string title, string[] items)
    {
        if (items is not { Length: > 0 }) return;
        sb.Append(title).Append("：");
        foreach (var it in items) sb.AppendLine().Append("　· ").Append(it);
        sb.AppendLine();
    }

    // ---------------- 驱动（总） ----------------

    /// <summary>切换模块（挂机启动时自动跟随）。重建步骤链。</summary>
    public void SetMode(string mode)
    {
        if (!ModeSteps.ContainsKey(mode)) return;
        _mode = mode;
        _suppressModeEvent = true;
        TabModeLeague.IsChecked = mode == "myleague";
        TabModeWeekly.IsChecked = mode == "intlweekly";
        TabModeCn.IsChecked = mode == "cndaily";
        _suppressModeEvent = false;
        BuildSteps();
    }

    private void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 加载期 IsChecked=True 会提前触发本事件，此时字段尚未解析赋值，必须守卫
        if (_suppressModeEvent || StepsHost is null) return;
        if (TabModeLeague.IsChecked == true) _mode = "myleague";
        else if (TabModeWeekly.IsChecked == true) _mode = "intlweekly";
        else if (TabModeCn.IsChecked == true) _mode = "cndaily";
        BuildSteps();
    }

    /// <summary>画面事件驱动：更新当前画面、按映射高亮当前步骤（8 秒内不覆盖手动选择）。</summary>
    public void OnScreen(GameScreen screen, double score, string? evidence)
    {
        string name = BotEngine.ScreenName(screen);
        TxtCurrent.Text = $"当前画面：{name}（{score:F1}）";
        DotCurrent.Fill = (Brush)FindResource("AccentGlow");

        var step = _screenMap.TryGetValue(screen, out var st) ? st : null;
        if (!ReferenceEquals(_currentStep, step))
        {
            if (_currentStep is not null) PulseStop(_cards.First(c => ReferenceEquals(c.Step, _currentStep)).Card);
            _currentStep = step;
            if (step is not null)
            {
                var card = _cards.First(c => ReferenceEquals(c.Step, step)).Card;
                card.BorderBrush = (Brush)FindResource("AccentGlow");
                PulseStart(card);
                // 手动点击 8 秒内不自动跳详情，避免用户正看别处被打断
                if ((DateTime.Now - _manualSelectAt).TotalSeconds > 8 || _selectedStep is null)
                    SelectStep(step, manual: false);
            }
        }
    }

    /// <summary>开始新场景时重置（清高亮，回到默认展开第一步）。</summary>
    public void ResetGraph()
    {
        if (_currentStep is not null)
            PulseStop(_cards.First(c => ReferenceEquals(c.Step, _currentStep)).Card);
        _currentStep = null;
        foreach (var (_, card) in _cards)
        {
            card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            card.BorderThickness = new Thickness(1.5);
        }
        TxtCurrent.Text = "当前画面：—";
        TxtDetail.Text = "判定依据：—";
        DotCurrent.Fill = (Brush)FindResource("EnvDotIdle");
    }

    private static void PulseStart(Border b)
    {
        var anim = new DoubleAnimation(1.0, 0.7, new Duration(TimeSpan.FromSeconds(0.8)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        b.BeginAnimation(OpacityProperty, anim);
    }

    private static void PulseStop(Border b)
    {
        b.BeginAnimation(OpacityProperty, null);
        b.Opacity = 1.0;
    }

    // ---------------- 可视化事件处理（Worker→GUI 管道事件消费，保持接口不变） ----------------

    /// <summary>探针 Top-5 候选事件：更新底部详情栏展示候选页面与分数。</summary>
    public void OnProbe(string topCandidatesJson)
    {
        try
        {
            var cands = System.Text.Json.JsonSerializer.Deserialize<List<ProbeCandidateDto>>(topCandidatesJson);
            if (cands is null || cands.Count == 0) return;
            var lines = cands.Select(c => $"{c.screen}({c.score:F1})");
            TxtDetail.Text = $"探针候选：{string.Join(" ｜ ", lines)}";
        }
        catch
        {
            /* JSON 解析失败不影响图谱展示 */
        }
    }

    /// <summary>导航寻路事件。</summary>
    public void OnNav(string from, string to)
    {
        TxtDetail.Text = $"导航：{from} → {to}";
    }

    /// <summary>比赛阶段切换事件。</summary>
    public void OnFlow(string stage, string? deadline)
    {
        var dl = string.IsNullOrEmpty(deadline) ? "" : $"（死线 {DateTime.Parse(deadline):HH:mm:ss}）";
        TxtDetail.Text = $"比赛阶段：{stage}{dl}";
    }

    /// <summary>探针候选 DTO（仅用于 JSON 反序列化）。</summary>
    private sealed class ProbeCandidateDto
    {
        public string screen { get; set; } = "";
        public double score { get; set; }
        public string evidence { get; set; } = "";
    }
}
