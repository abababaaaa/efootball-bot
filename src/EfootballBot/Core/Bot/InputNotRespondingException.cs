namespace EfootballBot.Core.Bot;

/// <summary>
/// 主页方向键自检两次无响应：游戏未收到任何手柄输入。
/// 常见于掌机睡眠唤醒后 Steam 输入转发层卡死——系统里手柄显示在线，但游戏收不到任何键。
/// </summary>
public sealed class InputNotRespondingException : Exception
{
    public const string Guidance =
        "游戏未收到任何手柄输入（主页方向键自检无响应）。掌机睡眠唤醒后常见："
        + "请完全退出 Steam（托盘右键→退出）后重开游戏再试，必要时重启掌机；"
        + "也可在 Steam 库中右键 eFootball→属性→控制器，停用 Steam Input。";

    public InputNotRespondingException() : base(Guidance) { }
}
