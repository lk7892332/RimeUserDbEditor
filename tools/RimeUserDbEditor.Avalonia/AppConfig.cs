using System.Text.Json;

namespace RimeUserDbEditor;

/// <summary>跟 exe 同目錄的 <c>config.json</c> —— 可攜,但 exe 目錄要可寫。</summary>
internal sealed class AppConfig
{
    /// <summary>共用資料目錄,裡面直接放 *.schema.yaml 等檔案
    /// (Windows: <c>...\weasel-x.y.z\data\</c>;Linux: <c>/usr/share/rime-data</c>)。</summary>
    public string SharedDataDir { get; set; } = string.Empty;

    /// <summary>Rime 使用者資料目錄,裡面有 *.userdb、installation.yaml、*.custom.yaml。</summary>
    public string UserDir { get; set; } = string.Empty;

    /// <summary>librime 動態庫絕對路徑 (rime.dll / librime.so* / librime.dylib)。</summary>
    public string RimeLib { get; set; } = string.Empty;

    private static string FilePath
        => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (c != null) return c;
            }
        }
        catch { /* 損毀或讀不到 → 落到下面回傳全新預設值 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 盡力儲存,失敗不算錯 */ }
    }
}
