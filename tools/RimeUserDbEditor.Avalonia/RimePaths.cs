namespace RimeUserDbEditor;

internal static class RimePaths
{
    public static bool HasInstallationYaml(string userDir)
        => File.Exists(Path.Combine(userDir, "installation.yaml"));

    /// <summary>backup_user_dict / restore_user_dict 的 TSV 檔名慣例:
    /// <c>&lt;dict&gt;.userdb.txt</c> (見 librime/src/rime/dict/user_db.cc)。
    /// 集中在這裡,Rime sync_dir 搜尋、暫存快照路徑、SaveAs 預設檔名
    /// 各處共用同一個來源。</summary>
    public static string SnapshotFileName(string dictName)
        => $"{dictName}.userdb.txt";

    /// <summary>
    /// 從 <c>&lt;userDir&gt;/installation.yaml</c> 讀出 <c>distribution_*</c>,
    /// 原樣回填,跟常駐 IME 寫的對上。librime 比對的是 code_name 跟 version
    /// (deployment_tasks.cc:123-128);name 不比對,但 rewrite 時會被我們覆蓋,
    /// 所以也一起回填。fallback 只在「從未 deploy 過的 user_dir」才用得到 ——
    /// 呼叫端應先用 <see cref="HasInstallationYaml"/> 警告使用者,因為一旦走到
    /// fallback,librime 就會把這幾個值寫進新的 yaml,而 WeaselServer 下次又會
    /// 把它蓋回去,造成每次切換都重新部署一次。
    /// </summary>
    public static (string name, string codeName, string version) ReadInstallationIdentity(string userDir)
    {
        string yaml = Path.Combine(userDir, "installation.yaml");
        if (File.Exists(yaml))
        {
            string? name = null, code = null, ver = null;
            // 不是真的 YAML parser —— installation.yaml 是 librime 自己寫的,
            // distribution_* 是扁平 scalar,沒有 quoted-with-colons / 折行 /
            // anchor 之類花樣;為這三個字串不值得拉一整套 YAML 函式庫。
            foreach (var raw in File.ReadAllLines(yaml))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string key = raw[..colon].Trim();
                string val = raw[(colon + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "distribution_name":      name = val; break;
                    case "distribution_code_name": code = val; break;
                    case "distribution_version":   ver  = val; break;
                }
            }
            if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(ver))
                return (name ?? "Rime", code, ver);
        }
        return ("Rime", "RimeUserDbEditor", "0.0.0");
    }
}
