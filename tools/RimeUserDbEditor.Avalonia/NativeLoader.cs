using System.Reflection;
using System.Runtime.InteropServices;

namespace RimeUserDbEditor;

/// <summary>
/// 把 <c>[LibraryImport("rime")]</c> 導向 <see cref="AppConfig.RimeLib"/>;
/// 第一次 P/Invoke 前要先呼叫 <see cref="SetLibraryPath"/>。
/// </summary>
internal static class NativeLoader
{
    private static string s_libPath = string.Empty;
    private static bool   s_registered;

    // resolver 註冊是 one-shot,重複呼叫只更新目標路徑。一旦載入成功 runtime
    // 會把 handle cache 起來,不會再走 resolver —— 換路徑只對「第一次載入失敗
    // 後重試」這個情境有意義。
    public static void SetLibraryPath(string absolutePath)
    {
        s_libPath = absolutePath;
        if (s_registered) return;
        NativeLibrary.SetDllImportResolver(typeof(Rime).Assembly, Resolve);
        s_registered = true;
    }

    private static IntPtr Resolve(string libraryName, Assembly _, DllImportSearchPath? __)
    {
        if (libraryName != "rime" || string.IsNullOrEmpty(s_libPath)) return IntPtr.Zero;
        return NativeLibrary.TryLoad(s_libPath, out IntPtr handle) ? handle : IntPtr.Zero;
    }
}
