using Avalonia;

namespace RimeUserDbEditor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Setup 沒跑過時 Shutdown 自動 short-circuit。
            try { Rime.Shutdown(); } catch { }
        }
    }

    // 同時也是 Avalonia previewer / designer 的進入點。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
