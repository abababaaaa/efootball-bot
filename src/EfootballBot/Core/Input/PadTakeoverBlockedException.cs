namespace EfootballBot.Core.Input;

/// <summary>
/// 真实手柄未让路导致虚拟手柄拿不到玩家 1 时的专用异常。
/// 与普通启动失败的差别：这是<b>需要用户动手</b>（关手柄电源/拔线）才能解决的问题，
/// Worker 会以 <see cref="Code"/> 上报，GUI 据此弹窗而不是只在日志里打一行。
/// </summary>
public sealed class PadTakeoverBlockedException : InvalidOperationException
{
    /// <summary>错误码前缀：GUI 用它识别"需要弹窗请用户物理断开手柄"，而不是普通启动失败。</summary>
    public const string Code = "[PAD_TAKEOVER_BLOCKED]";

    /// <summary>Worker 退出码：带此码退出 = 手柄未让路，别自动重启（用户不动手重试也是白跑）。</summary>
    public const int ExitCode = 42;

    public PadTakeoverBlockedException(string message) : base(message) { }

    /// <summary>给用户的弹窗正文（去掉错误码前缀）。</summary>
    public static string StripCode(string message)
        => message.StartsWith(Code, StringComparison.Ordinal) ? message[Code.Length..].TrimStart() : message;

    /// <summary>消息是否带本错误码。</summary>
    public static bool IsBlocked(string? message)
        => message is not null && message.Contains(Code, StringComparison.Ordinal);
}
