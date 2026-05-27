using System.Globalization;
using System.Text;

namespace RimeUserDbEditor;

/// <summary>
/// 一筆 Rime userdb 記錄。
///
/// Backup/snapshot 格式（plain_userdb_format）的磁碟 TSV 配置為：
///   code ⟨TAB⟩ phrase ⟨TAB⟩ c=N d=F t=T
///
/// 參見 librime/src/rime/dict/user_db.cc:
///   userdb_entry_formatter / userdb_entry_parser
/// </summary>
public sealed class UserDbEntry
{
    public string Text { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    /// <summary>提交（選字）次數。負值代表已刪除。</summary>
    public int Commits { get; set; }
    /// <summary>動態衰減權重 (dee)，影響候選排序，隨 tick 推進而衰減。</summary>
    public double Dee { get; set; }
    /// <summary>邏輯時間戳 (tick)，每次 commit 全域 +1。</summary>
    public ulong Tick { get; set; }

    public string PackValue()
        // "g6" = 6 位有效數字,鏡像 librime `UserDbValue::Pack()` 的
        // `std::ostringstream << dee` 預設精度 (user_db.cc:21-25)。用小寫 g 是
        // 為了科學記數法的指數字母 —— C# 大寫 "G6" 會輸出大寫 E (9.90387E-05),
        // 而 C++ ostream 是小寫 e (9.90387e-05);小寫 "g6" 才能跟 librime 自己
        // 寫的 byte-for-byte 一致。ranking 行為不受影響 (librime 本來就只保留
        // 6 位精度,std::stod 大小寫 e 都吃)。
        => $"c={Commits} d={Dee.ToString("g6", CultureInfo.InvariantCulture)} t={Tick}";

    public void UnpackValue(string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq < 0) continue;
            string k = token[..eq];
            string v = token[(eq + 1)..];
            switch (k)
            {
                case "c":
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
                        Commits = c;
                    break;
                case "d":
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        // 對齊 librime 自己的 userdb_entry_parser:
                        //   user_db.cc:40  dee = (std::min)(10000.0, std::stod(v));
                        // 實務 dee 是 formula_d(d,t,da,ta)=d+da*exp((ta-t)/200) 收斂的
                        // 值,正常用量在個位~十位數 (formula_p 把 d<20 當「大」分支),
                        // 10000 純粹是 librime 的硬上限,我們鏡像同一條線。
                        Dee = Math.Min(10000.0, d);
                    break;
                case "t":
                    if (ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong t))
                        Tick = t;
                    break;
            }
        }
    }
}

public sealed class UserDbModel
{
    public List<UserDbEntry> Entries { get; } = new();
    public List<KeyValuePair<string, string>> Metadata { get; } = new();
    public string FileDescription { get; set; } = "Rime user dictionary";
    public string? SourceDict { get; set; }

    public void Clear()
    {
        Entries.Clear();
        Metadata.Clear();
        FileDescription = "Rime user dictionary";
        SourceDict = null;
    }

    public void LoadFromFile(string path)
    {
        Clear();
        bool firstComment = true;
        bool enableComment = true;

        // ReadLines (streaming) 而非 ReadAllLines —— userdb 上看 10k+ 列,
        // 避免在解析前先把整份內容物化成 string[]。
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0) continue;
            if (enableComment && line[0] == '#')
            {
                if (line.StartsWith("#@"))
                {
                    var cols = line[2..].Split('\t');
                    if (cols.Length == 2) Metadata.Add(new(cols[0], cols[1]));
                }
                else if (line == "# no comment")
                {
                    enableComment = false;
                }
                else if (firstComment)
                {
                    FileDescription = line.TrimStart('#').TrimStart();
                }
                firstComment = false;
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
            var entry = new UserDbEntry
            {
                Code = parts[0].TrimEnd(),
                Text = parts[1],
            };
            if (parts.Length >= 3 && parts[2].Length > 0)
                entry.UnpackValue(parts[2]);
            Entries.Add(entry);
        }
    }

    public void SaveToFile(string path, Func<UserDbEntry, bool>? filter = null)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine($"# {FileDescription}");
        foreach (var kv in Metadata)
            w.WriteLine($"#@{kv.Key}\t{kv.Value}");
        foreach (var e in Entries)
        {
            if (filter != null && !filter(e)) continue;
            // `Code ⟨SPACE⟩⟨TAB⟩ Text ⟨TAB⟩ value` —— code 後面的那個空格不是
            // typo,是對齊 librime 自己 backup_user_dict 的輸出格式
            // (user_db.cc 的 plain_userdb_format / userdb_entry_formatter)。
            w.WriteLine($"{e.Code} \t{e.Text}\t{e.PackValue()}");
        }
    }
}
