using System.Runtime.InteropServices;

namespace RimeUserDbEditor;

/// <summary>
/// librime 的 P/Invoke 包裝層,透過 <c>rime_get_api()</c> 取得函式表。
/// 每個 struct 以 <c>Sequential</c> layout 宣告到我們綁定的最後一個 fn 為止;
/// 每次讀取前先驗 <c>data_size</c>,librime 過舊就 fail loud。
/// 動態庫解析交給 <see cref="NativeLoader"/>。
/// </summary>
internal static partial class Rime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RimeTraits
    {
        public int data_size;
        public IntPtr shared_data_dir;
        public IntPtr user_data_dir;
        public IntPtr distribution_name;
        public IntPtr distribution_code_name;
        public IntPtr distribution_version;
        public IntPtr app_name;
        public IntPtr modules;
        public int min_log_level;
        public IntPtr log_dir;
        public IntPtr prebuilt_data_dir;
        public IntPtr staging_dir;
    }

    // 宣告到 `get_user_id` (slot 55) 為止 —— 我們綁定的最後一個 fn。
    [StructLayout(LayoutKind.Sequential)]
    private struct RimeApi
    {
        public int data_size;
        public IntPtr setup;
        public IntPtr set_notification_handler;
        public IntPtr initialize;
        public IntPtr finalize;
        public IntPtr start_maintenance;
        public IntPtr is_maintenance_mode;
        public IntPtr join_maintenance_thread;
        public IntPtr deployer_initialize;
        public IntPtr prebuild;
        public IntPtr deploy;
        public IntPtr deploy_schema;
        public IntPtr deploy_config_file;
        public IntPtr sync_user_data;
        public IntPtr create_session;
        public IntPtr find_session;
        public IntPtr destroy_session;
        public IntPtr cleanup_stale_sessions;
        public IntPtr cleanup_all_sessions;
        public IntPtr process_key;
        public IntPtr commit_composition;
        public IntPtr clear_composition;
        public IntPtr get_commit;
        public IntPtr free_commit;
        public IntPtr get_context;
        public IntPtr free_context;
        public IntPtr get_status;
        public IntPtr free_status;
        public IntPtr set_option;
        public IntPtr get_option;
        public IntPtr set_property;
        public IntPtr get_property;
        public IntPtr get_schema_list;
        public IntPtr free_schema_list;
        public IntPtr get_current_schema;
        public IntPtr select_schema;
        public IntPtr schema_open;
        public IntPtr config_open;
        public IntPtr config_close;
        public IntPtr config_get_bool;
        public IntPtr config_get_int;
        public IntPtr config_get_double;
        public IntPtr config_get_string;
        public IntPtr config_get_cstring;
        public IntPtr config_update_signature;
        public IntPtr config_begin_map;
        public IntPtr config_next;
        public IntPtr config_end;
        public IntPtr simulate_key_sequence;
        public IntPtr register_module;
        public IntPtr find_module;
        public IntPtr run_task;
        public IntPtr get_shared_data_dir;
        public IntPtr get_user_data_dir;
        public IntPtr get_sync_dir;
        public IntPtr get_user_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RimeModule
    {
        public int data_size;
        public IntPtr module_name;
        public IntPtr initialize;
        public IntPtr finalize;
        public IntPtr get_api;
    }

    // 宣告到 `restore_user_dict` (slot 29) 為止 —— 我們綁定的最後一個 fn。
    [StructLayout(LayoutKind.Sequential)]
    private struct RimeLeversApi
    {
        public int data_size;
        public IntPtr custom_settings_init;
        public IntPtr custom_settings_destroy;
        public IntPtr load_settings;
        public IntPtr save_settings;
        public IntPtr customize_bool;
        public IntPtr customize_int;
        public IntPtr customize_double;
        public IntPtr customize_string;
        public IntPtr is_first_run;
        public IntPtr settings_is_modified;
        public IntPtr settings_get_config;
        public IntPtr switcher_settings_init;
        public IntPtr get_available_schema_list;
        public IntPtr get_selected_schema_list;
        public IntPtr schema_list_destroy;
        public IntPtr get_schema_id;
        public IntPtr get_schema_name;
        public IntPtr get_schema_version;
        public IntPtr get_schema_author;
        public IntPtr get_schema_description;
        public IntPtr get_schema_file_path;
        public IntPtr select_schemas;
        public IntPtr get_hotkeys;
        public IntPtr set_hotkeys;
        public IntPtr user_dict_iterator_init;
        public IntPtr user_dict_iterator_destroy;
        public IntPtr next_user_dict;
        public IntPtr backup_user_dict;
        public IntPtr restore_user_dict;
    }

    // 呼叫 iterator_init 前 librime 預期這個 struct 是 zero-initialised。
    [StructLayout(LayoutKind.Sequential)]
    private struct RimeUserDictIterator
    {
        public IntPtr ptr;
        public nuint i;
    }

    [LibraryImport("rime", EntryPoint = "rime_get_api")]
    private static partial IntPtr rime_get_api();

    // RimeApi 委派:
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApiSetup(ref RimeTraits traits);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApiDeployerInitialize(IntPtr traits);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int  ApiStartMaintenance(int fullCheck);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApiVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ApiFindModule(IntPtr moduleName);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ApiGetCString();

    // Levers 委派:
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UserDictIteratorInit(ref RimeUserDictIterator iter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UserDictIteratorDestroy(ref RimeUserDictIterator iter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NextUserDict(ref RimeUserDictIterator iter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BackupUserDictFn(IntPtr dictName);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RestoreUserDict(IntPtr snapshotFile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetApi();

    private static ApiSetup?              s_apiSetup;
    private static ApiDeployerInitialize? s_apiDeployerInit;
    private static ApiStartMaintenance?   s_apiStartMaint;
    private static ApiVoid?               s_apiJoinMaint;
    private static ApiVoid?               s_apiFinalize;
    private static ApiGetCString?         s_apiGetSyncDir;
    private static ApiGetCString?         s_apiGetUserId;

    private static UserDictIteratorInit?    s_iterInit;
    private static UserDictIteratorDestroy? s_iterDestroy;
    private static NextUserDict?            s_iterNext;
    private static BackupUserDictFn?        s_backup;
    private static RestoreUserDict?         s_restore;

    private static string s_userDir = string.Empty;

    public static void Setup(string sharedDataDir, string userDir)
    {
        s_userDir = userDir;
        string logDir = Path.GetTempPath();

        IntPtr apiPtr = rime_get_api();
        if (apiPtr == IntPtr.Zero)
            throw new InvalidOperationException("rime_get_api() returned null.");

        var api = ReadStruct<RimeApi>(apiPtr);

        s_apiSetup        = Bind<ApiSetup>             (api.setup);
        s_apiDeployerInit = Bind<ApiDeployerInitialize>(api.deployer_initialize);
        s_apiStartMaint   = Bind<ApiStartMaintenance>  (api.start_maintenance);
        s_apiJoinMaint    = Bind<ApiVoid>              (api.join_maintenance_thread);
        s_apiFinalize     = Bind<ApiVoid>              (api.finalize);
        s_apiGetSyncDir   = Bind<ApiGetCString>        (api.get_sync_dir);
        s_apiGetUserId    = Bind<ApiGetCString>        (api.get_user_id);
        var findModule    = Bind<ApiFindModule>        (api.find_module);

        var traits = new RimeTraits();
        traits.data_size = Marshal.SizeOf<RimeTraits>() - sizeof(int);

        var pins = new List<IntPtr>();
        IntPtr Utf8(string s) { var p = Marshal.StringToCoTaskMemUTF8(s); pins.Add(p); return p; }

        try
        {
            traits.shared_data_dir        = Utf8(sharedDataDir);
            traits.user_data_dir          = Utf8(s_userDir);
            // distribution_* 必須跟 installation.yaml 一致,否則 librime 的
            // installation_update 會改寫該檔,常駐 IME 與本工具就會輪流改寫,
            // 每次切換都 churn 一次。
            // 參見 librime/src/rime/lever/deployment_tasks.cc:123-128。
            var (distName, codeName, version) = RimePaths.ReadInstallationIdentity(s_userDir);
            traits.distribution_name      = Utf8(distName);
            traits.distribution_code_name = Utf8(codeName);
            traits.distribution_version   = Utf8(version);
            traits.app_name               = Utf8("rime.userdb-editor");
            traits.log_dir                = Utf8(logDir);
            traits.prebuilt_data_dir      = traits.shared_data_dir;

            s_apiSetup(ref traits);
            s_apiDeployerInit(IntPtr.Zero);

            // 每次啟動都跑一次 deployment —— start_maintenance(full_check=0) 會
            // 依序執行 installation_update / workspace_update / user_dict_upgrade
            // / cleanup_trash (deployment_tasks.cc)。我們要的主要是:
            //   1. installation_update 確保 sync_dir / user_id 可以查得到;
            //   2. user_dict_upgrade flush 掉任何待跑的 schema/userdb 升級,
            //      讓接下來的 backup/restore 不會踩到舊格式。
            // full_check=0 跳過 schema 重建,啟動才不會肉眼可見地慢。
            // join_maintenance_thread 等它跑完才繼續,後面馬上要讀 sync_dir。
            s_apiStartMaint(0);
            s_apiJoinMaint();
        }
        finally
        {
            foreach (var p in pins) Marshal.FreeCoTaskMem(p);
        }

        IntPtr modulePtr = WithUtf8("levers", p => findModule(p));
        if (modulePtr == IntPtr.Zero)
            throw new InvalidOperationException("Rime levers module not found.");

        var module = ReadStruct<RimeModule>(modulePtr);
        var getApi = Bind<GetApi>(module.get_api);

        IntPtr leversApiPtr = getApi();
        if (leversApiPtr == IntPtr.Zero)
            throw new InvalidOperationException("levers get_api() returned null.");

        var levers = ReadStruct<RimeLeversApi>(leversApiPtr);
        s_iterInit    = Bind<UserDictIteratorInit>   (levers.user_dict_iterator_init);
        s_iterDestroy = Bind<UserDictIteratorDestroy>(levers.user_dict_iterator_destroy);
        s_iterNext    = Bind<NextUserDict>           (levers.next_user_dict);
        s_backup      = Bind<BackupUserDictFn>       (levers.backup_user_dict);
        s_restore     = Bind<RestoreUserDict>        (levers.restore_user_dict);
    }

    public static void Shutdown() => s_apiFinalize?.Invoke();

    /// <summary>驗證開頭的 <c>data_size</c> ≥ 我們宣告的 prefix 再 copy out;
    /// librime 比 binding 舊就丟例外。</summary>
    private static T ReadStruct<T>(IntPtr ptr) where T : struct
    {
        int need = Marshal.SizeOf<T>() - sizeof(int);
        int got = Marshal.ReadInt32(ptr, 0);
        if (got < need)
            throw new InvalidOperationException(
                $"{typeof(T).Name} layout mismatch: data_size={got}, need >= {need}. " +
                $"librime is older than this binding expects, or " +
                $"the wrong rime_lib was picked (e.g. 32-bit vs 64-bit).");
        return Marshal.PtrToStructure<T>(ptr);
    }

    private static T Bind<T>(IntPtr fn) where T : Delegate
    {
        if (fn == IntPtr.Zero)
            throw new InvalidOperationException($"required {typeof(T).Name} function pointer is null.");
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    public static string GetUserDir() => s_userDir;

    public static List<string> ListUserDicts()
    {
        var list = new List<string>();
        var iter = new RimeUserDictIterator();
        if (s_iterInit!(ref iter) == 0) return list;
        try
        {
            while (true)
            {
                IntPtr namePtr = s_iterNext!(ref iter);
                if (namePtr == IntPtr.Zero) break;
                string? name = Marshal.PtrToStringUTF8(namePtr);
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }
        }
        finally
        {
            s_iterDestroy!(ref iter);
        }
        return list;
    }

    public static bool RestoreUserDictFromFile(string snapshotFile)
        => WithUtf8(snapshotFile, p => s_restore!(p) != 0);

    public static bool BackupUserDict(string dictName)
        => WithUtf8(dictName, p => s_backup!(p) != 0);

    private static T WithUtf8<T>(string s, Func<IntPtr, T> body)
    {
        IntPtr p = Marshal.StringToCoTaskMemUTF8(s);
        try     { return body(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? FindExistingSnapshot(string dictName)
    {
        string? syncDir = Marshal.PtrToStringUTF8(s_apiGetSyncDir!());
        if (string.IsNullOrEmpty(syncDir)) return null;

        string fileName = RimePaths.SnapshotFileName(dictName);
        string? userId  = Marshal.PtrToStringUTF8(s_apiGetUserId!());

        if (!string.IsNullOrEmpty(userId))
        {
            string direct = Path.Combine(syncDir, userId, fileName);
            if (File.Exists(direct)) return direct;
        }

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(syncDir))
            {
                string candidate = Path.Combine(sub, fileName);
                if (File.Exists(candidate))
                {
                    var time = File.GetLastWriteTimeUtc(candidate);
                    if (time > bestTime) { best = candidate; bestTime = time; }
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        return best;
    }
}
