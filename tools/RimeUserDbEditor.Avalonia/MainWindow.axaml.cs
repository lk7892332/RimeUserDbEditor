using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RimeUserDbEditor;

public sealed partial class MainWindow : Window
{
    // 共用資料目錄的父層,在 Windows 上等同 Weasel 安裝目錄 (拿來找
    // WeaselServer.exe);非 Windows 上找不到就 no-op。
    private string _weaselInstallDir = string.Empty;
    // shared_data_dir 本身,拿來找 essay.txt 等預設詞庫檔。
    private string _sharedDataDir = string.Empty;
    // essay.txt 的 phrase 集合,給「不在 essay.txt」filter 用;null = 尚未載入。
    private HashSet<string>? _essayPhrases;
    private readonly UserDbModel _model = new();
    // _rows 與 _model.Entries 一對一鏡射;_view 在外面包一層 Filter predicate,
    // 所以 search-as-you-type 跟 show-deleted 切換不會觸發 rebuild。排序狀態
    // 留在 _view 上,_rows 重建時不會掉。
    private readonly ObservableCollection<RowVm> _rows = new();
    private DataGridCollectionView? _view;
    private FilterCriteria _filter = FilterCriteria.None;
    private bool _dirty;
    private ServerPauseMode _pauseMode = ServerPauseMode.None;
    private bool _bootstrapped;
    // DictCombo.SelectedItem 在 discard-revert 路徑上會被我們手動回寫,
    // 那次再進來的 handler 一律忽略 —— 比靠 `newSel == _model.SourceDict` 推導
    // 安全得多。
    private bool _suppressDictChange;

    private sealed class RowVm : INotifyPropertyChanged
    {
        public RowVm(int modelIndex, UserDbEntry entry)
        {
            ModelIndex = modelIndex;
            Entry = entry;
        }

        public int ModelIndex { get; }
        public UserDbEntry Entry { get; private set; }
        public string Text    => Entry.Text;
        public string Code    => Entry.Code;
        public int    Commits => Entry.Commits;
        // Dee 顯示格式化字串;DeeValue 是 DataGrid 排序用的數值。
        public string Dee      => Entry.Dee.ToString("G", CultureInfo.InvariantCulture);
        public double DeeValue => Entry.Dee;
        public ulong  Tick     => Entry.Tick;

        public event PropertyChangedEventHandler? PropertyChanged;

        // 編輯後就地換 Entry 並通知所有欄位刷新,省掉 RefreshList 整表重建。
        // ModelIndex 不變,_rows 的 index↔model 不變式照舊成立。
        private static readonly string[] Cols =
            [nameof(Text), nameof(Code), nameof(Commits),
             nameof(Dee), nameof(DeeValue), nameof(Tick)];
        public void UpdateEntry(UserDbEntry entry)
        {
            Entry = entry;
            foreach (var name in Cols)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // RefreshView 前先把 filter 控制項的狀態 snapshot 成這個,predicate 對每列
    // 讀它而不是反覆戳控制項 —— 10k+ 列時省掉每列數次 IsChecked / Value / Text
    // 的存取,也讓判斷邏輯脫離 View 變得可測。
    private sealed record FilterCriteria(
        string Search, bool ShowDeleted, bool NotInEssay,
        HashSet<string>? EssayPhrases, int MinLength)
    {
        // view 第一次 filter 之前的佔位值;ShowDeleted=true 故不會誤藏任何列。
        public static readonly FilterCriteria None =
            new(string.Empty, true, false, null, 0);

        public bool Passes(UserDbEntry e)
        {
            if (!ShowDeleted && e.Commits < 0) return false;
            if (NotInEssay && EssayPhrases != null && EssayPhrases.Contains(e.Text)) return false;
            if (MinLength > 0 && e.Text.Length < MinLength) return false;
            if (Search.Length == 0) return true;
            return e.Text.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || e.Code.Contains(Search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private enum ServerPauseMode { None, IpcMaintenance, Quit }

    // LoadFile / SaveAs 共用的檔案過濾器 —— backup-format 的 userdb dump
    // 慣例上是 .txt,另外留個 "all files" 給特殊情況用。
    private static readonly FilePickerFileType[] TxtFileFilter =
    [
        new("文字檔") { Patterns = ["*.txt"] },
        FilePickerFileTypes.All,
    ];

    public MainWindow()
    {
        InitializeComponent();
        Opened  += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed  += MainWindow_Closed;
        // DataGrid 的 Enter→往下移是 class handler,在 bubble 階段比一般 instance
        // handler 早跑;我們要趕在它前面 set Handled,所以掛在 tunnel。Delete 同理
        // 放一起,免得兩條 handler 路徑混淆。
        List.AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel);
    }

    // -- 啟動 / 關閉 ---------------------------------------------------------

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_bootstrapped) return;
        _bootstrapped = true;

        string? resolved = await EnsureConfigAsync();
        if (resolved == null) { Close(); return; }
        _sharedDataDir    = resolved;
        _weaselInstallDir = Path.GetDirectoryName(resolved) ?? string.Empty;

        if (WeaselServer.IsRunning())
        {
            _pauseMode = await TryPauseServerAsync();
            if (_pauseMode == ServerPauseMode.None)
            {
                await this.ShowWarning(
                    "WeaselServer 正在執行而且無法暫停。\n"
                    + "對 Rime 的載入／儲存會因為 leveldb 鎖而失敗。");
            }
        }

        PopulateDictCombo();
        UpdateButtons();
        UpdateStatus();
    }

    private async Task<ServerPauseMode> TryPauseServerAsync()
    {
        if (RimeIpc.StartMaintenance())
            return ServerPauseMode.IpcMaintenance;

        var yes = await this.Confirm(
            "WeaselServer 正在執行，但維護模式的 IPC 沒有回應。\n\n"
            + "是否在本次工作階段中關閉 WeaselServer？關閉編輯器時會自動重新啟動。");
        if (!yes) return ServerPauseMode.None;
        return WeaselServer.TryQuit(_weaselInstallDir) ? ServerPauseMode.Quit : ServerPauseMode.None;
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        // IsProgrammatic = true 是我們自己呼叫 Close() 觸發的重入,直接放行;
        // 使用者按 X 才會走確認流程。
        if (!_dirty || e.IsProgrammatic) return;
        e.Cancel = true;
        if (await ConfirmDiscard()) Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        switch (_pauseMode)
        {
            case ServerPauseMode.IpcMaintenance: RimeIpc.EndMaintenance(); break;
            case ServerPauseMode.Quit:           WeaselServer.TryStart(_weaselInstallDir); break;
        }
    }

    // -- 動作 ----------------------------------------------------------------

    private void PopulateDictCombo()
    {
        DictCombo.Items.Clear();
        try
        {
            foreach (var d in Rime.ListUserDicts())
                DictCombo.Items.Add(d);
            if (DictCombo.Items.Count > 0) DictCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _ = this.ShowError("無法列出使用者詞典：\n" + ex.Message);
        }
    }

    private async void LoadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DictCombo.SelectedItem is not string dictName)
        {
            await this.ShowInfo("請先選擇一個使用者詞典。");
            return;
        }
        if (!await ConfirmDiscard()) return;
        await LoadFromRimeAsync(dictName);
    }

    /// <summary>透過 librime 重讀 <paramref name="dictName"/>,替換掉記憶體中的
    /// model;不會確認,呼叫端要自己擋未存的編輯。</summary>
    private async Task LoadFromRimeAsync(string dictName)
    {
        try
        {
            // 只有 BackupUserDict 這條路會完整保留 c/d/t;
            // export 路線會丟掉 d/t。見 docs/userdb_cdt_analysis.md。
            if (!Rime.BackupUserDict(dictName))
            {
                await this.ShowError("Rime backup_user_dict 失敗。");
                return;
            }
            string? snapshotPath = Rime.FindExistingSnapshot(dictName);
            if (snapshotPath == null)
            {
                await this.ShowError(
                    "備份已執行，但找不到快照檔。\n"
                    + "請檢查 installation.yaml 的 sync_dir 設定。");
                return;
            }
            _model.LoadFromFile(snapshotPath);
            _model.SourceDict = dictName;
            _dirty = false;
            RefreshList();
            UpdateButtons();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            await this.ShowError("載入失敗：\n" + ex.Message);
        }
    }

    private async void LoadFileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "開啟使用者詞典文字檔",
            AllowMultiple = false,
            FileTypeFilter = TxtFileFilter,
        });
        if (files.Count == 0) return;
        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _model.LoadFromFile(path);
            // Clear() 把 SourceDict 設成 null,這裡重新綁到 combo (目標詞典)。
            _model.SourceDict = DictCombo.SelectedItem as string;
            _dirty = false;
            RefreshList();
            UpdateButtons();
        }
        catch (Exception ex)
        {
            await this.ShowError("開啟失敗：\n" + ex.Message);
        }
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_model.SourceDict)) { await SaveAsAsync(); return; }

        if (_pauseMode == ServerPauseMode.None && WeaselServer.IsRunning())
        {
            await this.ShowError(
                "WeaselServer 正在執行並佔用使用者詞典。\n"
                + "請重新啟動編輯器，讓它先暫停 server。");
            return;
        }

        // 把目的詞典寫清楚 —— 不然「開檔 + 切 combo」可能默默存到錯的詞典。
        bool ok = await this.Confirm(
            $"將 {_model.Entries.Count} 筆詞條合併進 Rime 使用者詞典「{_model.SourceDict}」？\n\n"
            + "這是 UserDbMerger 合併而非取代——目前清單裡沒有的詞條會留在 DB。\n"
            + "刪除（commits < 0）依照 Rime 的對稱翻負規則。",
            title: "儲存至 Rime");
        if (!ok) return;

        try
        {
            string tmp = SnapshotTmpPath(_model.SourceDict);
            _model.SaveToFile(tmp);
            bool restored = Rime.RestoreUserDictFromFile(tmp);
            // restore 已讀完 tmp,不論成敗都刪掉,別在 temp 留下含使用者資料的殘檔。
            TryDeleteFile(tmp);
            if (!restored)
            {
                await this.ShowError("Rime restore 失敗。");
                return;
            }
            _dirty = false;
            UpdateButtons();
            await this.ShowInfo(
                $"已儲存至 Rime 使用者詞典（{_model.Entries.Count} 筆詞條）。\n\n"
                + "注意：Rime restore 是合併（UserDbMerger）；\n"
                + "刪除（commits < 0）會生效，但檔案裡沒有的詞條\n"
                + "仍會留在 DB 中。");
            // UserDbMerger 不會動 DB 上沒列出的詞條,重新讀回以對齊現況。
            await LoadFromRimeAsync(_model.SourceDict);
        }
        catch (Exception ex)
        {
            await this.ShowError("儲存失敗：\n" + ex.Message);
        }
    }

    private async void SaveAsButton_Click(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存新檔",
            SuggestedFileName = string.IsNullOrEmpty(_model.SourceDict)
                ? "userdb_export.txt"
                : RimePaths.SnapshotFileName(_model.SourceDict),
            DefaultExtension = "txt",
            FileTypeChoices = TxtFileFilter,
        });
        if (file == null) return;
        string? path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _model.SaveToFile(path);
            await this.ShowInfo("文字檔已儲存。");
        }
        catch (Exception ex)
        {
            await this.ShowError("儲存失敗：\n" + ex.Message);
        }
    }

    private async void RebuildButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_model.SourceDict))
        {
            await this.ShowInfo("請先從 Rime 載入一個詞典。");
            return;
        }

        bool ok = await this.Confirm(
            $"目標詞典：「{_model.SourceDict}」\n\n"
            + "這將會「完全砍掉」這個 Rime 詞典的資料庫，並以目前清單上的資料"
            + "（排除已刪除的詞）建立全新的乾淨資料庫。\n\n"
            + "幽靈紀錄與長期累積的無用資訊都將被徹底抹除。\n"
            + "你確定要執行這項操作嗎？",
            title: "清空並重建");
        if (!ok) return;

        // 物理刪除目錄需要 leveldb lock 完全釋放 —— 維護模式還不夠,
        // server 必須真的退出。
        if (WeaselServer.IsRunning())
        {
            if (_pauseMode == ServerPauseMode.IpcMaintenance)
                RimeIpc.EndMaintenance();
            if (!WeaselServer.TryQuit(_weaselInstallDir))
            {
                await this.ShowError("無法關閉 WeaselServer，資料庫被鎖定無法清除。");
                return;
            }
            _pauseMode = ServerPauseMode.Quit;
            UpdateStatus();
        }

        string dbFolder  = Path.Combine(Rime.GetUserDir(), $"{_model.SourceDict}.userdb");
        string bakFolder = $"{dbFolder}.bak-{DateTime.Now:yyyyMMddHHmmssfff}";
        string tmp       = SnapshotTmpPath(_model.SourceDict);
        bool movedAside  = false;

        try
        {
            // 先把舊資料庫搬到旁邊;Restore 失敗才有東西可以回滾。Directory.Move
            // 比 Recursive Delete 快(只動目錄項),但仍可能撞到 OS 還沒釋放的
            // leveldb 檔案 handle —— 短延遲後重試。
            if (Directory.Exists(dbFolder))
            {
                var (moved, err) = await TryMoveDirectoryAsync(dbFolder, bakFolder);
                if (!moved)
                {
                    await this.ShowError(
                        $"無法搬開舊資料庫:\n{err}\n\n"
                        + "資料庫沒有被動到,可以放心重試;\n"
                        + "若反覆失敗,請確認沒有其他程式佔用該目錄。");
                    return;
                }
                movedAside = true;
            }

            // 用 filter 寫出「不含 tombstone」的快照,而不是先 mutate model ——
            // Restore 失敗時 model 還是原樣,使用者可以直接重試。
            _model.SaveToFile(tmp, e => e.Commits >= 0);

            if (!Rime.RestoreUserDictFromFile(tmp))
            {
                await TryRollbackRebuildAsync(dbFolder, bakFolder, movedAside, tmp);
                return;
            }

            // 成功 —— 清掉備份;清不掉也只是留個目錄,不影響資料庫。
            if (movedAside)
            {
                try { Directory.Delete(bakFolder, true); } catch { /* ignore */ }
            }
            // 成功才刪 tmp 快照;失敗路徑的 rollback 會把它留著當復原依據。
            TryDeleteFile(tmp);

            _dirty = false;
            await this.ShowInfo("資料庫已被徹底清除並重建完成！", "完成");
            // 重新讀回 —— librime 可能把 tick / d 規一化過了。LoadFromRimeAsync 內部
            // 已 RefreshList + UpdateButtons,所以這裡不先做,省一次整表重建。
            await LoadFromRimeAsync(_model.SourceDict);
        }
        catch (Exception ex)
        {
            // 也可能在 SaveToFile / RestoreUserDictFromFile 之外炸,一律嘗試回滾。
            await TryRollbackRebuildAsync(dbFolder, bakFolder, movedAside, tmp);
            await this.ShowError("重建失敗：\n" + ex.Message);
        }
    }

    /// <summary>把搬到 <paramref name="bakFolder"/> 的舊 DB 還原回 <paramref name="dbFolder"/>。
    /// Restore 可能已經寫了部分檔案進 dbFolder,要先清掉再 Move 回來。</summary>
    private async Task TryRollbackRebuildAsync(string dbFolder, string bakFolder, bool movedAside, string tmpSnapshot)
    {
        if (!movedAside)
        {
            await this.ShowError(
                "Rime 無法重建資料庫。\n"
                + $"當下的 TSV 快照保留在:\n{tmpSnapshot}");
            return;
        }

        // Restore 失敗的副作用可能是建出半成品 dbFolder;先抹掉,Move 才不會撞名。
        if (Directory.Exists(dbFolder))
        {
            try { Directory.Delete(dbFolder, true); }
            catch (Exception ex)
            {
                await this.ShowError(
                    $"Rime 無法重建資料庫,而且回滾前無法清掉半成品:\n{ex.Message}\n\n"
                    + $"原始資料庫保留在:\n{bakFolder}\n"
                    + $"TSV 快照保留在:\n{tmpSnapshot}");
                return;
            }
        }

        try
        {
            Directory.Move(bakFolder, dbFolder);
            await this.ShowError(
                "Rime 無法重建資料庫。原始資料庫已還原。\n"
                + $"TSV 快照保留在:\n{tmpSnapshot}");
        }
        catch (Exception ex)
        {
            await this.ShowError(
                $"Rime 無法重建資料庫,而且回滾也失敗:\n{ex.Message}\n\n"
                + $"原始資料庫保留在:\n{bakFolder}\n"
                + $"TSV 快照保留在:\n{tmpSnapshot}");
        }
    }

    /// <summary>Directory.Move 加上對 file-handle 延遲釋放的退讓重試。
    /// WeaselServer 已停,但 leveldb LOCK / *.ldb 可能還掛在 OS 的延遲關閉佇列上
    /// (AV 掃描尤其常見)。總等待 ≈ 3.35 秒。</summary>
    private static async Task<(bool ok, string? error)> TryMoveDirectoryAsync(string src, string dst)
    {
        int[] delays = [0, 100, 250, 500, 1000, 1500];
        Exception? last = null;
        foreach (int d in delays)
        {
            if (d > 0) await Task.Delay(d);
            try
            {
                Directory.Move(src, dst);
                return (true, null);
            }
            catch (IOException ex)                 { last = ex; }
            catch (UnauthorizedAccessException ex) { last = ex; }
        }
        return (false, last?.Message ?? "unknown error");
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new EntryEditWindow(new UserDbEntry());
        bool ok = await dlg.ShowDialog<bool>(this);
        if (ok)
        {
            _model.Entries.Add(dlg.Entry);
            // 只 append 一筆 —— 加一個 RowVm 即可,不必 RefreshList 整表重建。
            // 新項 ModelIndex == 舊 _rows.Count 故不變式維持;DataGridCollectionView
            // 會自動對新項跑 filter。_view 還沒建 (尚未載入任何詞典) 則退回 RefreshList
            // 走首次建立。
            if (_view == null)
                RefreshList();
            else
            {
                _rows.Add(new RowVm(_rows.Count, dlg.Entry));
                UpdateStatus();
            }
            _dirty = true;
            UpdateButtons();
            // 新增完直接落在新詞條上,方便連續編輯。
            SelectModelIndex(_model.Entries.Count - 1);
        }
        else
        {
            List.Focus();
        }
    }

    private async void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not RowVm sel) return;
        int idx = sel.ModelIndex;
        var dlg = new EntryEditWindow(_model.Entries[idx]);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (ok)
        {
            _model.Entries[idx] = dlg.Entry;
            // 就地換 Entry + 通知,而非 RefreshList 整表重建。改完可能命中/離開
            // filter (例如 c 改成負值),所以仍要 RefreshView 重跑 predicate。
            sel.UpdateEntry(dlg.Entry);
            _dirty = true;
            // 先記住它在 view 裡的位置:RefreshView 後若這筆離開了 filter,把焦點
            // 順移到原位置遞補上來的那列 —— 跟刪除一致,避免選到隱藏列。
            int viewIndex = _view?.IndexOf(sel) ?? -1;
            RefreshView();
            UpdateButtons();
            if (_filter.Passes(sel.Entry))
                SelectModelIndex(idx);
            else
                SelectAtViewIndex(viewIndex);
            return;
        }
        // 取消:那筆沒動,還原選取到它。ModelIndex 不變所以能直接找。
        SelectModelIndex(idx);
    }

    private async void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        var indices = List.SelectedItems.OfType<RowVm>().Select(r => r.ModelIndex).ToList();
        if (indices.Count == 0) return;

        // 抓「最上面那筆已選列」在 view 裡的位置,用來決定刪完之後焦點落哪。
        // 隱藏已刪除時,後面的列會往上遞補到這個位置 —— Excel / 檔案管理員的慣例。
        int focusViewIndex = -1;
        if (_view != null)
        {
            foreach (var sel in List.SelectedItems)
            {
                int i = _view.IndexOf(sel);
                if (i >= 0 && (focusViewIndex < 0 || i < focusViewIndex))
                    focusViewIndex = i;
            }
        }

        bool ok = await this.Confirm(
            $"刪除選取的 {indices.Count} 筆詞條？\n\n"
            + "Commits 會被設成 min(-1, -原值)，比照 Rime 內建 Ctrl+Delete 的\n"
            + "行為（對稱翻負、保留絕對值）。這樣才能撐過後續的 UserDbMerger\n"
            + "合併；單純設成 -1 會被任何 |c| > 1 的 DB 紀錄蓋掉。");
        if (!ok) { List.Focus(); return; }

        foreach (int i in indices)
        {
            // 對齊 UserDictionary::UpdateEntry:
            //   v.commits = std::min(-1, -v.commits);
            var entry = _model.Entries[i];
            entry.Commits = Math.Min(-1, -entry.Commits);
            // 刪除只是把 c 翻負,不是結構性移除 —— 就地通知對應列刷新 (顯示已刪除
            // 時 Commits 欄要更新),而非 RefreshList 整表重建。重建的 Clear+Add
            // reset 會讓 DataGrid 在後續 layout 把列表捲到最下面,defer 也壓不住;
            // 走 RefreshView 這條輕路徑就沒這問題。ModelIndex 不變式成立故直接索引。
            _rows[i].UpdateEntry(entry);
        }
        _dirty = true;
        RefreshView();
        UpdateButtons();
        SelectAtViewIndex(focusViewIndex);
    }

    /// <summary>選取 <paramref name="row"/>,捲到可見並 focus。<c>null</c> 就只 focus。</summary>
    private void SelectRow(RowVm? row)
    {
        if (row != null)
        {
            List.SelectedItem = row;
            ScrollIntoViewDeferred(row);
        }
        List.Focus();
    }

    /// <summary>把 <paramref name="row"/> 捲進可見區,但 post 到 layout pass 之後。
    /// Refresh() / _rows 重建會觸發 collection reset,DataGrid 隨後的 layout 會
    /// 自行把列表捲到最下面 —— 同步呼叫 ScrollIntoView 會被那趟覆寫,所以 defer。</summary>
    private void ScrollIntoViewDeferred(RowVm row)
        => Dispatcher.UIThread.Post(() => List.ScrollIntoView(row, null),
                                    DispatcherPriority.Background);

    /// <summary>選取 ModelIndex 對應的列。RefreshList 後 <c>_rows[i].ModelIndex == i</c>
    /// 的不變式成立,所以直接索引。</summary>
    private void SelectModelIndex(int modelIndex)
        => SelectRow((uint)modelIndex < (uint)_rows.Count ? _rows[modelIndex] : null);

    /// <summary>選取 view 上第 viewIndex 列 (clamp 到合法範圍)。view 為空就只 focus。
    /// 給刪除完「焦點順移到下一列」這個 UX 用。</summary>
    private void SelectAtViewIndex(int viewIndex)
    {
        if (_view == null || _view.Count == 0) { List.Focus(); return; }
        int idx = Math.Clamp(viewIndex, 0, _view.Count - 1);
        SelectRow(_view.GetItemAt(idx) as RowVm);
    }

    // -- 列表與排序 ----------------------------------------------------------
    // 排序交給 DataGridCollectionView,XAML 上用 SortMemberPath 設好欄位;
    // _view 在 _rows 重建之間保留排序狀態。

    private async void DictCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // discard-revert 路徑上我們會手動把 SelectedItem 寫回上一個值,那次重入
        // 一律忽略。
        if (_suppressDictChange) return;

        // combo 選項是「目標詞典」的唯一來源 —— 一變就重讀 (包含初始
        // SelectedIndex=0 那次)。
        string? newSel = DictCombo.SelectedItem as string;

        if (string.IsNullOrEmpty(newSel))
        {
            _model.SourceDict = null;
            UpdateButtons();
            UpdateStatus();
            return;
        }

        // 同一個詞典 (例如 Avalonia 自己重發事件) —— 只更新按鈕 enabled 狀態。
        if (newSel == _model.SourceDict)
        {
            UpdateButtons();
            UpdateStatus();
            return;
        }

        // 擋未存的編輯;使用者取消時把 combo 還原。回寫包在 suppress flag 裡,
        // 不會遞迴重入。
        if (_dirty && !await ConfirmDiscard())
        {
            string? prev = e.RemovedItems.Count > 0 ? e.RemovedItems[0] as string : null;
            if (prev != null)
            {
                _suppressDictChange = true;
                try { DictCombo.SelectedItem = prev; }
                finally { _suppressDictChange = false; }
            }
            return;
        }

        await LoadFromRimeAsync(newSel);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshView();

    private void ShowDeleted_Changed(object? sender, RoutedEventArgs e) => RefreshView();

    private void MinLength_Changed(object? sender, RoutedEventArgs e) => RefreshView();

    private void MinLengthValue_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        // checkbox 沒勾時,SnapshotFilter 會把 MinLength 收成 0 (不過濾),改數字
        // 不影響結果 —— 跳過刷新,省一次整個 view 的 re-filter。
        if (MinLengthBox.IsChecked == true) RefreshView();
    }

    /// <summary>Lazy-load essay.txt 後刷新 view;載入失敗就把 checkbox 退回去
    /// (退回會重入這個 handler,但 IsChecked == false 不會再嘗試載入,只多跑一次
    /// RefreshView,無副作用)。</summary>
    private async void NotInEssay_Changed(object? sender, RoutedEventArgs e)
    {
        if (NotInEssayBox.IsChecked == true && !await EnsureEssayLoadedAsync())
        {
            NotInEssayBox.IsChecked = false;
            return;
        }
        RefreshView();
    }

    private async Task<bool> EnsureEssayLoadedAsync()
    {
        if (_essayPhrases != null) return true;

        // librime 的 FallbackResourceResolver (resource.cc:34) 是 user_data_dir
        // 為主、shared_data_dir 為 fallback (service.cc:169-170);使用者放在
        // user_dir 的客製 essay.txt 會蓋掉發行版附的。我們鏡像同一順序。
        string userPath   = Path.Combine(Rime.GetUserDir(), "essay.txt");
        string sharedPath = Path.Combine(_sharedDataDir,    "essay.txt");
        string path;
        if      (File.Exists(userPath))   path = userPath;
        else if (File.Exists(sharedPath)) path = sharedPath;
        else
        {
            await this.ShowError(
                $"找不到 essay.txt,以下路徑皆不存在:\n"
                + $"  {userPath}\n"
                + $"  {sharedPath}\n\n"
                + "請確認設定的目錄正確,或這個發行版沒有附 essay 預設詞庫。");
            return false;
        }
        try
        {
            // essay.txt 可能上看 10 MB / 數十萬行,丟去 worker thread 不卡 UI。
            // 格式參考 librime/src/rime/dict/preset_vocabulary.cc:
            //   phrase<TAB>weight,加上 # 開頭的註解 / metadata。
            _essayPhrases = await Task.Run(() =>
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int tab = line.IndexOf('\t');
                    string phrase = tab < 0 ? line : line[..tab];
                    if (phrase.Length > 0) set.Add(phrase);
                }
                return set;
            });
            return true;
        }
        catch (Exception ex)
        {
            await this.ShowError("讀取 essay.txt 失敗:\n" + ex.Message);
            return false;
        }
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateButtons();

    private void List_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (List.SelectedItem is RowVm) EditButton_Click(sender, e);
    }

    /// <summary>Enter = 編輯選取列;Delete = 刪除選取列。沒有 modifier 時才觸發,
    /// 不擋 Ctrl/Shift 組合鍵以免跟未來的快捷鍵衝突。</summary>
    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                if (List.SelectedItem is RowVm)
                {
                    EditButton_Click(sender, e);
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (List.SelectedItems.Count > 0)
                {
                    DeleteButton_Click(sender, e);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>從 <c>_model.Entries</c> 重建 <see cref="_rows"/>。只在 model
    /// 本身變動時呼叫;過濾條件切換改用 <see cref="RefreshView"/>。</summary>
    private void RefreshList()
    {
        if (_view == null)
        {
            // predicate 只讀 snapshot,先補一次 (control 預設值);之後每次
            // RefreshView 會重新 snapshot。
            _filter = SnapshotFilter();
            _view = new DataGridCollectionView(_rows)
            {
                Filter = obj => obj is RowVm row && _filter.Passes(row.Entry),
            };
            List.ItemsSource = _view;
        }

        _rows.Clear();
        for (int i = 0; i < _model.Entries.Count; i++)
            _rows.Add(new RowVm(i, _model.Entries[i]));
        UpdateStatus();
    }

    /// <summary>重新 snapshot filter 狀態並重跑 predicate;不會走 model。</summary>
    private void RefreshView()
    {
        _filter = SnapshotFilter();
        // Refresh() 後 DataGrid 會把列表捲到最下面;選取列若仍通過 filter,把它
        // 捲回可見。重選路徑 (編輯/刪除) 另有 SelectRow 處理,這裡專管「只改
        // filter、不重選」的情形 (搜尋打字、切 checkbox)。
        var selected = List.SelectedItem as RowVm;
        _view?.Refresh();
        UpdateStatus();
        if (selected != null && _filter.Passes(selected.Entry))
            ScrollIntoViewDeferred(selected);
    }

    /// <summary>把當下 filter 控制項的狀態收成一個 <see cref="FilterCriteria"/>。
    /// MinLength 未勾選時收成 0 (= 不過濾);NumericUpDown 的 Minimum=1 保證勾選時 ≥1。</summary>
    private FilterCriteria SnapshotFilter() => new(
        SearchBox.Text ?? string.Empty,
        ShowDeletedBox.IsChecked == true,
        NotInEssayBox.IsChecked == true,
        _essayPhrases,
        MinLengthBox.IsChecked == true ? (int)(MinLengthValue.Value ?? 0) : 0);

    private void UpdateStatus()
    {
        var sb = new StringBuilder();
        int visible = _view?.Count ?? _rows.Count;
        sb.Append($"詞條：{visible} / {_model.Entries.Count}");
        if (_dirty) sb.Append("  *");
        if (!string.IsNullOrEmpty(_model.SourceDict)) sb.Append($"  [{_model.SourceDict}]");
        sb.Append(_pauseMode switch
        {
            ServerPauseMode.IpcMaintenance => "  (server：維護中)",
            ServerPauseMode.Quit           => "  (server：已停止)",
            _ => string.Empty,
        });
        StatusText.Text = sb.ToString();
    }

    private void UpdateButtons()
    {
        LoadButton.IsEnabled    = DictCombo.SelectedItem is string;
        SaveButton.IsEnabled    = !string.IsNullOrEmpty(_model.SourceDict);
        RebuildButton.IsEnabled = !string.IsNullOrEmpty(_model.SourceDict);
        bool hasSel = List.SelectedItems.Count > 0;
        EditButton.IsEnabled    = hasSel;
        DeleteButton.IsEnabled  = hasSel;
    }

    private async Task<bool> ConfirmDiscard()
    {
        if (!_dirty) return true;
        return await this.Confirm("有尚未儲存的改動，要丟棄嗎？");
    }

    private static string SnapshotTmpPath(string dictName)
        => Path.Combine(Path.GetTempPath(), RimePaths.SnapshotFileName(dictName));

    // 暫存快照含使用者詞庫內容,用完盡力刪掉;刪不掉 (例如被防毒掃描鎖住) 無妨。
    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    // -- 首次啟動的路徑挑選 / config 迴圈 -------------------------------------

    private async Task<string?> EnsureConfigAsync()
    {
        var config = AppConfig.Load();

        while (true)
        {
            if (!FileOrDirExists(config.SharedDataDir))
            {
                string intro = string.IsNullOrEmpty(config.SharedDataDir)
                    ? "選擇 Rime 共用資料目錄 (shared_data_dir)。\n\n"
                      + "這層裡面直接放著 *.schema.yaml 等 schema 檔。\n"
                      + "  • Windows: ...\\weasel-x.y.z\\data\\\n"
                      + "  • Linux:   /usr/share/rime-data\n"
                      + "  • macOS:   /Library/Input Methods/Squirrel.app/Contents/SharedSupport"
                    : $"設定中的共用資料目錄不存在：\n{config.SharedDataDir}\n\n請重新選擇。";
                string? picked = await PromptForFolderAsync("shared_data_dir", intro);
                if (picked == null) return null;
                config.SharedDataDir = picked;
                config.Save();
                continue;
            }

            if (!FileOrDirExists(config.UserDir))
            {
                string intro = string.IsNullOrEmpty(config.UserDir)
                    ? "選擇 Rime 使用者資料夾(user_dir)。\n\n"
                      + "通常裡面會有：\n"
                      + "  • installation.yaml\n"
                      + "  • *.userdb 子目錄(leveldb 詞典資料)\n"
                      + "  • *.custom.yaml 個人化設定"
                    : $"設定中的使用者資料夾不存在：\n{config.UserDir}\n\n請重新選擇。";
                string? picked = await PromptForFolderAsync("user_dir", intro);
                if (picked == null) return null;
                config.UserDir = picked;
                config.Save();
                continue;
            }

            if (!FileOrDirExists(config.RimeLib))
            {
                string example = OperatingSystem.IsWindows() ? "rime.dll"
                              : OperatingSystem.IsMacOS()   ? "librime.dylib"
                              :                                "librime.so / librime.so.1";
                string intro = string.IsNullOrEmpty(config.RimeLib)
                    ? $"選擇 librime 函式庫檔案(rime_lib)。\n\n本機通常叫 {example}。"
                    : $"設定中的 librime 檔案不存在：\n{config.RimeLib}\n\n請重新選擇。";
                string? picked = await PromptForLibFileAsync("rime_lib", intro);
                if (picked == null) return null;
                config.RimeLib = picked;
                config.Save();
                continue;
            }

            if (!RimePaths.HasInstallationYaml(config.UserDir))
            {
                // 沒有 installation.yaml = 這個 user_dir 從沒被 Rime 部署過。繼續會讓
                // librime 用我們的 fallback distribution_* 寫一份新的 yaml,下次
                // WeaselServer 啟動又會把它蓋回去 —— 兩邊輪流 churn。
                bool proceed = await this.Confirm(
                    $"選擇的 user_dir 沒有 installation.yaml:\n{config.UserDir}\n\n"
                    + "這個目錄看起來沒有被 Rime 部署過。繼續會讓本工具寫出一份新的\n"
                    + "installation.yaml,下次 WeaselServer 啟動時會把它蓋回去 ——\n"
                    + "兩邊輪流改寫,每次切換都重新部署一次。\n\n"
                    + "建議:取消後改選 Rime 平常用的 user_dir,或先讓 WeaselServer/\n"
                    + "WeaselDeployer 部署過再回來。\n\n仍要繼續嗎?",
                    title: "缺少 installation.yaml");
                if (!proceed)
                {
                    config.UserDir = string.Empty;
                    config.Save();
                    continue;
                }
            }

            try
            {
                NativeLoader.SetLibraryPath(config.RimeLib);
                Rime.Setup(config.SharedDataDir, config.UserDir);
                return config.SharedDataDir;
            }
            catch (Exception ex)
            {
                await this.ShowError(
                    "Rime 初始化失敗：\n" + ex.Message
                    + "\n\n請重新選擇三個路徑。");
                // 三個一起清空 —— librime 不會告訴我們是哪個出問題。
                config.SharedDataDir = string.Empty;
                config.UserDir       = string.Empty;
                config.RimeLib       = string.Empty;
                config.Save();
            }
        }
    }

    private async Task<string?> PromptForFolderAsync(string title, string explanation)
    {
        if (!await this.ExplainOkCancel(title, explanation)) return null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async Task<string?> PromptForLibFileAsync(string title, string explanation)
    {
        if (!await this.ExplainOkCancel(title, explanation)) return null;

        var typeFilter = OperatingSystem.IsWindows()
            ? new FilePickerFileType("Windows DLL")   { Patterns = ["*.dll"] }
            : OperatingSystem.IsMacOS()
                ? new FilePickerFileType("macOS dylib") { Patterns = ["*.dylib"] }
                : new FilePickerFileType("Linux .so")  { Patterns = ["*.so", "*.so.*"] };

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [typeFilter, FilePickerFileTypes.All],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
    
    private static bool FileOrDirExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

}
