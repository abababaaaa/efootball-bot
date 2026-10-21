#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
反馈帧 → 回归用例生成器（离线工具，纯标准库）

用途
----
用户反馈包里最值钱的东西是**实机帧**。以前排障完就丢了，下次同类问题还得重新截图。
本工具把一张实机帧变成可直接跑的真实帧测试：

    python tools/frame_to_test.py data/failures/20261021/183012_走到不动.jpg \
        --name matchhub_new_ui_20261021 --copy

生成内容：
1) 把图片拷进 tests/EfootballBot.Tests/TestData/（`--copy`，重名自动加后缀）；
2) 打印一个 C# 测试类骨架：LoadFrame 助手 + 分类断言 + 决策断言 + OCR 词块打印，
   里面只有 3 处 `TODO` 要你按实际判定填（帧一跑，OCR 词块就从测试输出里出来了）。

默认只打印到屏幕；加 `--out` 直接写进测试工程目录。

用法
----
    python tools/frame_to_test.py <帧文件> --name <用例名> [--copy] [--out tests/EfootballBot.Tests]

参数
----
    --name     用例名（同时作为 TestData 文件名主干，建议带日期）
    --copy     把帧复制到 tests/EfootballBot.Tests/TestData/
    --out DIR  把生成的 .cs 写到 DIR（默认只打印）
    --brain    MyLeagueBrain / PeakLadderBrain / WeeklyBrain / CnWeeklyBrain（默认 MyLeagueBrain）
"""

import argparse
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTDATA = os.path.join(ROOT, "tests", "EfootballBot.Tests", "TestData")

TEMPLATE = """using EfootballBot.Core.Bot.Scenarios.{ns};
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 实机帧回归：{name}
// 来源：{src}
// 生成：tools/frame_to_test.py（把用户反馈的帧固化成长期回归资产）
public class {cls}
{{
    private readonly ITestOutputHelper _out;
    public {cls}(ITestOutputHelper output) => _out = output;

    private static FrameData LoadFrame()
    {{
        var data = TestFrameLoader.LoadPngBgra(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "{file}"), out int w, out int h);
        return new FrameData(data, w, h, 0);
    }}

    /// <summary>先把这一帧的 OCR 词块打出来（跑一次看输出，再填下面的断言）。</summary>
    [Fact]
    public async Task {name}_DumpOcr()
    {{
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = await ocr.RecognizeAsync(LoadFrame());
        _out.WriteLine("[OCR] " + string.Join(" | ", res.MergedWords.Select(w => w.Text)));
        Assert.True(res.Words.Count > 0, "该帧至少要能识别出词——0 词说明捕获/裁切出了问题");
    }}

    [Fact]
    public async Task {name}_Classify()
    {{
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = await ocr.RecognizeAsync(LoadFrame());
        var screen = {brain}.{classify_call};
        // TODO: 改成这一帧在游戏里实际是什么界面
        Assert.Equal({screen_default}, screen);
    }}
{decide_test}}}
"""

DECIDE_TEST = """
    [Fact]
    public async Task {name}_Decide()
    {{
        using var ocr = new OcrRecognizer(OcrEngineKind.SimdPaddle, preferInt8: false);
        var res = await ocr.RecognizeAsync(LoadFrame());
        var screen = {brain}.{classify_call};
        var act = {brain}.Decide(screen, res, screenStreak: 0);
        // TODO: 改成这一帧 Bot 应该做的动作（防「走到不动」类回归的核心断言）
        Assert.Equal({action_default}, act.Kind);
    }}
"""


def main():
    ap = argparse.ArgumentParser(description="把一张实机帧变成 RealFrame 回归测试（离线，只读）")
    ap.add_argument("frame", help="帧文件（jpg/png）")
    ap.add_argument("--name", required=True, help="用例名（同时作为 TestData 文件名主干）")
    ap.add_argument("--copy", action="store_true", help="把帧复制到 tests/EfootballBot.Tests/TestData/")
    ap.add_argument("--out", default="", help="把生成的 .cs 写到该目录（默认只打印）")
    ap.add_argument("--brain", default="MyLeagueBrain",
                    choices=["MyLeagueBrain", "PeakLadderBrain", "WeeklyBrain", "CnWeeklyBrain"])
    ap.add_argument("--ns", default="", help="Brain 所在命名空间（默认按 brain 推断）")
    args = ap.parse_args()

    if not os.path.isfile(args.frame):
        print(f"找不到帧文件：{args.frame}")
        return 1

    ext = os.path.splitext(args.frame)[1].lower() or ".png"
    safe = "".join(c if (c.isalnum() or c in "_-") else "_" for c in args.name)
    target_name = f"{safe}{ext}"
    dest = os.path.join(TESTDATA, target_name)
    if args.copy:
        os.makedirs(TESTDATA, exist_ok=True)
        n = 1
        while os.path.exists(dest) and os.path.getsize(dest) != os.path.getsize(args.frame):
            target_name = f"{safe}_{n}{ext}"
            dest = os.path.join(TESTDATA, target_name)
            n += 1
        if not os.path.exists(dest):
            shutil.copy2(args.frame, dest)
        print(f"[已复制] {args.frame} → {dest}")

    ns_map = {
        "MyLeagueBrain": ("MyLeague", "Classify(new List<YoloDetection>(), res)",
                          "MlScreen.Unknown", "MlActionKind.PadConfirm", True),
        "PeakLadderBrain": ("PeakLadder", "Classify(res)", "PlScreen.None", "", False),
        "WeeklyBrain": ("Weekly", "Classify(new List<YoloDetection>(), res)", "WkScreen.None", "", False),
        "CnWeeklyBrain": ("CnWeekly", "Classify(new List<YoloDetection>(), res)", "CwScreen.None", "", False),
    }
    ns, classify_call, screen_default, action_default, has_decide = ns_map[args.brain]
    if args.ns:
        ns = args.ns

    decide_test = DECIDE_TEST.format(
        name=safe, brain=args.brain, classify_call=classify_call,
        action_default=action_default) if has_decide else ""
    if not has_decide:
        decide_test = "\n    // 本 Brain 没有统一的 Decide 入口：决策断言请直接写该场景的判定函数\n"

    code = TEMPLATE.format(
        ns=ns,
        name=safe,
        cls=f"{safe}RealFrameTests",
        file=target_name,
        src=os.path.abspath(args.frame),
        brain=args.brain,
        classify_call=classify_call,
        screen_default=screen_default,
        decide_test=decide_test,
    )

    if args.out:
        os.makedirs(args.out, exist_ok=True)
        path = os.path.join(args.out, f"{safe}RealFrameTests.cs")
        with open(path, "w", encoding="utf-8") as f:
            f.write(code)
        print(f"[已生成] {path}")
        print("下一步：跑 `dotnet test --filter FullyQualifiedName~" + safe + "` 看 OCR 输出，再填 3 处 TODO。")
    else:
        print(code)
    return 0


if __name__ == "__main__":
    sys.exit(main())
