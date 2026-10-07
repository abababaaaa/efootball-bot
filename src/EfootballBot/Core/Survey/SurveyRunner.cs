using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using EfootballBot.Core.Probe;
using EfootballBot.Core.Vision;


namespace EfootballBot.Core.Survey;

/// <summary>
/// 画面巡测模式（EfootballBot --survey [分钟=8] [进程名]）：
/// 在真实游戏里自动按键探索，逐屏截图 + OCR + 探针判定，记录「每个按键从哪屏到哪屏、按 B 怎么回来」，
/// 并为每屏计算独占重点词（本屏有、其他所有屏都没有的词）。
/// 全部输出到独立目录 survey\&lt;时间戳&gt;\（screen_NN.png / report.md / trace.yml），
/// 不写 logs\、不改任何现有状态——与主程序数据完全隔离。
/// 安全围栏：主页画面一律不发 A（默认高光就是「前往比赛」）；出现开赛危险词（开始比赛/选择AI难度等）禁 A；
/// 检测到比赛画面（AI 图标/探针 InMatch）立即终止；全程不按 START。
/// 注意：运行前请关闭主程序 GUI，避免两份虚拟手柄同时注入。
/// </summary>
public static class SurveyRunner
{
    private sealed class ScreenNode
    {
        public int Id;
        public string Fingerprint = "";
        public string JoinedText = "";
        public List<string> Words = new();
        public string ProbeScreen = "";
        public List<string> Evidence = new();
        public string Png = "";
        public HashSet<string> TriedKeys = new();
    }

    private sealed record Edge(int From, string Key, int To);

    private static readonly (string Key, Action<Pad> Act)[] DpadKeys =
    {
        ("Down", p => p.Dpad(PadDir.Down)),
        ("Right", p => p.Dpad(PadDir.Right)),
        ("Up", p => p.Dpad(PadDir.Up)),
        ("Left", p => p.Dpad(PadDir.Left)),
    };

    private static readonly (string Key, Action<Pad> Act)[] ButtonKeys =
    {
        ("RB", p => p.RB()),
        ("LB", p => p.LB()),
        ("X", p => p.X()),
        ("Y", p => p.Y()),
        ("A", p => p.Confirm()),
        ("B", p => p.Back()),
    };

    public static async Task RunAsync(string[] args)
    {
        int maxMinutes = args.Length > 1 && int.TryParse(args[1], out int m) && m > 0 ? m : 8;
        string procName = args.Length > 2 ? args[2] : CnConfig.ProcessName;
        var deadline = DateTime.Now.AddMinutes(maxMinutes);

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "survey",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outDir);
        var trace = new StringBuilder();
        var edges = new List<Edge>();
        var nodes = new List<ScreenNode>();
        var visited = new HashSet<string>();
        string? abortReason = null;

        var win = System.GameWindow.FindFirst(procName)
            ?? throw new InvalidOperationException($"未找到进程「{procName}」的可见窗口，请确认游戏已启动。");
        var (w, h) = System.GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        Console.WriteLine($"目标窗口：{win.Title}（{win.ProcessName}）客户区 {w}×{h}");
        Console.WriteLine($"输出目录：{outDir}");
        Console.WriteLine($"安全围栏：主页禁 A；开赛危险词禁 A；比赛画面立即终止；不按 START。时长上限 {maxMinutes} 分钟。");

        using var capture = new WindowCapture();
        capture.Start(win.Handle, w, h);
        var ocrEng = new OcrRecognizer(null);
        var probeEngine = new ProbeEngine(PageCatalog.Pages);

        using var vg = new VirtualGamepad360();
        vg.Connect();
        Console.WriteLine($"虚拟手柄已连接（XInput 玩家 {vg.UserIndex + 1}）");
        var pad = new Pad(vg, new TimingConfig());

        async Task<Bot.Observation> ObserveAsync(int settleMs)
        {
            await Task.Delay(settleMs);
            FrameData? f = null;
            for (int i = 0; i < 10 && f is null; i++)
            {
                await Task.Delay(300);
                f = capture.TryGetLatest();
            }
            if (f is null) throw new InvalidOperationException("捕获不到帧（游戏窗口被最小化/关闭？）");
            var ocr = await ocrEng.RecognizeAsync(f);
            var probe = probeEngine.Classify(new Bot.Observation(f, ocr, GameScreen.Unknown, 0, Array.Empty<string>()));
            return new Bot.Observation(f, ocr, probe.Screen, probe.RawScore, probe.Evidence.ToArray());
        }

        (ScreenNode, bool isNew) Record(Bot.Observation obs)
        {
            var fp = Fingerprint(obs);
            var node = nodes.FirstOrDefault(n => n.Fingerprint == fp);
            bool isNew = node is null;
            if (isNew)
            {
                node = new ScreenNode
                {
                    Id = nodes.Count,
                    Fingerprint = fp,
                    JoinedText = obs.Ocr.JoinedText,
                    Words = obs.Ocr.MergedWords.Select(x => OcrText.Norm(x.Text))
                        .Where(t => t.Length > 0).Distinct().ToList(),
                    ProbeScreen = obs.Screen.ToString(),
                    Evidence = obs.Evidence.Take(4).ToList(),
                };
                node.Png = $"screen_{node.Id:00}.png";
                SavePng(obs.Frame, Path.Combine(outDir, node.Png));
                nodes.Add(node);
                visited.Add(fp);
                var line = $"[屏{node.Id:00}] 探针={node.ProbeScreen} 证据={string.Join(",", node.Evidence)} 词数={node.Words.Count}";
                Console.WriteLine(line);
                trace.AppendLine(line);
                trace.AppendLine($"  全文：{node.JoinedText}");
            }
            return (node!, isNew);
        }

        try
        {
            // 起点屏
            var startObs = await ObserveAsync(1200);
            var (start, _) = Record(startObs);
            var stack = new List<ScreenNode> { start };
            var current = start;

            while (stack.Count > 0 && DateTime.Now < deadline && abortReason is null && nodes.Count < 25)
            {
                current = stack[^1];
                string? usedKey = null;
                ScreenNode? next = null;

                // 候选键序：方向 → 肩键 → X/Y →（视围栏）A → B 退出
                foreach (var (key, act) in DpadKeys.Concat(ButtonKeys))
                {
                    if (DateTime.Now >= deadline) break;
                    if (!current.TriedKeys.Add(key)) continue;

                    // 安全围栏：按 A 前检查本屏是否危险
                    if (key == "A" && IsForbiddenForA(current, out string why))
                    {
                        trace.AppendLine($"[屏{current.Id:00}] 跳过 A：{why}");
                        continue;
                    }

                    Console.Write($"[屏{current.Id:00}] 按 {key} … ");
                    act(pad);
                    usedKey = key;

                    var obs2 = await ObserveAsync(1800);
                    var (n2, isNew) = Record(obs2);

                    if (isNew && n2.Id != current.Id)
                    {
                        Console.WriteLine($"→ 新屏{n2.Id:00}（{n2.ProbeScreen}）");
                        edges.Add(new Edge(current.Id, key, n2.Id));
                        // 检测到比赛：立即终止（B 退出比赛无效，防止任何进一步操作）
                        if (n2.ProbeScreen == "InMatch" || FrameAnalyzer.FindAiControlIcon(obs2.Frame, out _, out _))
                        {
                            abortReason = $"进入比赛画面（屏{n2.Id:00}），巡测立即终止";
                        }
                        else
                        {
                            stack.Add(n2);
                            next = n2;
                        }
                        break;
                    }
                    if (n2.Id != current.Id)
                    {
                        Console.WriteLine($"→ 已访问屏{n2.Id:00}");
                        edges.Add(new Edge(current.Id, key, n2.Id));
                        next = n2;
                        break;
                    }
                    Console.WriteLine("→ 无变化");
                    edges.Add(new Edge(current.Id, key, -1));
                }

                if (abortReason is not null || next is not null) continue;

                // 本屏候选试尽：按 B 回退
                Console.WriteLine($"[屏{current.Id:00}] 候选试尽，按 B 回退");
                pad.Back();
                var back = await ObserveAsync(1800);
                var (nb, isNewB) = Record(back);
                if (isNewB)
                {
                    // B 弹出了新画面（退出确认框等）：记录、禁 A 探索其 B 路径
                    edges.Add(new Edge(current.Id, "B", nb.Id));
                    if (nb.ProbeScreen == "InMatch" || FrameAnalyzer.FindAiControlIcon(back.Frame, out _, out _))
                        abortReason = "按 B 进入比赛画面，巡测终止";
                    else
                        stack.Add(nb);
                }
                else if (nb.Id != current.Id)
                {
                    edges.Add(new Edge(current.Id, "B", nb.Id));
                    stack.Remove(current);
                }
                else
                {
                    edges.Add(new Edge(current.Id, "B", -1));
                    // B 无效：再试一次，仍无效则出栈
                    pad.Back();
                    var back2 = await ObserveAsync(1800);
                    var (nb2, _) = Record(back2);
                    if (nb2.Id != current.Id)
                    {
                        edges.Add(new Edge(current.Id, "B", nb2.Id));
                        stack.Remove(current);
                    }
                    else
                    {
                        trace.AppendLine($"[屏{current.Id:00}] B 两次无效果，出栈（可能需手动回退）");
                        stack.Remove(current);
                    }
                }
            }

            if (abortReason is null && nodes.Count >= 25) abortReason = "屏数上限（25）";
            if (abortReason is null && DateTime.Now >= deadline) abortReason = "时长上限";

            // 收尾：从当前画面连按 B 回起点（每步记录）
            trace.AppendLine("---- 收尾回退 ----");
            for (int i = 0; i < 12 && abortReason is null; i++)
            {
                var obs = await ObserveAsync(1500);
                var (n, _) = Record(obs);
                if (n.Id == start.Id) { trace.AppendLine("已回到起点屏"); break; }
                if (n.ProbeScreen == "InMatch" || FrameAnalyzer.FindAiControlIcon(obs.Frame, out _, out _))
                { trace.AppendLine("回退途中出现比赛画面，停止按键"); break; }
                trace.AppendLine($"回退：屏{n.Id:00} 按 B");
                pad.Back();
            }
        }
        finally
        {
            vg.ResetAll();
        }

        // 独占词计算：本屏词集 − 其他所有屏词集并集
        var report = new StringBuilder();
        report.AppendLine("# 画面巡测报告");
        report.AppendLine();
        report.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  截图目录：{Path.GetFileName(outDir)}");
        report.AppendLine($"- 终止原因：{abortReason ?? "探索完成（候选边穷尽）"}");
        report.AppendLine($"- 屏幕数：{nodes.Count}，按键边数：{edges.Count}");
        report.AppendLine();
        report.AppendLine("## 每屏重点词（独占词 = 本屏有、其他所有屏都没有）");
        foreach (var n in nodes)
        {
            var others = nodes.Where(x => x.Id != n.Id).SelectMany(x => x.Words).ToHashSet();
            var exclusive = n.Words.Where(w2 => !others.Contains(w2)).ToList();
            // 推荐排序：中文词优先（≥2字），过滤纯数字
            var rec = exclusive
                .OrderByDescending(w2 => w2.Any(char.IsAsciiLetter) && w2.All(c => c > 127))
                .ThenByDescending(w2 => w2.Length)
                .ToList();
            report.AppendLine();
            report.AppendLine($"### 屏{n.Id:00}（探针 {n.ProbeScreen}，证据 {string.Join("、", n.Evidence)}）截图 {n.Png}");
            report.AppendLine($"- 独占词：{(rec.Count > 0 ? string.Join("、", rec) : "（无——与其他屏完全重叠）")}");
            report.AppendLine($"- 全部词：{string.Join("、", n.Words)}");
            report.AppendLine($"- OCR 全文：{n.JoinedText}");
        }
        report.AppendLine();
        report.AppendLine("## 按键边表（从哪屏按什么键到哪屏；-1 = 无变化）");
        foreach (var e in edges.GroupBy(x => (x.From, x.Key, x.To)))
            report.AppendLine($"- 屏{e.Key.From:00} --{e.Key}--> {(e.Key.To < 0 ? "（无变化）" : $"屏{e.Key.To:00}")}");

        File.WriteAllText(Path.Combine(outDir, "report.md"), report.ToString());
        File.WriteAllText(Path.Combine(outDir, "trace.yml"),
            "# 巡测轨迹（按时间序）\n" + trace.ToString());
        Console.WriteLine();
        Console.WriteLine($"巡测完成：{nodes.Count} 屏 / {edges.Count} 条按键边，终止原因：{abortReason ?? "探索完成"}");
        Console.WriteLine($"报告：{Path.Combine(outDir, "report.md")}");
    }

    /// <summary>画面指纹：探针页 + 归一化中文词（排序前 10）。动态队名/数字会破坏重访识别，仅作辅助。</summary>
    private static string Fingerprint(Bot.Observation obs)
    {
        var cnWords = obs.Ocr.MergedWords
            .Select(x => OcrText.Norm(x.Text))
            .Where(t => t.Length > 0 && t.Any(c => c > 127))
            .Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(10);
        return obs.Screen + "|" + string.Join(",", cnWords);
    }

    /// <summary>按 A 危险围栏：主页（默认高光=前往比赛）、开赛危险词、难度选择一律禁 A。</summary>
    private static bool IsForbiddenForA(ScreenNode n, out string why)
    {
        var t = n.JoinedText;
        if (t.Contains("前往比赛") && (t.Contains("赛程表") || t.Contains("联赛排名") || t.Contains("比赛计划")))
        { why = "主页画面（默认高光=前往比赛）"; return true; }
        if (t.Contains("开始比赛") || t.Contains("比赛开始") || t.Contains("选择AI难度")
            || t.Contains("选择赛事等级") || t.Contains("关键比赛"))
        { why = "开赛/难度危险词"; return true; }
        why = "";
        return false;
    }

    private static void SavePng(FrameData f, string path)
    {
        var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        using var fs = File.OpenWrite(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        enc.Save(fs);
    }
}


