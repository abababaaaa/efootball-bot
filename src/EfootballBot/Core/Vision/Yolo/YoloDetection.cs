namespace EfootballBot.Core.Vision.Yolo;

/// <summary>YOLO 单条检测结果（坐标归一化到 0~1，相对捕获帧）。</summary>
public sealed record YoloDetection(
    string ClassName,
    int ClassId,
    float Score,
    double X,
    double Y,
    double W,
    double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
}
