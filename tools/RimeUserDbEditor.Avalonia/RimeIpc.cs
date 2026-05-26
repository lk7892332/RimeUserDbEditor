using System.Buffers.Binary;
using System.IO.Pipes;

namespace RimeUserDbEditor;

/// <summary>
/// 跟 running WeaselServer.exe 對話的 named-pipe IPC client。
///
/// 只在 Windows 有意義 —— WeaselServer 本身只在 Windows 跑,wire protocol
/// 用 Windows named pipe + message-mode framing
/// (<c>\\.\pipe\&lt;username&gt;\WeaselNamedPipe</c>)。非 Windows 上每個進入
/// 點直接回 <c>false</c>,呼叫端不用再加 OS guard。
/// </summary>
internal static class RimeIpc
{
    // 出自 include/WeaselIPC.h
    private const int WM_APP = 0x8000;
    private const int WEASEL_IPC_START_MAINTENANCE = WM_APP + 9;  // 0x8009
    private const int WEASEL_IPC_END_MAINTENANCE   = WM_APP + 10; // 0x800A

    public static bool StartMaintenance() => SendCommand(WEASEL_IPC_START_MAINTENANCE);
    public static bool EndMaintenance()   => SendCommand(WEASEL_IPC_END_MAINTENANCE);

    private static bool SendCommand(int cmd)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string pipeName = $@"{Environment.UserName}\WeaselNamedPipe";

            using var client = new NamedPipeClientStream(
                ".", pipeName,
                PipeDirection.InOut,
                PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);

            client.Connect(1000);
            client.ReadMode = PipeTransmissionMode.Message;

            Span<byte> req = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian (req,         cmd);
            BinaryPrimitives.WriteUInt32LittleEndian(req[4..],    0);
            BinaryPrimitives.WriteUInt32LittleEndian(req[8..],    0);
            client.Write(req);
            client.Flush();

            Span<byte> reply = stackalloc byte[4];
            return client.Read(reply) > 0;
        }
        catch (Exception ex)
        {
            // Debug.WriteLine 在 release build 是 [Conditional("DEBUG")] no-op,
            // 不會在生產環境留 trace,但開發時可以早一點看到 pipe 連不上 / 協議
            // 不符的真實原因。UI 端只在乎 bool。
            System.Diagnostics.Debug.WriteLine($"RimeIpc.SendCommand(0x{cmd:X4}) failed: {ex.Message}");
            return false;
        }
    }
}
