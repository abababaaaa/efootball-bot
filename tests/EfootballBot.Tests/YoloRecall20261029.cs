using EfootballBot.Core.Capture;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Vision.Yolo;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace EfootballBot.Tests;

// 检测召回量化（2026-10-29）：对 18 张 val 图逐框比对标注，回答「检测侧还有没有漏检可挖」。
// 背景：外部资料主张「YOLO 漏检根因在 Neck」。本项目只有 6 类大尺寸 UI 元素、固定分辨率、
// 164 张训练图，与「小目标 + 密集场景」不是一个问题域，先用数字确认再决定要不要碰结构。
// 口径：IoU≥0.5 且类别一致算命中，按分数贪心一对一匹配；置信度用生产默认 0.25。
public class YoloRecall20261029
{
    private readonly ITestOutputHelper _out;
    public YoloRecall20261029(ITestOutputHelper output) => _out = output;

    private static string RepoDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
    private static string ValDir => Path.Combine(RepoDir, "training", "dataset");

    private sealed record Gt(int Cls, float Cx, float Cy, float W, float H);

    [Fact]
    public void Measure_Recall_On_ValSet()
    {
        string imgDir = Path.Combine(ValDir, "images", "val");
        string lblDir = Path.Combine(ValDir, "labels", "val");
        if (!Directory.Exists(imgDir))
        {
            _out.WriteLine("（training/dataset 不在本地，跳过）");
            return;
        }

        string modelPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "yolo26n.onnx");
        string classesPath = Path.Combine(AppContext.BaseDirectory, "models", "yolo", "classes.txt");
        var classes = File.ReadAllLines(classesPath).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        using var det = new YoloDetector(modelPath, classesPath, new OnnxSessionFactory("CPU"));

        var perClassGt = new int[classes.Length];
        var perClassHit = new int[classes.Length];
        int gtTotal = 0, hitTotal = 0, fpTotal = 0;
        var details = new List<string>();

        foreach (var img in Directory.GetFiles(imgDir).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(img);
            string lbl = Path.Combine(lblDir, name + ".txt");
            if (!File.Exists(lbl)) continue;

            var frame = LoadFrame(img);
            var gts = new List<Gt>();
            foreach (var line in File.ReadAllLines(lbl))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5) continue;
                gts.Add(new Gt(int.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]), float.Parse(p[4])));
            }
            var dets = det.Detect(frame);

            var used = new bool[dets.Count];
            var missed = new List<string>();
            int imgHit = 0;
            foreach (var g in gts)
            {
                // GT 归一化 → 像素 xyxy
                float gx1 = (g.Cx - g.W / 2) * frame.Width, gx2 = (g.Cx + g.W / 2) * frame.Width;
                float gy1 = (g.Cy - g.H / 2) * frame.Height, gy2 = (g.Cy + g.H / 2) * frame.Height;
                int best = -1; double bestIou = 0;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used[i] || dets[i].ClassId != g.Cls) continue;
                    double iou = Iou(gx1, gy1, gx2, gy2, dets[i], frame);
                    if (iou >= 0.5 && iou > bestIou) { bestIou = iou; best = i; }
                }
                perClassGt[g.Cls]++;
                gtTotal++;
                if (best >= 0) { used[best] = true; perClassHit[g.Cls]++; hitTotal++; imgHit++; }
                else
                {
                    missed.Add($"{classes[g.Cls]}({g.Cx:F2},{g.Cy:F2},{g.W:F2}×{g.H:F2})");
                }
            }
            int fp = used.Count(u => !u);
            fpTotal += fp;
            if (missed.Count > 0 || fp > 0)
            {
                var extra = dets.Where((_, i) => !used[i])
                    .Select(d => $"{d.ClassName}:{d.Score:F2}({d.X:F2},{d.Y:F2},{d.W:F2}×{d.H:F2})");
                details.Add($"  {name}: GT {gts.Count}（命中 {imgHit}），漏 {string.Join(" ", missed)}，多余 {fp} [{string.Join(" ", extra)}]");
            }
        }

        _out.WriteLine($"| 类别 | 标注数 | 命中 | 召回 |");
        _out.WriteLine($"|---|---|---|---|");
        for (int i = 0; i < classes.Length; i++)
        {
            string rec = perClassGt[i] == 0 ? "n/a" : $"{perClassHit[i] * 100.0 / perClassGt[i]:F0}%";
            _out.WriteLine($"| {classes[i]} | {perClassGt[i]} | {perClassHit[i]} | {rec} |");
        }
        _out.WriteLine($"| **合计** | **{gtTotal}** | **{hitTotal}** | **{hitTotal * 100.0 / Math.Max(1, gtTotal):F0}%** |");
        _out.WriteLine($"多余检出（未匹配标注）: {fpTotal}");
        _out.WriteLine("");
        foreach (var d in details) _out.WriteLine(d);
    }

    private static double Iou(float ax1, float ay1, float ax2, float ay2, YoloDetection d, FrameData f)
    {
        float bx1 = (float)(d.X * f.Width), by1 = (float)(d.Y * f.Height);
        float bx2 = bx1 + (float)(d.W * f.Width), by2 = by1 + (float)(d.H * f.Height);
        float ix1 = Math.Max(ax1, bx1), iy1 = Math.Max(ay1, by1);
        float ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        double inter = (double)iw * ih;
        double uni = (double)(ax2 - ax1) * (ay2 - ay1) + (double)(bx2 - bx1) * (by2 - by1) - inter;
        return uni <= 0 ? 0 : inter / uni;
    }

    private static FrameData LoadFrame(string path)
    {
        using var mat = Cv2.ImRead(path, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(mat, bgra, ColorConversionCodes.BGR2BGRA);
        var data = new byte[(int)(bgra.Total() * bgra.ElemSize())];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, data, 0, data.Length);
        return new FrameData(data, mat.Width, mat.Height, 0);
    }
}
