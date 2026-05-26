using System.Diagnostics;

namespace RimeUserDbEditor;

/// <summary>WeaselServer.exe 行程控制。非 Windows 上全部 no-op
/// (ibus-rime / fcitx5-rime 不在處理範圍)。</summary>
internal static class WeaselServer
{
    private const string ProcessName = "WeaselServer";
    private const string ExeFileName = ProcessName + ".exe";

    public static bool IsRunning()
        => OperatingSystem.IsWindows()
        && Process.GetProcessesByName(ProcessName).Length > 0;

    private static string? ResolveExe(string installDir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var exe = Path.Combine(installDir, ExeFileName);
        return File.Exists(exe) ? exe : null;
    }

    public static bool TryQuit(string installDir)
    {
        if (ResolveExe(installDir) is not string exe) return false;
        try
        {
            // `/q` 會開一個 helper process 去送 quit 訊息 —— 先抓住真正的
            // server PID,等下直接等它退。
            var existing = Process.GetProcessesByName(ProcessName);

            using (var helper = Process.Start(new ProcessStartInfo(exe, "/q")
            {
                WorkingDirectory = installDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                helper?.WaitForExit(5000);
            }

            bool allExited = true;
            foreach (var p in existing)
            {
                try
                {
                    // WaitForExit(timeout) 回 false = 還在跑;我們把這視為失敗,
                    // 不要讓呼叫端誤以為可以放心 Directory.Delete。
                    if (!p.WaitForExit(3000)) allExited = false;
                }
                catch { /* 已退且 handle 被 dispose —— 視為已退 */ }
                p.Dispose();
            }
            return allExited && !IsRunning();
        }
        catch { return false; }
    }

    public static bool TryStart(string installDir)
    {
        if (ResolveExe(installDir) is not string exe) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = installDir,
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }
}
