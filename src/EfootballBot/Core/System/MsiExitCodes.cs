namespace EfootballBot.Core.System;

/// <summary>驱动安装流程的结果类别。</summary>
public enum DriverSetupResultKind
{
    Installed,
    RebootRequired,
    AlreadyPresent,
    Failed,
    UserCancelled,
    Timeout,
}

/// <summary>MSI / Advanced Installer 标准退出码映射。</summary>
internal static class MsiExitCodes
{
    public static DriverSetupResultKind Map(int code) => code switch
    {
        0 => DriverSetupResultKind.Installed,
        3010 => DriverSetupResultKind.RebootRequired,
        1638 => DriverSetupResultKind.AlreadyPresent,
        1602 => DriverSetupResultKind.UserCancelled,
        _ => DriverSetupResultKind.Failed,
    };
}
