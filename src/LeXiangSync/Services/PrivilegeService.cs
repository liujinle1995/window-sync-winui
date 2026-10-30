using System.Diagnostics;
using System.Security.Principal;

namespace LeXiangSync.Services;

/// <summary>管理员权限检测与提权重启。向提权进程 PostMessage 需要同级或更高完整性级别。</summary>
public static class PrivilegeService
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员身份重启当前程序，返回是否成功发起（调用方需随后退出当前进程）。</summary>
    public static bool RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消了 UAC 提示
            return false;
        }
    }
}
