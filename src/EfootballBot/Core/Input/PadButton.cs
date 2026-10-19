namespace EfootballBot.Core.Input;

/// <summary>语义按键位（原 Xbox 360 位布局，各虚拟后端自行映射到自家报告格式）。</summary>
public enum PadButton : ushort
{
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,   // L3
    RightThumb = 0x0080,  // R3
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>方向（十字键与摇杆 Flick 共用）。</summary>
public enum PadDir { Up, Down, Left, Right }
