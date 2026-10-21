using System.IO;
using System.IO.Compression;
using System.Net.Http;
using EfootballBot.Core.Bot;

namespace EfootballBot.Core.System;

/// <summary>
/// 外置模型包（瘦身发布用）：
/// 用 <c>-p:ExternalModels=true</c> 发布时，models\ 不再内嵌进单文件 exe，
/// 而是随包输出为 exe 同级的 models\ 目录；如果用户拿到的是「纯 exe」（没有 models\），
/// 首次开始挂机时按需下载一次。
///
/// 为什么不是默认：内嵌模型换来的「一个 exe 拷走就能用」很值钱，而瘦身省下的约 30MB
/// 只在弱网用户身上有感知。开关交给发布侧决定，运行侧只保证两条路径都能工作。
/// </summary>
public static class ModelPack
{
    /// <summary>OCR 必需文件（任一缺失即视为模型不完整）。</summary>
    private static readonly (string[] Parts, string Label)[] Required =
    {
        (new[] { "models", "v6", "PP-OCRv6_det_small.onnx" }, "检测模型"),
        (new[] { "models", "v6", "PP-OCRv6_rec_small.onnx" }, "识别模型"),
        (new[] { "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx" }, "方向分类模型"),
        (new[] { "models", "v6", "ppocrv6_small_dict.txt" }, "字典"),
    };

    /// <summary>模型文件是否齐全（不检查内容，只检查存在）。</summary>
    public static bool IsPresent(out string missing)
    {
        missing = "";
        foreach (var (parts, label) in Required)
        {
            if (AppPaths.FindBundledFile(parts) is null)
            {
                missing = label;
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 确保模型可用：已就位直接返回 true；缺失则从 R2 下载模型包解压到 data\models\。
    /// 下载失败不抛异常，只返回 false 并给出可执行的处理建议（不影响 GUI 打开）。
    /// </summary>
    public static async Task<bool> EnsureAsync(Action<string, LogLevel> log, CancellationToken ct = default)
    {
        if (IsPresent(out _)) return true;

        string baseUrl = Updater.R2PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            log("OCR 模型文件缺失，且当前版本未配置模型下载地址。请重新下载完整版程序（含 models 目录）。", LogLevel.Error);
            return false;
        }

        string url = baseUrl.TrimEnd('/') + "/models/ppocr-v6.zip";
        string target = Path.Combine(AppPaths.DataDir, "models");
        string tmpZip = Path.Combine(AppPaths.TempDir, "ppocr-v6.zip");
        try
        {
            Directory.CreateDirectory(AppPaths.TempDir);
            log($"OCR 模型未随包提供，正在下载模型包（约 30MB）：{url}", LogLevel.Info);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    log($"模型包下载失败（HTTP {(int)resp.StatusCode}）。请检查网络后重试，或重新下载完整版程序。", LogLevel.Error);
                    return false;
                }
                await using var fs = File.Create(tmpZip);
                await resp.Content.CopyToAsync(fs, ct);
            }

            // 解压到 data\models（覆盖旧文件）。zip 结构约定为 models/v6/*、models/v5/*
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(tmpZip, AppPaths.DataDir, overwriteFiles: true);
            try { File.Delete(tmpZip); } catch { }

            if (IsPresent(out string missing))
            {
                log("OCR 模型已就绪。", LogLevel.Success);
                return true;
            }
            log($"模型包解压后仍缺少{missing}，包结构可能不对。请重新下载完整版程序。", LogLevel.Error);
            return false;
        }
        catch (Exception ex)
        {
            log($"模型下载/解压失败：{ex.Message}。可手动把 models 目录放到 exe 旁边。", LogLevel.Error);
            return false;
        }
    }
}
