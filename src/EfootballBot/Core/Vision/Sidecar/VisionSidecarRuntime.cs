using EfootballBot.Core.Capture;

namespace EfootballBot.Core.Vision.Sidecar;

/// <summary>
/// 侧车运行时注册表：把「正在做捕获的那个 sidecar 客户端」暴露给 OCR/YOLO，
/// 让两者共用同一个 vision-svc 进程（省掉第二个进程 + 第二份模型/ORT 内存），
/// 并在识别的是"刚捕获的那一帧"时跳过上传拷贝。
/// </summary>
internal static class VisionSidecarRuntime
{
    private static SidecarCaptureProvider? _capture;

    public static void RegisterCapture(SidecarCaptureProvider provider) => _capture = provider;

    public static void UnregisterCapture(SidecarCaptureProvider provider)
    {
        if (ReferenceEquals(_capture, provider)) _capture = null;
    }

    /// <summary>捕获侧车的客户端（未启用侧车捕获时为 null）。</summary>
    public static VisionSidecarClient? SharedClient => _capture?.Client;

    /// <summary>这一帧是否就是捕获侧车刚产出的那一帧（同尺寸 + 同时间戳）。</summary>
    public static bool MatchesCapturedFrame(FrameData frame) => _capture?.IsCurrentFrame(frame) == true;
}
