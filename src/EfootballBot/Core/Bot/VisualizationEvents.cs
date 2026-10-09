namespace EfootballBot.Core.Bot;

/// <summary>
/// 可视化事件载荷。Worker 侧派发，GUI 消费。
/// </summary>

/// <summary>感知摘要事件：画面来源 + OCR 词数 + 检测框数。</summary>
public sealed class ScreenEvent
{
    /// <summary>感知摘要文本（如 "OCR 12 词 / 检测 3 框"），旧字段名 Screen 保留以兼容管道协议。</summary>
    public string Screen { get; init; } = "";
    public double Score { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}
