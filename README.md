# RimeUserDbEditor.Avalonia

跨平台的 Rime 使用者詞典 (LevelDB-backed `*.userdb/`) 圖形編輯器。Avalonia 12 /
.NET 10,單一 codebase 在 Windows、Linux、macOS 上都能 build 與執行。透過
`rime_get_api()` 直接 P/Invoke librime —— 不必自己 build librime 或 Weasel。

`c` / `d` / `t` 三個欄位的語意以及 `UserDbMerger` 的合併規則 (這決定你的修改
最終會不會生效) 詳見 [docs/userdb_cdt_analysis.md](docs/userdb_cdt_analysis.md)。

## 執行

```powershell
cd tools\RimeUserDbEditor.Avalonia
dotnet run
```

首次啟動時編輯器會用資料夾/檔案選擇器問你三件事,結果寫到 exe 同層的
`config.json`:

| 鍵 | 要選什麼 |
|---|---|
| `shared_data_dir` | 直接放著 `*.schema.yaml` 那層共用資料目錄。Windows: `C:\Program Files (x86)\Rime\weasel-[版本]\data\`;Linux: `/usr/share/rime-data` (來自 `ibus-rime` / `fcitx5-rime` 套件);macOS: `/Library/Input Methods/Squirrel.app/Contents/SharedSupport/`。 |
| `user_dir` | Rime 使用者資料目錄 —— 裡面有 `installation.yaml`、`*.userdb/` leveldb 子目錄、`*.custom.yaml`。 |
| `rime_lib` | librime native 的絕對路徑 —— Windows 是 `rime.dll`、macOS 是 `librime.dylib` (或 `librime.1.dylib`)、Linux 是 `librime.so.1`。 |

之後啟動會直接讀 `config.json` 跳過選擇器。三項其中之一失效時 (被改名、刪除、
librime ABI 不對) 編輯器會清空設定重新問。沒有自動偵測 —— 每條路徑都來自
使用者親自選的結果。

## 架構分層

檔案維持平鋪,但邏輯上分成六層,從上往下依賴:

| 層 | 檔案 | 職責 |
|---|---|---|
| ① Bootstrap | [Program.cs](tools/RimeUserDbEditor.Avalonia/Program.cs)、[App.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/App.axaml.cs) | Avalonia 進入點、desktop lifetime、`Rime.Shutdown()` 收尾 |
| ② UI | [MainWindow.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/MainWindow.axaml.cs)、[EntryEditWindow.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/EntryEditWindow.axaml.cs)、[Dialogs.cs](tools/RimeUserDbEditor.Avalonia/Dialogs.cs) | 主畫面、編輯 modal、共用 message-box 擴充方法 |
| ③ Domain | [UserDbModel.cs](tools/RimeUserDbEditor.Avalonia/UserDbModel.cs) | 純記憶體模型 + backup-format TSV 讀寫 |
| ④ librime 整合 | [NativeLoader.cs](tools/RimeUserDbEditor.Avalonia/NativeLoader.cs)、[Rime.cs](tools/RimeUserDbEditor.Avalonia/Rime.cs)、[RimePaths.cs](tools/RimeUserDbEditor.Avalonia/RimePaths.cs) | DllImport resolver、`rime_get_api()` P/Invoke、`installation.yaml` 與檔名慣例 |
| ⑤ Weasel 協調 (Windows-only) | [WeaselServer.cs](tools/RimeUserDbEditor.Avalonia/WeaselServer.cs)、[RimeIpc.cs](tools/RimeUserDbEditor.Avalonia/RimeIpc.cs) | 行程控制、named-pipe IPC |
| ⑥ Config | [AppConfig.cs](tools/RimeUserDbEditor.Avalonia/AppConfig.cs) | `config.json` 讀寫 |

依賴方向永遠往下:UI 不直接碰 P/Invoke,經由 `Rime` / `UserDbModel`;`Rime` 不認得 UI。`Dialogs.cs` 是唯一引用 MsBox.Avalonia 的檔,把第三方對話框依賴局部化。

## 程式碼結構

| 檔案 | 角色 |
|---|---|
| [Program.cs](tools/RimeUserDbEditor.Avalonia/Program.cs) | 純 Avalonia bootstrap (`StartWithClassicDesktopLifetime`);finally 區塊呼叫 `Rime.Shutdown()` |
| [App.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/App.axaml.cs) | 標準 Avalonia application —— Fluent theme + DataGrid theme |
| [MainWindow.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/MainWindow.axaml.cs) | Picker 迴圈、dict combo、搜尋/過濾、CRUD 按鈕、save/rebuild/reload、狀態列。搜尋、「顯示已刪除」、「不在 essay.txt」、「長度 ≥ N」四個過濾條件是對既有 rows 做 O(1) `DataGridCollectionView.Refresh()`;只有 load/add/edit/delete/rebuild 才會從 `_model.Entries` 重建 `_rows`。Enter / Delete 鍵在 DataGrid 上對應編輯 / 刪除,並把焦點順移到下一列 (Excel 慣例)。 |
| [EntryEditWindow.axaml(.cs)](tools/RimeUserDbEditor.Avalonia/EntryEditWindow.axaml.cs) | 五欄位的 modal (Text/Code/Commits/Dee/Tick) |
| [Dialogs.cs](tools/RimeUserDbEditor.Avalonia/Dialogs.cs) | `Window` 的擴充方法 (`Confirm` / `ShowInfo` / `ShowWarning` / `ShowError` / `ExplainOkCancel`);MsBox.Avalonia 依賴集中於此 |
| [AppConfig.cs](tools/RimeUserDbEditor.Avalonia/AppConfig.cs) | exe 同層的 JSON 讀寫 |
| [NativeLoader.cs](tools/RimeUserDbEditor.Avalonia/NativeLoader.cs) | 用 `NativeLibrary.SetDllImportResolver` 把 `[LibraryImport("rime")]` 在每個平台上都導到使用者選的 `RimeLib` 檔 |
| [Rime.cs](tools/RimeUserDbEditor.Avalonia/Rime.cs) | P/Invoke 層:`rime_get_api()` + Sequential-layout 綁 8 個 RimeApi 項目和 5 個 RimeLeversApi 項目 (`backup_user_dict`、`restore_user_dict`、`user_dict_iterator_*`) |
| [RimePaths.cs](tools/RimeUserDbEditor.Avalonia/RimePaths.cs) | `installation.yaml` 讀取 (把 `distribution_*` 透過 RimeTraits 原樣回傳,避免 librime churn 那份 yaml) + `SnapshotFileName` 集中 `<dict>.userdb.txt` 檔名慣例 |
| [RimeIpc.cs](tools/RimeUserDbEditor.Avalonia/RimeIpc.cs) | Windows-only named-pipe client,送 `WEASEL_IPC_START_MAINTENANCE` / `_END_MAINTENANCE` |
| [WeaselServer.cs](tools/RimeUserDbEditor.Avalonia/WeaselServer.cs) | Windows-only 行程控制 (`IsRunning` / `TryQuit` / `TryStart`);Linux/macOS 上 no-op |
| [UserDbModel.cs](tools/RimeUserDbEditor.Avalonia/UserDbModel.cs) | Backup-format 的 TSV parser/writer (`code \t phrase \t c=N d=F t=T`);`SaveToFile` 可選 predicate (Rebuild 用來排除 tombstone 而不需 mutate model) |

## 關鍵流程

- **啟動** — `MainWindow_Opened` → `EnsureConfigAsync` (路徑驗證迴圈;缺檔就彈 `ExplainOkCancel` + picker) → `NativeLoader.SetLibraryPath` → `Rime.Setup` (跑 `start_maintenance(0)` + `join_maintenance_thread` 確保 sync_dir 可查) → 若 WeaselServer 在跑就 `RimeIpc.StartMaintenance`,IPC 沒回應再退到 `WeaselServer.TryQuit`。
- **讀 Rime 詞典** — `BackupUserDict` 寫快照 → `FindExistingSnapshot` 在 sync_dir 下找 `<dict>.userdb.txt` → `UserDbModel.LoadFromFile`。`BackupUserDict` 路線才會完整保留 `c` / `d` / `t`;`export_user_dict` 會丟失 `d` / `t`,所以這裡不用。
- **寫回 Rime** — `UserDbModel.SaveToFile(tmp)` → `Rime.RestoreUserDictFromFile(tmp)`。注意這是 `UserDbMerger` 合併、不是取代:檔案裡沒列出的詞條會留在 DB 中。刪除靠 `c = min(-1, -c)` 鏡像 `user_dictionary.cc:442` 的對稱翻負規則,撐得過後續的合併。
- **清空重建** — 強制 `WeaselServer.TryQuit` 完全釋放 leveldb lock → `Directory.Move` (帶 file-handle 延遲釋放退讓重試) 把舊 DB 搬到 `.bak-<timestamp>` → `SaveToFile(tmp, e => e.Commits >= 0)` 用 predicate 過濾 tombstone (不 mutate `_model.Entries`,Restore 失敗使用者可重試) → `Restore` → 成功清備份,失敗 rollback。**副作用**:Restore 對空 DB 跑時,`UserDbMerger` 把每個 entry 的 `tick` 統一成 snapshot 的 `/tick` metadata、`dee` 衰減到該 tick — 等同做了一次全表 tick normalization。純 Save 沒有此效果(只動 snapshot 列到的 entry,沒列的 entry 留在 DB 不變)。

## 跨平台行為

Windows 特有的 hook 都用 `OperatingSystem.IsWindows()` runtime guard,同一份
binary 全平台跑;Windows 專屬路徑在 Linux/macOS 上直接 no-op:

| 項目 | Windows | Linux / macOS |
|---|---|---|
| 偵測 IME server 是否在跑 | `Process.GetProcessesByName("WeaselServer")` | 一律回 "not running" |
| 透過 IPC 暫停 IME | `\\.\pipe\<user>\WeaselNamedPipe` → `WEASEL_IPC_START_MAINTENANCE` | 跳過 (ibus/fcitx5 沒對等機制) |
| `WeaselServer.exe /q` fallback | 有 | 跳過 |
| Native lib 載入名稱 | `rime.dll` | `librime.so` / `librime.dylib` |
| `RimeTraits.distribution_*` | 從 `<user_dir>/installation.yaml` 讀回填,讀不到才退到 `("Rime", "RimeUserDbEditor", "0.0.0")` | 邏輯與程式路徑完全相同 |

在 Linux/macOS 上,**ibus-rime / fcitx5-rime 要自己關掉**才能跑這個工具。
`ibus exit` / `fcitx5 -r` 不夠 —— IME daemon 對每個 userdb 都掛著 leveldb
exclusive lock,只有真正的行程結束才會釋放。daemon 還活著時編輯器的 Load/Save
會丟 leveldb lock 錯誤,直接拒絕。

## 編譯 self-contained binary

```powershell
dotnet publish -c Release -r [runtime] --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

產出單一執行檔在 `bin/Release/net10.0/[runtime]/publish/RimeUserDbEditor`。

x64 / ARM64 / x86 都支援 —— `Rime.cs` 用 `[StructLayout(LayoutKind.Sequential)]`
宣告 librime 的結構,offset 由 runtime 根據 host 的 `IntPtr.Size` 算出。發佈
RID 要跟 librime binary 的位元數對齊 (32-bit dotnet process 無法載 64-bit
librime,反之亦然)。

## librime ABI 變動時怎麼處理

`Rime.cs` 每個 librime 結構都宣告成 `[StructLayout(LayoutKind.Sequential)]`,
取到我們呼叫的最後一個 fn 為止的 prefix (RimeApi 取到 slot 55,RimeLeversApi
取到 slot 29)。Offsets 全由 runtime 算 —— 沒有要維護的數字常數。RIME_STRUCT
是 append-only,升級理論上透明;只有 slot reorder/移除 (位置在我們的 cutoff
之前) 才會需要動欄位順序。下面這兩支 dumper 會吐出新的 C# Sequential 宣告
供貼回比對:

### Windows (PDB)

```powershell
dotnet run tools\findOffset\dump-rime-structs.cs <llvm-pdbutil.exe> <pdb-dir>
```

[dump-rime-structs.cs](tools/findOffset/dump-rime-structs.cs) 是 .NET 10
file-based program。`<pdb-dir>` 需含 `rime.pdb`;若同時含 `WeaselServer.pdb`,
script 會額外輸出 [RimeIpc.cs](tools/RimeUserDbEditor.Avalonia/RimeIpc.cs)
要用的 `WEASEL_IPC_COMMAND` 常數和 `weasel::PipeMessage` 結構。

### Linux / macOS (DWARF)

```bash
tools/findOffset/dump-rime-structs-gdb.sh /path/to/librime.so.debug   # 需要 gdb 12+
```

對 stand-alone 的 `<buildid>.debug` split-debug 檔或未 strip 的 `librime.so`
都能跑。

若不放心 dumper 結果,
[tools/findOffset/rime_api_probe.c](tools/findOffset/rime_api_probe.c) 是支
獨立的小 C 程式,用 `dlopen` 載入 librime 並直接 dump 原始 RimeApi 指標表
—— 與任何 debug-info parser 無關。
