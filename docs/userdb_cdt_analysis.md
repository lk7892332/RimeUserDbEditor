# Rime UserDb 的 `c`、`d`、`t` 欄位分析

本文件整理 Rime 使用者詞庫(UserDb)裡每筆詞條三個核心欄位 `c` / `d` / `t` 的語意、librime 內部的兩套格式、merger 的合併規則,以及 [`RimeUserDbEditor.Avalonia`](../tools/RimeUserDbEditor.Avalonia/) 如何處理這些細節。

---

## 一、`c`、`d`、`t` 的定義

來源:`librime/src/rime/dict/user_db.h` 的 `UserDbValue`

```cpp
struct UserDbValue {
  int commits = 0;       // c
  double dee = 0.0;      // d
  TickCount tick = 0;    // t  (uint64_t)
};
```

序列化後在 LevelDB 內部以字串形式存:

```
c=10 d=0.000235 t=512
```

| 欄位 | 變數 | 型別 | 意義 |
|---|---|---|---|
| **`c`** | `commits` | `int` | 該詞被使用者選擇/提交的累計次數。**正值**為有效詞條;**負值**為「墓碑」 — `commits < 0` 在查詢時會被跳過,該詞不再出現在候選清單。 |
| **`d`** | `dee` | `double` | 動態權重因子(decay factor)。會隨著全域 tick 的推進透過 `formula_d()` 衰減,所以越久沒用的詞 `d` 越低。上限 capped 在 10000.0。 |
| **`t`** | `tick` | `uint64_t` | 邏輯時間戳。**不是真實時間**,而是 Rime 自己維護的全域邏輯時鐘 — 每次使用者 commit 一個詞,全域 tick 就 +1。記錄該詞條最後一次被更新時的 tick 值。 |

### 權重計算(候選詞排序)

`librime/src/rime/dict/user_dictionary.cc` 的 `CreateDictEntry()`:

```cpp
// 若該詞 tick 落後於全域 present_tick,先用 formula_d 衰減 dee
if (v.tick < present_tick)
    v.dee = algo::formula_d(0, present_tick, v.dee, v.tick);

// 最終權重
double weight = algo::formula_p(0, v.commits / present_tick, present_tick, v.dee);
e->weight = log(weight > 0 ? weight : DBL_EPSILON) + credibility;
```

簡言之:
- `c` 影響**基礎頻率**(`commits / present_tick` 是歸一化的頻率)
- `d` 是**主排序鍵**,衰減後值越大候選排越前
- `t` **不直接出現在權重公式裡**,而是「告訴 `formula_d`:這筆要從哪個時間點開始衰減」

---

## 二、librime 的兩套 TSV 格式

librime 內部有 **兩套**「userdb 倒成文字檔」的格式,因為定位不同 — 一個是給人讀的(會 lossy),一個是同步用的(保留完整資訊):

### Export 格式 — `TableDb::format`(❌ 不再使用)

`librime/src/rime/dict/table_db.cc`,用於 `export_user_dict` / `import_user_dict`:

```
phrase<TAB>code<TAB>commits
```

```
你好	ni hao	15
```

> [!WARNING]
> **這個格式會丟失 `d` 和 `t`**。import 時靠 `dee = (commits + 1) / 1e8` 估算 dee,`tick = 0` — 使用者的時間衰減歷史完全重置。

### Backup/snapshot 格式 — `plain_userdb_format`(本工具走這條)

`librime/src/rime/dict/user_db.cc`,用於 `backup_user_dict` / `restore_user_dict`,保留完整 `c=N d=F t=T`:

```
code <TAB>phrase<TAB>c=15 d=0.000235 t=512
```

```
ni hao 	你好	c=15 d=0.000235 t=512
```

注意 key 順序:**code 在前、phrase 在後**(跟 export 相反)。

> [!IMPORTANT]
> 本編輯器**全程走 backup/restore**,不再用 export/import,所以 [Rime.cs](../tools/RimeUserDbEditor.Avalonia/Rime.cs) 連 `export_user_dict` / `import_user_dict` 的 fn pointer 都沒綁。

### `d` 的序列化精度

`UserDbValue::Pack()` ([user_db.cc:21-25](../weasel/librime/src/rime/dict/user_db.cc#L21-L25)) 用 `std::ostringstream << dee` 寫出 `dee`,套用 C++ stdlib 預設 floating-point 設定:**precision 6**、指數 < −4 或 ≥ 6 自動切科學記號。所以一筆久沒用的詞會看到 `d=9.90387e-05` 之類的輸出 — 不是 librime 自己選的格式,純粹是 stdlib 預設。

Editor 端用 `double.TryParse(..., NumberStyles.Float, InvariantCulture)` 讀(吃得下科學記號),寫回時用 `Dee.ToString("G", InvariantCulture)`(.NET 預設 precision 15、round-trip safe)。**數值一致但 disk 字串不會 byte-for-byte 相同**。要嚴格鏡像 librime 的精度可改 `"G6"`,但實務上沒差 — librime 自己 round-trip 一次也不保證 byte-equal,因為它 read 進來就是已降到 6 位的 double。

`dee` 在 [user_db.cc:40](../weasel/librime/src/rime/dict/user_db.cc#L40) 讀取時硬上限 `min(10000.0, ...)`、無下限,所以老詞會持續衰減到趨近 0(只是不會 underflow 成負值)。

---

## 三、合併規則:`UserDbMerger`(Save 路徑)

關鍵源碼:`librime/src/rime/dict/user_db.cc` 的 `UserDbMerger::Put`

```cpp
bool UserDbMerger::Put(const string& key, const string& value) {
  UserDbValue v(value);                              // 你的 snapshot 這筆
  if (v.tick < their_tick_)                          // 用 snapshot 的全域 tick 衰減
    v.dee = formula_d(0, their_tick_, v.dee, v.tick);

  UserDbValue o;
  if (db_->Fetch(key, &our_value)) o.Unpack(our_value);   // DB 這筆
  if (o.tick < our_tick_)                            // 用 DB 的全域 tick 衰減
    o.dee = formula_d(0, our_tick_, o.dee, o.tick);

  if (abs(o.commits) < abs(v.commits)) o.commits = v.commits;
  o.dee  = max(o.dee, v.dee);
  o.tick = max_tick_;                                // ← 強制統一
  return db_->Update(key, o.Pack());
}
```

合併結束時 `CloseMerge()` 把 DB 的 `/tick` metadata 也更新成 `max_tick_`。

| 欄位 | 合併規則 |
|---|---|
| `c` (commits) | `abs(o.c) < abs(v.c)` 才覆蓋。**絕對值較大者勝**(帶符號) |
| `d` (dee) | 兩邊各自依 `our_tick_` / `their_tick_` 衰減後,取 `max(o.d, v.d)` |
| `t` (tick) | **不分輸贏**,merge 後一律覆蓋為 `max(our_tick, their_tick)` |

### `d` 的「decay-then-max」不是「在統一 tick 重算」

注意上面 `d` 那條:`o.dee` 衰減到 **`our_tick_`**,`v.dee` 衰減到 **`their_tick_`** — 兩邊衰減基準點**不同**。然後直接 `max(o.dee, v.dee)`,贏的那邊被搭配 `t = max_tick_`。如果 `our_tick_ ≠ their_tick_`,被選中的 dee 實際對應的是較舊的 tick,但會被當成 `max_tick_` 的快照處理 — **造成輕微偏高**。`formula_d` 在後續查詢時會用 entry 的 `t` 當衰減基準,所以這個偏差會隨後續使用慢慢被攤平,但合併當下不是嚴格意義上的「統一 tick 重算」。

### 直觀後果

1. **「沒列在檔案裡」的詞條,DB 裡仍然會留著** — merger 只動有列出的 key。要徹底清掉只能用 Wipe and Rebuild。
2. **`t` 改小會被忽略** 這個**直覺是錯的** — 個別 entry 的 `t` 在合併時不影響採用哪一版,只決定 `d` 在被比較前要衰減多少。
3. **想刪一個 `c=10` 的詞,把它設成 `c=-1` 沒用** — `|10| ≥ |1|`,墓碑被忽略。要設絕對值更大的負數(下一節說明)。
4. **純 Save 不會做全表 tick 統一** — 只有 snapshot 列出的 entry 會被 Put,沒列的 entry `tick` / `dee` 留在原地。DB-level `/tick` metadata 雖然被推到 `max_tick_`,但不會回去掃過所有 entry。

---

## 四、Rime 內建 Ctrl+Delete 的「對稱翻負」(Edit 直寫路徑)

`librime/src/rime/dict/user_dictionary.cc` 的 `UserDictionary::UpdateEntry` 在刪除分支:

```cpp
} else if (commits < 0) {  // mark as deleted
  v.commits = std::min(-1, -v.commits);                     // ★ 對稱翻負
  v.dee = formula_d(0.0, tick_, v.dee, v.tick);
}
v.tick = tick_;
return db_->Update(key, v.Pack());                          // 直接寫 LevelDB,不走 merger
```

注意:這條路徑 **不經過 `UserDbMerger`**,是直接 `db_->Update`。它跟 backup/restore 是完全分開的兩條路。

### 對稱翻負的結果

| 原本 `commits` | `-orig` | `min(-1, -orig)` | 意義 |
|---|---|---|---|
| 0(新詞) | 0 | **-1** | 標準墓碑 |
| 10 | -10 | **-10** | 保留絕對值翻負 |
| 100 | -100 | **-100** | 同上 |
| -5(已是墓碑) | 5 | **-1** | 縮回 -1 |

### 為什麼設計成這樣

對照第三節的 merger 規則 `abs(o.c) < abs(v.c)`:

- 原 DB `c=10`,**內建刪詞**後變 `c=-10`(直接 Update,不經 merge)
- 之後 sync 來一份舊 snapshot,裡面是 `c=10`,DB 是 `c=-10`:`|10| < |10|` 為 false → 不覆蓋,**墓碑保住**

如果內建刪詞用「永遠寫 -1」:
- 原 DB `c=10`,刪除後變 `c=-1`,然後 sync 來 `c=10` → `|−1| < |10|` 為 true → **墓碑被原值覆蓋,刪掉的詞復活了**

對稱翻負就是為了讓「本機刪除」在 sync/merge 場景下不被遠端 stale 資料復活。**Rime 整個刪詞語意建立在「絕對值大者勝,符號靠最後一次操作」這個 invariant 上**。

---

## 五、`RimeUserDbEditor.Avalonia` 對應的設計

### 資料模型

[UserDbModel.cs](../tools/RimeUserDbEditor.Avalonia/UserDbModel.cs) 完整建模 `c` / `d` / `t`:

```csharp
public sealed class UserDbEntry
{
    public string Text { get; set; } = string.Empty;    // phrase
    public string Code { get; set; } = string.Empty;    // code
    public int    Commits { get; set; }                  // c
    public double Dee     { get; set; }                  // d
    public ulong  Tick    { get; set; }                  // t

    public string PackValue()  => $"c={Commits} d={Dee:G} t={Tick}";
    public void   UnpackValue(string value) { /* parse "c=N d=F t=T" */ }
}
```

格式只支援 **backup 格式**(`code\tphrase\tc=N d=F t=T`),不再相容 export 舊格式。

### A. Load From Rime:強制喚起最新 backup

`LoadFromRimeAsync` 先呼叫 `Rime.BackupUserDict(dictName)` 觸發 librime 寫出 snapshot,再讀回:

```
+----------+   BackupUserDict   +----------+   find snapshot   +----------+   parse   +----------+
| LevelDB  | -----------------> | rime.dll | ----------------> | .txt 檔  | --------> | 編輯器   |
| (locked) |                    |          |                   |          |           | model    |
+----------+                    +----------+                   +----------+           +----------+
```

這是**唯一保留完整 c/d/t 的讀取路徑**。`export_user_dict` 路徑已移除,不再做 fallback。

### B. Delete 按鈕:對稱翻負(對齊 Rime 內建 Ctrl+Delete)

`DeleteButton_Click` 對選中項做:

```csharp
entry.Commits = Math.Min(-1, -entry.Commits);
```

跟第四節 `UserDictionary::UpdateEntry` 的算法**完全相同**。實際刪除後該詞被加上墓碑(`c < 0`),UI 預設過濾掉(可勾 `Show Deleted` 看)。Save 時走 backup → restore_user_dict → merger,因為墓碑值絕對值 ≥ DB 原值,merger 會接受。

### C. Save To Rime:backup 格式 + restore_user_dict

```
+----------+   write   +----------+   RestoreUserDictFromFile   +----------+
| 編輯器   | --------> | .txt 檔  | --------------------------> | LevelDB  |
| model    |           |          |                             | (merger) |
+----------+           +----------+                             +----------+
```

寫 backup 格式暫存檔,呼叫 `Rime.RestoreUserDictFromFile`,librime 透過 `UserDbMerger` 把每筆 merge 進 LevelDB。

### D. Wipe and Rebuild DB:繞過 merger 的徹底重建

當 merger 規則擋路(例如想批次降低 c、想徹底清光墓碑、想擺脫累積的 sync 殘留)時,用 Wipe and Rebuild:

1. 確保 WeaselServer 完全退出(連 IPC maintenance 都不夠 — 要釋放 LevelDB lock)
2. **物理刪除** `<user_data_dir>\<dict>.userdb\` 整個目錄
3. **記憶體中所有 `Commits < 0` 的墓碑紀錄移除**
4. 寫 backup 格式 → `RestoreUserDictFromFile` 從零建一個新的 LevelDB(因為 DB 是空的,merger 對每筆都是純 insert)

這條路徑徹底避開 merger 的「沒列就保留」和「絕對值較小不覆蓋」兩個性質。

**額外副作用 — 全表 tick normalization**:Restore 對空 DB 跑時,`our_tick_ = 0`、`o` 全是預設值,所以每筆 entry 的處理變成:`v.dee` 從自己的 `v.tick` 衰減到 `their_tick_`(snapshot 的 `/tick` metadata),最終結果 `dee = decayed_v.dee`、`tick = their_tick_`。**所有 entry 一致地統一在同一個 tick 基準上、dee 也一致地衰減過一次**,等於做了一次全表 normalization。純 Save 路徑沒有這個效果(只動 snapshot 列出的 key)。所以如果你的目的就是「重置 tick、強迫 dee 重新衰減」,Wipe and Rebuild 是唯一達得到的路徑。

---

## 六、總結對照表

| 項目 | librime DB | Backup 檔 | Editor model | 各路徑寫回行為 |
|------|-----------|----------|-------------|---------------|
| **`c`** | int | `c=N` 第三欄 | `Commits` | restore = abs 較大者勝;Ctrl+Delete = `min(-1, -orig)` |
| **`d`** | double | `d=F` | `Dee` | restore = 衰減後取 max;Ctrl+Delete = 衰減到 0 起重算 |
| **`t`** | uint64 | `t=T` | `Tick` | restore = 一律覆蓋為 `max(our, their)`;Ctrl+Delete = 設為當下 `tick_` |

| 操作 | 編輯器入口 | 走哪條 librime 路徑 |
|---|---|---|
| 看一個 userdb 內容 | Load From Rime | `backup_user_dict` → 讀 snapshot |
| 提權某詞 | 改 commits 為較大正值 → Save | `restore_user_dict` → UserDbMerger |
| 刪除某詞 | Delete 按鈕 → Save | `restore_user_dict` → UserDbMerger(墓碑 abs ≥ 原值,通過) |
| 徹底清乾淨 | Wipe and Rebuild | 物理刪 dir + `restore_user_dict` 從零建 |

## 七、實務建議

* **提權詞**:`Commits` 直接加到較大值(例如 100),`d` 和 `t` 不用動 — Rime 下次選字時會自動依新的 c 重算 d。
* **刪詞**:用編輯器的 Delete 按鈕(對稱翻負);**不要自己手動把 c 設成 -1**,會被 merger 忽略。
* **碰到頑固的詞復活**:多半是 sync 殘留。先 Wipe and Rebuild,或檢查 sync_dir 裡別的機器的 snapshot 有沒有也清掉。
* **`t` 不要手動改小**:沒效果(merger 強制統一)。要重算衰減就 commits 加大、讓 Rime 自己重新走一遍 `formula_d`。
