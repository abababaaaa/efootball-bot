using System.Text;
using EfootballBot.Core.Input;

Console.OutputEncoding = Encoding.UTF8;

var nodes = PadNodeHygiene.Scan();
int physical = 0, stale = 0, live = 0, other = 0, inactive = 0;
foreach (var n in nodes)
{
    var kind = PadNodeHygiene.Classify(n.IsVirtual, n.Started, n.RealBus);
    switch (kind)
    {
        case PadNodeKind.Physical: physical++; break;
        case PadNodeKind.VirtualStale: stale++; break;
        case PadNodeKind.VirtualLive: live++; break;
        case PadNodeKind.OtherVirtual: other++; break;
        default: inactive++; break;
    }
    Console.WriteLine($"  [{kind,-12}] started={n.Started,-5} virtual={n.IsVirtual,-5} bus={n.RealBus,-5} {n.Name}");
    Console.WriteLine($"                 {n.InstanceId}  service={n.Service}");
}
bool slot0 = PadBackendProbe.IsSlotOccupied(0);
Console.WriteLine();
Console.WriteLine($"counts: physical={physical} live={live} stale={stale} otherVirtual={other} inactive={inactive}");
Console.WriteLine($"XInput slot0 occupied = {slot0}");
Console.WriteLine($"gate CanTouchDevices = {PadNodeHygiene.CanTouchDevices(physical, slot0)}");

Console.WriteLine();
Console.WriteLine("== Run(apply:false) 预览 ==");
var preview = PadNodeHygiene.Run(apply: false, force: false, restartBus: false, log: Console.WriteLine);
Console.WriteLine("preview: " + preview.Message);

Console.WriteLine();
Console.WriteLine("== Run(apply:true) 非提权下的降级行为（预期 pnputil 拒绝，不抛异常） ==");
var applied = PadNodeHygiene.Run(apply: true, force: false, restartBus: false, log: Console.WriteLine);
Console.WriteLine("apply: " + applied.Message + $" touched={applied.TouchedDevices}");
