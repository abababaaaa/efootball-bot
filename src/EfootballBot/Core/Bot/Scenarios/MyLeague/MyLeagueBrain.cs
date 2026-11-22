using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using System.Text.RegularExpressions;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本层职责：单个挂机模块自己的菜单流程与私有状态机（只认自己的界面）。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用别的模块的 *Scenario（只读借用别人的 *Brain.Classify 认残留页面是允许的）
//   · 不许自己写场次记账 / 比赛内挂机节奏 —— 一律走 MatchPlay.Tick / IdleTapAsync
//   · 共享界面判定与决策一律用 Core/Bot/Match 的 MatchBrain，不要复制一份
// 新增模块：ModuleIds 加常量 + BotModuleCatalog 加一条记录，再写本目录的状态机。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 我的联赛模块私有逻辑（模块层，不再被别的模块引用）：
/// 道具购买（物品页行序/档位匹配/已拥有判定）、难度页探针与腿计划、
/// 赛后经验值页球员数、联赛主页磁贴焦点检测与导航图。
///
/// 共享的界面分类与决策已搬到 Core/Bot/Match/MatchBrain.*：
/// 别的模块（国服天梯/周活动/AI天梯/种马）不该再 using 本命名空间拿公共件。
/// </summary>
public static class MyLeagueBrain
{
    // ---------------- 我的联赛道具购买 ----------------
    /// <summary>道具类别（与游戏内「我的联赛物品」页一一对应，名称随用户设置 UI）。</summary>
    public enum MlItemKind { ExpBoost, CoachBoost, ConditionBoost }
    /// <summary>一个待购道具目标：类别 + 档位（x2/x3/x4；普通/好/极佳；教练强化无档位置空）。</summary>
    public sealed record MlItemTarget(MlItemKind Kind, string Level = "");
    /// <summary>道具行匹配结果：行文本 + 该行在物品列表区的序号（0 起，自上而下）+ 是否使用中（已购买）。</summary>
    public sealed record MlItemRow(int Index, string Text, double CenterY, bool InUse);
    /// <summary>
    /// 物品页固定行序（用户提供，2026-10-10）：
    /// 第1~3行=状态加成（普通+/好+/杰出），第4~6行=经验值加成（2x/3x/4x），第13行=主教练强化。
    /// 导航以固定行号为准（顶部锚定后下移），OCR 仅做「使用中」预检。
    /// </summary>
    public static int ExpectedItemRow(MlItemTarget target) => target.Kind switch
    {
        MlItemKind.ConditionBoost => target.Level switch { "好+" => 2, "杰出" => 3, _ => 1 },
        MlItemKind.ExpBoost => target.Level switch { "3x" => 5, "4x" => 6, _ => 4 },
        MlItemKind.CoachBoost => 13,
        _ => 1,
    };
    /// <summary>目标按物品页行序升序排序（如 杰出3 → 4x6 → 教练13 依次购买，增量下移不回顶）。</summary>
    public static List<MlItemTarget> SortByItemRow(IEnumerable<MlItemTarget> targets)
        => targets.OrderBy(ExpectedItemRow).ToList();
    // ---------------- 难度选择页 ----------------
    /// <summary>难度名 → 列表行号（1~7，自上而下）。未知名返回 1。</summary>
    public static int DifficultyRowNumber(string name)
    {
        int i = Array.IndexOf(MatchBrain.Difficulties, name);
        return i >= 0 ? i + 1 : 1;
    }

    /// <summary>
    /// 该难度页是否按游戏默认直接确认（不改选难度）：关键比赛的难度由游戏指定——
    /// 预告上下文（<paramref name="nextMatchIsKey"/>：预告弹窗/主页预告）或页面受限横幅
    /// （OCR 含「关键比赛」）任一命中即尊重默认。用户 2026-10-12 实机反馈
    /// （bot-20261012-015835 02:00）：受限页曾被当普通页导航改选（按了↑），要求关键
    /// 比赛一律不按↑。
    /// </summary>
    public static bool RespectGameDifficulty(bool nextMatchIsKey, OcrResult ocr) =>
        nextMatchIsKey || ocr.ContainsJoined("关键比赛");
    /// <summary>
    /// 检出当前难度页实际可用的选项（按 Y 自上而下）。普通页 7 项全有；
    /// 关键比赛受限页只出现高档位若干项（实机 P1：仅 超级球星+传奇 2 项）。
    /// 导航公式按实际项数 N 自适应：上×(N+1-k)。
    /// </summary>
    public static List<string> DetectDifficultyOptions(OcrResult ocr) =>
        MatchBrain.Difficulties
            .Select(d => (Name: d, Word: ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains(d))))
            .Where(t => t.Word is not null)
            .OrderBy(t => t.Word!.CenterY)
            .Select(t => t.Name)
            .ToList();
    /// <summary>
    /// 检测游戏内当前已选难度（蓝勾）：各难度词左右两侧窗口（±0.06、y±0.03）的蓝色像素数
    /// （B&gt;120 且高于 R/G 40+），合计最大且 ≥20 的行为选中（实机三帧：传奇24 / 新手151 / 新手179，
    /// 未选中行全部为 0）。返回 null = 未检出。
    /// </summary>
    public static string? DetectSelectedDifficulty(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        string? best = null;
        int bestBlue = 0;
        foreach (var diff in MatchBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int blue = 0;
            for (int y = (int)((word.CenterY - 0.03) * h); y < (int)((word.CenterY + 0.03) * h); y += 2)
            {
                for (int side = 0; side < 2; side++)
                {
                    double xa = side == 0 ? word.CenterX - word.W / 2 - 0.06 : word.CenterX + word.W / 2;
                    double xb = side == 0 ? word.CenterX - word.W / 2 : word.CenterX + word.W / 2 + 0.06;
                    for (int x = Math.Max(0, (int)(xa * w)); x < Math.Min(w, (int)(xb * w)); x += 2)
                    {
                        if (y < 0 || y >= h) continue;
                        int i = (y * w + x) * 4;
                        if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40) blue++;
                    }
                }
            }
            if (blue > bestBlue) { bestBlue = blue; best = diff; }
        }
        return bestBlue >= 20 ? best : null;
    }

    /// <summary>
    /// 检测难度页当前勾选（✓）难度——国际服难度页专用。<see cref="DetectSelectedDifficulty"/>
    /// 的宽窗（词左右 ±0.06）会把光标行的蓝框描边一起扫进来：蓝框横边线落在词 y±0.03 带内，
    /// 是否入带随捕获宽度漂移（实机 2026-10-20 两帧：1280 宽帧光标行 0 票判定正确，
    /// 1227 宽帧光标行 68 票压过勾行 22 票——同一会话判定在勾行/光标行间抖动的根源）。
    /// 校准窗（两张实机帧 difficulty_new_ui / difficulty_live_20261020 锁定回归）：
    /// x=词左缘 [-0.034,-0.004]（只含勾图标；卡左边线在 -0.05 开外）、y=词中心 ±0.016
    /// （勾与词同高；框横边线在 ±0.029）。实测勾行 22~24 票、其余行全 0，阈值取 12。
    /// </summary>
    public static string? DetectCheckedDifficulty(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        string? best = null;
        int bestBlue = 0;
        foreach (var diff in MatchBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int blue = 0;
            double x0 = word.CenterX - word.W / 2 - 0.034, x1 = word.CenterX - word.W / 2 - 0.004;
            for (int y = (int)((word.CenterY - 0.016) * h); y < (int)((word.CenterY + 0.016) * h); y += 2)
            {
                for (int x = Math.Max(0, (int)(x0 * w)); x < Math.Min(w, (int)(x1 * w)); x += 2)
                {
                    if (y < 0 || y >= h) continue;
                    int i = (y * w + x) * 4;
                    if (bgra[i] > 120 && bgra[i] > bgra[i + 1] + 40 && bgra[i] > bgra[i + 2] + 40) blue++;
                }
            }
            if (blue > bestBlue) { bestBlue = blue; best = diff; }
        }
        return bestBlue >= 12 ? best : null;
    }

    /// <summary>
    /// 难度页确认腿计划（round 自 1 起）：
    /// ★第 1 轮 = 只按 A（2026-10-21 实机视频：对话框打开时光标可能直接落在「确认」上——
    /// 光标位置是游戏记忆的，并非恒在列表第 1 行；确认已聚焦时 A 直接开赛，列表行上 A 是空操作无副作用）。
    /// 第 2 轮起按原配方腿（光标在列表第 1 行的情形）：重试腿先 上×9 饱和回顶行，下×10 饱和落底行
    /// → 右×N（取消→确认）→ A；偶数轮 右×2 防吞键。第 4/7 轮返回 null = B 逃生（关对话框回联赛
    /// 主页重进），第 9 轮后停止挂机。
    /// </summary>
    public static (bool UpFirst, int Downs, int Rights)? DifficultyLegPlan(int round)
    {
        if (round is 4 or 7) return null; // B 逃生轮
        if (round > 9) return null;       // 调用方在此停止挂机（弹窗遮挡类，按腿无解）
        if (round == 1) return (UpFirst: false, Downs: 0, Rights: 0); // 纯 A：吃定「确认已聚焦」的常见情形
        return (UpFirst: true, Downs: 10, Rights: round % 2 == 0 ? 2 : 1);
    }

    /// <summary>
    /// 在物品列表 OCR 行中找目标道具行。物品区为页面主体（y 0.22~0.90），
    /// 按类别关键词 + 档位词匹配（x2/x3/x4 匹配数字位；状态档位匹配中文词）。
    /// 返回 null 表示本页没有该道具。同一行文本不可变（固定 UI 词），比分/球队名不参与。
    /// </summary>
    public static MlItemRow? FindItemRow(OcrResult ocr, MlItemTarget target)
    {
        // 物品区候选行（排除标题/底栏），按 Y 自上而下编号
        var rows = ocr.Lines
            .Where(l => l.CenterY is > 0.22 and < 0.90)
            .OrderBy(l => l.CenterY)
            .ToList();
        bool IsUse(OcrLine l) => l.Text.Contains("使用中");
        int wantExp = int.Parse(ExpDigit(target.Level)!);
        // 命中优先级（实机 itempage_inuse：已购行显示「使用中」且变灰）：
        // ① 已拥有：同类「使用中」行且档位足够（4x 在用满足 2x 需求）→ 返回 InUse=true（跳过购买）
        // ② 精确档位的可购行（非使用中）
        // ③ 类别兜底（档位词没读到，非使用中）
        OcrLine? hit = target.Kind switch
        {
            MlItemKind.ExpBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("经验值") && IsUse(l) &&
                                          LevelDigit(l.Text) is { } d && int.Parse(d) >= wantExp)
                ?? rows.FirstOrDefault(l => l.Text.Contains("经验值") && !IsUse(l) &&
                                             LevelDigit(l.Text) == ExpDigit(target.Level))
                ?? rows.FirstOrDefault(l => l.Text.Contains("经验值") && !IsUse(l)),
            MlItemKind.CoachBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("教练") && IsUse(l))
                ?? rows.FirstOrDefault(l => l.Text.Contains("教练") && !IsUse(l)),
            MlItemKind.ConditionBoost =>
                rows.FirstOrDefault(l => l.Text.Contains("状态") && IsUse(l) &&
                                          CondRank(l.Text) >= CondRank(target.Level))
                ?? rows.FirstOrDefault(l => l.Text.Contains("状态") && !IsUse(l) && l.Text.Contains(CondWord(target.Level)))
                ?? rows.FirstOrDefault(l => l.Text.Contains("状态") && !IsUse(l)),
            _ => null,
        };
        if (hit is null) return null;
        int index = rows.IndexOf(hit);
        if (index < 0) return null;
        // 「使用中」也可能以独立标记行出现在同行带（Y 相近）
        bool inUse = IsUse(hit) ||
                     rows.Any(r => !ReferenceEquals(r, hit) &&
                                   Math.Abs(r.CenterY - hit.CenterY) < 0.05 && IsUse(r));
        return new MlItemRow(index, hit.Text, hit.CenterY, inUse);
    }

    /// <summary>
    /// 检测物品列表当前焦点行（白色高亮条）的文字。逐行 OCR 扫描定位用：
    /// 焦点行背景是近白（实机 ≈230+）、未聚焦是深灰（≈40~60），采样每条 OCR 行中心附近的
    /// 背景亮度取最亮者，且须明显高于次高，否则认为读不准返回 null。
    /// 返回该行文字（如「经验值:4x 有效场数:5场 使用中」）；读不准返回 null。
    /// </summary>
    public static string? DetectFocusedItemText(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        double RegionAvg(double x0, double x1, double y0, double y1)
        {
            long sum = 0; int n = 0;
            for (int y = (int)(h * y0); y < (int)(h * y1); y += 2)
                for (int x = (int)(w * x0); x < (int)(w * x1); x += 2)
                {
                    int cx = x < 0 ? 0 : (x >= w ? w - 1 : x);
                    int cy = y < 0 ? 0 : (y >= h ? h - 1 : y);
                    int i = (cy * w + cx) * 4;
                    sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? 0 : (double)sum / n;
        }
        // 只取物品列表区的行（排除标题/顶部积分栏/底部按键提示）
        var lines = ocr.Lines.Where(l => l.CenterY is > 0.20 and < 0.88).ToList();
        if (lines.Count == 0) return null;
        // 整行横向扫描取亮度中位数：焦点行整条白底、未聚焦深灰。用中位数而非平均值，
        // 抗行内白色文字笔画/绿色图标的局部亮点（旧版只采右侧窄带 x0.86~0.94 落在卡片外黑区
        // 或边缘，实机读不准 → 14 格扫描全 miss）。
        double RowMedian(double cy, double hh)
        {
            var vals = new List<double>();
            for (double yy = cy - hh * 0.5; yy <= cy + hh * 0.5; yy += 0.004)
                for (double xx = 0.10; xx <= 0.90; xx += 0.006)
                    vals.Add(RegionAvg(xx, xx + 0.005, yy, yy + 0.004));
            if (vals.Count == 0) return 0;
            vals.Sort();
            return vals[vals.Count / 2];
        }
        var scored = new List<(OcrLine Line, double Avg)>();
        foreach (var l in lines)
            scored.Add((l, RowMedian(l.CenterY, l.H)));
        var top = scored.OrderByDescending(s => s.Avg).ToList();
        // 焦点行须明显亮于次高（白底 ≈230 vs 深灰 ≈50，间隙极大）；放宽绝对线到 110
        // 并保留差值 ≥45，兼容捕获整体偏暗的机器。
        if (top.Count < 2 || top[0].Avg - top[1].Avg < 45 || top[0].Avg < 110) return null;
        return top[0].Line.Text;
    }

    /// <summary>行文本里的加成数字位（游戏格式"4x"，OCR 偶见"x4"；无则 null）。</summary>
    private static string? LevelDigit(string lineText)
    {
        var t = lineText.Replace("×", "x").Replace("X", "x");
        var m = Regex.Match(t, @"(\d)\s*x|x\s*(\d)");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    private static string? ExpDigit(string level) =>
        level switch { "3x" => "3", "4x" => "4", _ => "2" };
    /// <summary>状态档位词（OCR 常把「普通+」的加号丢掉，匹配时去掉加号）。</summary>
    private static string CondWord(string level) => level.TrimEnd('+');
    /// <summary>状态档位序：普通 0 &lt; 好 1 &lt; 杰出 2（高档位满足低档位需求）。</summary>
    internal static int CondRank(string word) => word switch
    {
        var w when w.Contains("杰出") => 2,
        var w when w.Contains("好") => 1,
        _ => 0,
    };
    /// <summary>
    /// 联赛主页右侧「使用的物品」面板（右摇杆拨出）：判定目标道具是否已被拥有。
    /// 经验值看档位数字（高档满足低档）；状态看档位序；教练看关键词。
    /// 实机（flow_3）：面板行如 "Exp经验值4x"、"我的联赛积分物品剩余比赛场次"。
    /// </summary>
    public static bool IsOwned(OcrResult ocr, MlItemTarget target) => target.Kind switch
    {
        MlItemKind.ExpBoost => ocr.Lines.Any(l =>
            l.Text.Contains("经验值") && LevelDigit(l.Text) is { } d && int.Parse(d) >= int.Parse(ExpDigit(target.Level)!)),
        MlItemKind.CoachBoost => ocr.Lines.Any(l => l.Text.Contains("教练")),
        MlItemKind.ConditionBoost => ocr.Lines.Any(l =>
            l.Text.Contains("状态") && CondRank(l.Text) >= CondRank(target.Level)),
        _ => false,
    };
    /// <summary>联赛主页右侧面板当前显示的是「使用的物品」（右摇杆拨出的状态）。</summary>
    public static bool ShowsItemsPanel(OcrResult ocr) => ocr.ContainsJoined("使用的物品");
    /// <summary>联赛主页右侧面板当前显示的是「挑战」（默认状态，需右拨切换）。</summary>
    public static bool ShowsChallengePanel(OcrResult ocr) => ocr.ContainsJoined("挑战");

    // ---------------- 赛后经验值页（球员数判定） ----------------
    /// <summary>
    /// 赛后「经验值」页（实机 2026-10-17 截图 myleague_exp_result_page.png）：比赛推进链中的
    /// 一页——页首大字标题「经验值」+ 球员格（每格「等级 N/N」+「加成:+N%」两行）+
    /// 底部「Y 查看经验值」提示 + 右下「下一步」。判据只用固定 UI 词，球员名不参与：
    /// 主判「查看经验值」（全流程唯一）；底部提示漏读时兜底=页首标题「经验值」（y&lt;0.2）
    /// 且页内确有球员行——物品页行/主页面板行同样含「经验值」字样但都在 y&gt;0.2，单凭标题会误判。
    /// </summary>
    public static bool IsExpResultPage(OcrResult ocr)
    {
        if (ocr.ContainsJoined("查看经验值")) return true;
        return ocr.Lines.Any(l => l.CenterY < 0.2 && l.Text.Contains("经验值")) &&
               CountExpResultPlayers(ocr) > 0;
    }

    /// <summary>同一球员「等级 N/N」与「加成:+N%」两行的 Y 差上限（实机 ≈0.03；不同球员行距 ≥0.12）。</summary>
    private const double ExpPlayerLineGap = 0.08;
    /// <summary>左右两列球员格的 X 分界（两列中心距 ≈0.43，列内词 X 抖动 ≲0.05）。</summary>
    private const double ExpPlayerColumnGap = 0.2;
    /// <summary>
    /// 数经验值页显示的球员数：2 列网格每格一名球员，格内「等级 N/N」+「加成:+N%」两行，
    /// OCR 漏读其一仍按现存词计。先按 X 聚成左右列，列内按 Y 聚球员（相邻两行 Y 差小归同人，
    /// 上下球员行距大分人）。「等级/加成」是每格固定 UI 词；球员名/头像数每场都变，禁作判定点。
    /// </summary>
    public static int CountExpResultPlayers(OcrResult ocr)
    {
        var words = ocr.Words
            .Where(w =>
            {
                var t = w.NormText;
                return t.Contains("等级") || t.Contains("加成");
            })
            .OrderBy(w => w.CenterX)
            .ToList();
        if (words.Count == 0) return 0;
        int count = 0;
        int i = 0;
        while (i < words.Count)
        {
            // 按 X 聚列（锚=列内最左词）
            double colLeft = words[i].CenterX;
            var column = new List<OcrWord>();
            for (; i < words.Count && words[i].CenterX - colLeft < ExpPlayerColumnGap; i++)
                column.Add(words[i]);
            // 列内按 Y 聚球员：相邻词 Y 差 ≤0.08 归同一名（等级/加成两行差 ≈0.03）
            column.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
            double? lastY = null;
            foreach (var w in column)
            {
                if (lastY is null || w.CenterY - lastY.Value > ExpPlayerLineGap)
                    count++;
                lastY = w.CenterY;
            }
        }
        return count;
    }

    // ---------------- 联赛主页光标检测与智能导航 ----------------
    /// <summary>主页可聚焦磁贴（固定 UI 词；「i」为前往比赛旁的信息图标，无文字不可检）。</summary>
    public static readonly (string Keyword, string Name)[] HomeTiles =
    {
        ("前往比赛", "前往比赛"), ("赛程表", "赛程表"), ("兑换积分", "兑换积分"),
        ("赛季纪", "赛季纪录"), ("赛事", "赛事信息"), ("建议", "建议"),
    };
    /// <summary>
    /// 检测联赛主页当前聚焦的磁贴：采样各磁贴文字框的平均亮度（聚焦变白）。
    /// 阈值取「全部磁贴亮度的中位数 + 22」的相对判定（抗捕获整体明暗漂移；实机数据：
    /// 聚焦 183~200 / 赛程表弱高光 133 / 未聚焦 90~105，中位数≈95 → 阈值≈117 全覆盖）。
    /// 「i」图标（前往比赛词右侧锚定区域）聚焦时亮 165~211（未聚焦 19~114）。
    /// 返回 null = 未检出。
    /// </summary>
    public static string? DetectFocusedHomeTile(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;
        double RegionAvg(double x0, double x1, double y0, double y1)
        {
            long sum = 0; int n = 0;
            for (int y = (int)(h * y0); y < (int)(h * y1); y += 2)
                for (int x = (int)(w * x0); x < (int)(w * x1); x += 2)
                {
                    int i = (y * w + x) * 4;
                    sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? 0 : (double)sum / n;
        }
        var scores = new List<(string Name, double Avg)>();
        OcrWord? goWord = null;
        foreach (var (kw, name) in HomeTiles)
        {
            var word = ocr.Words.FirstOrDefault(x => x.Text.Replace(" ", "").Contains(kw));
            if (word is null) continue;
            if (name == "前往比赛") goWord = word;
            double avg = RegionAvg(word.CenterX - word.W * 0.6, word.CenterX + word.W * 0.6,
                                   word.CenterY - word.H * 0.6, word.CenterY + word.H * 0.6);
            scores.Add((name, avg));
        }
        if (scores.Count == 0) return null;
        // 相对阈值：中位数 + 22（下限 112），最高者过线即聚焦（中位数按全部磁贴算）
        var sorted = scores.Select(s => s.Avg).OrderBy(v => v).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double threshold = Math.Max(112, median + 22);
        // 赛程表不参与聚焦竞争：其亮度不含聚焦信息——用户 2026-10-11 逐磁贴标定帧实测，
        // 光标在任意磁贴时赛程表均恒亮 ≈182（未读赛程内容自带高亮 ≈ 聚焦亮度；
        // 开发机弱高光 133 同样不可区分聚焦）。聚焦判定只在其余磁贴中取最亮；
        // 光标真在赛程表时按"未检出"返回，由导航约定（未检出=按赛程表）承接。
        var candidates = scores.Where(s => s.Name != "赛程表").ToList();
        if (candidates.Count == 0)
        {
            DumpFocusDiag(scores, threshold, 0, null);
            return null;
        }
        var best = candidates.OrderByDescending(s => s.Avg).First();
        // i 图标亮度：前往比赛词右侧锚定（右缘 +0.07~+0.16、y -0.035~+0.045 —— 图标实际范围，
        // 实机网格扫描亮区 x0.25~0.31/y0.215~0.285；用户标定帧：i 聚焦时该区亮 208，
        // 前往比赛聚焦时磁贴自身亮块在 +0.03~0.08 不污染本锚定区。
        // y 窗向下偏：图标带比文字行高约一倍且向行下方延伸，居中窗会把图标上方暗行采进来
        // 拉低读数（Simd 引擎词框略矮时 130 差 1 分脱靶，2026-10-21 实测），故 -0.035/+0.045）
        double iAvg = 0;
        if (goWord is not null)
        {
            double x0 = goWord.CenterX + goWord.W / 2 + 0.07;
            iAvg = RegionAvg(x0, x0 + 0.09, goWord.CenterY - 0.035, goWord.CenterY + 0.045);
        }
        string? verdict;
        if (best.Avg >= threshold)
        {
            verdict = best.Name;
        }
        else if (goWord is not null && (iAvg >= 140 ||
                 (iAvg >= 125 && best.Avg <= 125 && iAvg > best.Avg)))
        {
            // 「i」图标：文字磁贴全暗才算（判据本意）。旧相对线 iAvg≥best+25 对词框敏感——
            // 引擎换 Simd 后多读出 赛事信息(106) 把 best 抬高，130 差 1 分脱靶（2026-10-21 实测）；
            // 改绝对暗线 best≤125（未聚焦磁贴实测 78~108，聚焦 183+ 有大间隙）+ iAvg>best。
            // iAvg≥140 绝对亮线保留（高亮捕获下磁贴整体偏亮时兜底）。
            verdict = "i";
        }
        else
        {
            verdict = null;
        }
        DumpFocusDiag(scores, threshold, iAvg, verdict);
        return verdict;
    }

    /// <summary>焦点检测诊断输出（场景接走打 Debug 日志；2s 节流）。
    /// 导航错判时日志会给出每个磁贴的实际亮度/i 图标亮度/阈值/最终判定，据此定位。</summary>
    public static Action<string>? FocusDiag { get; set; }
    private static long _lastFocusDiagTick;

    private static void DumpFocusDiag(IReadOnlyList<(string Name, double Avg)> scores,
        double threshold, double iAvg, string? verdict)
    {
        long now = Environment.TickCount64;
        if (now - _lastFocusDiagTick < 2000) return;
        _lastFocusDiagTick = now;
        var detail = string.Join(" ", scores.Select(s => $"{s.Name}={s.Avg:F0}"));
        FocusDiag?.Invoke($"焦点检测：{detail} i图标={iAvg:F0} 阈值={threshold:F0} → {(verdict ?? "未检出")}");
    }

    /// <summary>
    /// 主页导航图（用户确认边，2026-10-11 修订）：**前往比赛出发第一步必须向下进赛程表；
    /// 前往比赛 --右--> i**（i 在前往比赛右边，切记）——因此前往比赛 --下--> 赛程表、
    /// i --下--> 赛程表、i --左--> 前往比赛（右边的反向）；
    /// 赛程表 --右--> 大概率赛季纪录/小概率兑换积分（**不确定边，盲导航勿走**）；赛季纪录 --左--> 赛程表、--上--> 兑换积分；
    /// 兑换积分 --左--> 赛程表、--下--> 赛季纪录、**--上--> 回卷第一行（2026-10-11 深夜实测：
    /// 可能落 i，而 i --下--> 赛程表不是兑换积分——兑换积分上方禁按 Up）**；
    /// 赛事信息 --上--> 赛程表、--右--> 建议；建议 --左--> 赛事信息、--上--> 赛季纪录。
    /// 兑换积分唯一确定性到达路线：赛事信息 --右--> 建议 --上--> 赛季纪录 --上--> 兑换积分（全确定边）。
    /// 未确认方向一律不走（赛事信息下/左、建议右、前往比赛左上均无路）。
    /// 焦点未检出（null）：赛程表聚焦亮度不可检（恒亮），null 大概率=光标在赛程表——
    /// 按「未检出=赛程表」约定导航：去兑换积分按 Right，回前往比赛按 Up。
    /// 返回下一步方向；null = 已在目标。
    /// </summary>
    public static PadDir? PlanHomeNavStep(string? focus, string target)
    {
        if (focus == target) return null;
        return target switch
        {
            "兑换积分" => focus switch
            {
                null or "赛程表" => PadDir.Right,             // 未检出=赛程表：右（大概率赛季纪录，再验证）
                "前往比赛" or "i" => PadDir.Down,            // 前往比赛出发第一步必须向下进赛程表（右是 i）
                "赛季纪录" or "兑换积分" => PadDir.Up,        // 赛季纪录--上-->兑换积分
                "赛事信息" => PadDir.Up,                      // 上→赛程表（下/左无路）
                "建议" => PadDir.Up,                          // 上→赛季纪录
                _ => PadDir.Right,                            // 未知磁贴：按赛程表行处理
            },
            "前往比赛" => focus switch
            {
                null or "赛程表" => PadDir.Up,                // 未检出=赛程表：上→前往比赛
                "i" => PadDir.Left,                           // i--左-->前往比赛（前往比赛--右-->i 的反向）
                "兑换积分" or "赛季纪录" => PadDir.Left,      // 左→赛程表（上方向未确认）
                "赛事信息" => PadDir.Up,                      // 上→赛程表
                "建议" => PadDir.Up,                          // 上→赛季纪录
                _ => PadDir.Up,
            },
            _ => null,
        };
    }
}




