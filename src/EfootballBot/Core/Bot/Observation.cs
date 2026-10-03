using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

public sealed record Observation(FrameData Frame, OcrResult Ocr, GameScreen Screen, double Score, IReadOnlyList<string> Evidence)
{
    public bool Is(GameScreen s) => Screen == s;
    public bool AnyOf(params GameScreen[] ss) => ss.Contains(Screen);
}

public enum LogLevel { Info, Success, Warn, Error, Debug }