# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Scope

Focus on the `tools/` directory only. The active project is `tools/RimeUserDbEditor.Avalonia/` (Avalonia 12 / .NET 10, cross-platform). The rest of the `weasel/` tree is reference/context, and `output/` holds the built binaries that the .NET tool P/Invokes into.

## Overview of the Weasel repository

Weasel (【小狼毫】) is the Windows distribution of the **Rime input method engine** (librime). The repo is a thin Windows-platform shell around librime plus build glue, installer, and a TSF IME front-end. The actual IME logic lives in the [librime/](weasel/librime/) git submodule.

## Architecture

### Process / DLL topology at runtime

```
+----------------------+    Win32 TSF    +-----------------------+
| Host app (Word, ...) | <-------------> | weaselx64.dll (TSF)   |
+----------------------+                 | = built from          |
                                         |   WeaselTSF/          |
                                         | links WeaselIPC client|
                                         +-----------+-----------+
                                                     |
                                          named pipe + shared mem
                                          \\.\pipe\<user>\WeaselNamedPipe
                                                     |
                                         +-----------v-----------+
                                         |  WeaselServer.exe     |
                                         |  WeaselIPCServer +    |
                                         |  RimeWithWeasel +     |
                                         |  WeaselUI (candidates)|
                                         |       |               |
                                         |       v               |
                                         |   rime.dll (librime)  |
                                         +-----------------------+
```

The TSF DLL is intentionally thin — every keystroke is forwarded to **the single long-running `WeaselServer.exe`** which owns librime. This is why librime state (sessions, userdb) is process-wide and why the userdb files in `%AppData%\Rime` are held under an exclusive leveldb lock while WeaselServer is running.

### Subproject roles

Static libs (linked into the exes/dll above):

| Project | Role |
|---|---|
| [WeaselIPC/](weasel/WeaselIPC/) | Client + server-side **interface** for the named-pipe IPC. `WEASEL_IPC_*` commands and `PipeMessage` struct defined in [include/WeaselIPC.h](weasel/include/WeaselIPC.h). |
| [WeaselIPCServer/](weasel/WeaselIPCServer/) | Server-side IPC **implementation** (pipe accept, dispatch to `RequestHandler`). |
| [WeaselUI/](weasel/WeaselUI/) | Direct2D/GDI candidate-window rendering. Has `/openmp` enabled. |
| [RimeWithWeasel/](weasel/RimeWithWeasel/) | The glue: implements `weasel::RequestHandler` by driving librime's session API; formats responses for the TSF client. Also hosts [`WeaselUtility.cpp`](weasel/RimeWithWeasel/WeaselUtility.cpp) which resolves user/shared data dirs. |

Binaries / DLLs:

| Target | Output | Role |
|---|---|---|
| [WeaselTSF/](weasel/WeaselTSF/) | `weasel.dll` (x86), `weaselx64.dll` (x64), `weaselARM*.dll` | TSF IME entry — the COM DLL registered with Windows TSF |
| [WeaselServer/](weasel/WeaselServer/) | `WeaselServer.exe` | The long-running server; one per user session. `/q` to quit; `/install`/`/uninstall` for COM registration. |
| [WeaselDeployer/](weasel/WeaselDeployer/) | `WeaselDeployer.exe` | Configuration/deployment GUI. Subcommands: `/deploy`, `/dict`, `/sync`, `/install`, `/help`. Uses **librime's `levers` module** for custom-settings + dict management. |
| [WeaselSetup/](weasel/WeaselSetup/) | `WeaselSetup.exe` | x86-only — IME registration tool used by the installer. |

There's also a separate cross-platform .NET 10 / Avalonia userdb editor in [tools/RimeUserDbEditor.Avalonia/](tools/RimeUserDbEditor.Avalonia/) that P/Invokes librime directly (Windows: the installed Weasel's `rime.dll`; Linux/macOS: any installed `librime.so` / `librime.dylib`) — no librime build required. See the repo-root [README.md](README.md) for its usage, layered module breakdown, and build instructions; the rest of this document describes the C++ Weasel source tree.

### Versioned struct ABI (RIME_STRUCT)

librime structs like `RimeTraits`, `RimeModule`, `RimeApi`, `RimeLeversApi` use a **self-versioning convention**: the first field is `int data_size`, set via `RIME_STRUCT_INIT(Type, var)` to `sizeof(Type) - sizeof(int)`. Callers test for the presence of a member with `RIME_STRUCT_HAS_MEMBER` ([librime/src/rime_api.h:60-75](weasel/librime/src/rime_api.h#L60-L75)). New fields are appended at the end; never inserted in the middle. [tools/RimeUserDbEditor.Avalonia/Rime.cs](tools/RimeUserDbEditor.Avalonia/Rime.cs) mirrors each struct as a `[StructLayout(LayoutKind.Sequential)]` C# struct (only the prefix through the last fn we call), so the runtime computes offsets from the host's pointer size — same source works for x64 / ARM64 / x86. `data_size` is validated against `Marshal.SizeOf<T>() - sizeof(int)` before dereferencing. Two dump helpers in [tools/findOffset/](tools/findOffset/) print the current librime struct layout for diff/sanity-check after an upgrade: [`dump-rime-structs.cs`](tools/findOffset/dump-rime-structs.cs) (Windows, `llvm-pdbutil` vs `rime.pdb`) and [`dump-rime-structs-gdb.sh`](tools/findOffset/dump-rime-structs-gdb.sh) (Linux/macOS, `gdb -batch ptype /o`).

### Function-table API vs deprecated direct exports

librime exports two parallel interfaces to the same implementations: deprecated direct symbols (`RimeSetup`, `RimeFindModule`, `RimeStartMaintenance`, `RimeGetSyncDir`, ...) and the modern `rime_get_api()` function-pointer table. They aren't separate code paths — [rime_api_impl.h:1128+](weasel/librime/src/rime_api_impl.h#L1128) literally stores `&RimeSetup` etc. into the table fields, so both reach the same machine code. **Upstream Weasel uses only the function table** ([RimeWithWeasel.cpp](weasel/RimeWithWeasel/RimeWithWeasel.cpp), [WeaselDeployer/Configurator.cpp](weasel/WeaselDeployer/Configurator.cpp), [DictManagementDialog.cpp](weasel/WeaselDeployer/DictManagementDialog.cpp)); the deprecated names exist purely for binary compat with old callers that linked them. New code in this repo should follow the same convention — touch only `rime_get_api()` and route everything else through the returned struct.

### Levers module

Configuration / dict management are not in the main `RimeApi` function table — they live in a separately-loaded **levers module** that ships inside `rime.dll`. To call them: `rime_api->find_module("levers")` returns a `RimeModule*` whose `get_api()` returns a `RimeLeversApi*` (function-pointer table defined in [librime/src/rime_levers_api.h](weasel/librime/src/rime_levers_api.h)). This is what `WeaselDeployer` and the Avalonia userdb editor use.

### Data and config paths

- **Shared (read-only) data**: directory `data/` next to the running .exe (resolved by [`WeaselSharedDataPath()`](weasel/RimeWithWeasel/WeaselUtility.cpp)). Populated from `output/data/` at install time.
- **User data**: registry override `HKCU\Software\Rime\Weasel\RimeUserDir`, else `%AppData%\Rime`. Userdb leveldb dirs live here (e.g. `luna_pinyin.userdb/`).
- **Logs**: `%TEMP%\rime.weasel` (created on demand).
- **Userdb has two TSV formats** with different fidelity. Choose carefully:
  - **`export_user_dict` / `import_user_dict`** ([table_db.cc](weasel/librime/src/rime/dict/table_db.cc)) uses `phrase<TAB>code<TAB>commits` and **loses `d` (decay) and `t` (tick)** on the round trip — import reconstructs `dee = (commits+1)/1e8`, `tick = 0`. Fine for plain text editing, lossy for sync.
  - **`backup_user_dict` / `restore_user_dict`** ([user_db.cc](weasel/librime/src/rime/dict/user_db.cc)) uses `code <TAB> phrase <TAB> c=N d=F t=T` and preserves everything; this is what Rime's own sync writes. The Avalonia editor in [tools/RimeUserDbEditor.Avalonia/](tools/RimeUserDbEditor.Avalonia/) standardised on this format for that reason.
    - `d` is serialised by `UserDbValue::Pack()` ([user_db.cc:21-25](weasel/librime/src/rime/dict/user_db.cc#L21-L25)) via `std::ostringstream << dee` — C++ stdlib default precision **6**, auto-switches to scientific notation for exponent < -4 (so an old entry that hasn't been touched in ~1800 ticks shows up as e.g. `9.90387e-05`). The Avalonia editor reads with `double.TryParse` and writes back with `"g6"` (6 significant digits, lowercase `g` so the exponent letter is lowercase `e` like C++ — uppercase `"G6"` would emit `E` and break byte-for-byte parity) — so disk strings round-trip byte-for-byte, including scientific-notation entries. `dee` is capped at `min(10000.0, ...)` on read ([user_db.cc:40](weasel/librime/src/rime/dict/user_db.cc#L40)); no lower bound, so old entries decay toward 0.
  - Both share the file-level conventions: header `# description`, `#@key<TAB>value` metadata lines, comments start with `#`, a `# no comment` line disables further comment parsing. See [librime/src/rime/dict/tsv.cc](weasel/librime/src/rime/dict/tsv.cc).
- **`restore_user_dict` is a 3-way merge, not a replace** — [user_db.cc:`UserDbMerger::Put`](weasel/librime/src/rime/dict/user_db.cc#L203-L223): `c` is absolute-value-bigger-wins (signed), `d` is `max(decayed_o, decayed_v)`, `t` is overwritten to `max(our_tick, their_tick)`. Entries missing from the input remain in the DB. To physically erase the DB, the Avalonia editor deletes the leveldb directory then re-runs `restore_user_dict` against the desired clean state.
  - **Decay-then-max, NOT "recompute at unified tick".** `o.dee` is decayed to `our_tick_` (DB's `/tick` metadata), `v.dee` is decayed to `their_tick_` (snapshot's `/tick` metadata) — *different* bases. After `max()` picks one, the chosen dee is tagged with `t = max_tick_`, which is a slight inflation (the dee is "older" than its new tick implies). Only `our_tick_ == their_tick_` makes the bias vanish.
  - **Wipe-and-Rebuild has a uniform-tick side-effect that pure Save does not.** When Restore runs against an empty leveldb dir (`our_tick_ = 0`, no existing entries), every entry merged in is `v.dee` decayed from `v.tick` to `their_tick_`, tagged with `t = their_tick_`. All entries end up at the same tick, with dee uniformly decayed forward — a full-table tick normalization. Pure Save only touches entries listed in the snapshot, so DB entries not in the snapshot keep their stale tick and dee.
- **librime's "delete entry" convention** (the action behind WeaselServer's <kbd>Ctrl+Delete</kbd> on a candidate) is *symmetric negate, magnitude-preserving*: [user_dictionary.cc:442](weasel/librime/src/rime/dict/user_dictionary.cc#L442) sets `commits = min(-1, -commits)` — so a popular word with `c=100` becomes `c=-100`, not `c=-1`. This makes the tombstone survive a subsequent merger round-trip against a stale snapshot that has `c=100`. Any custom "delete entry" implementation needs to mirror this; just writing `c=-1` lets `|−1| < |100|` revive the entry.

## Common gotchas

- **WeaselServer.exe holds a leveldb exclusive lock** on every userdb while it's running. librime's `OpenReadOnly` (in [level_db.cc](weasel/librime/src/rime/dict/level_db.cc)) still goes through `leveldb::DB::Open()` which takes the same lock — so even *exporting* a userdb fails while the server is running. Tools that touch userdb must either `WeaselServer.exe /q` first or drive the server into maintenance mode via IPC (`WEASEL_IPC_START_MAINTENANCE` / `_END_MAINTENANCE`, see [WeaselDeployer/Configurator.cpp](weasel/WeaselDeployer/Configurator.cpp)).
- **`rime_api->start_maintenance()` and `WEASEL_IPC_START_MAINTENANCE` are different things despite the name.** The former runs librime's deployment tasks (`installation_update`, `workspace_update`, `user_dict_upgrade`, `cleanup_trash`) inside the *current* process — it does not signal anyone else. The IPC command goes over `\\.\pipe\<user>\WeaselNamedPipe` to tell WeaselServer.exe to *release its leveldb locks*. A tool that wants both to deploy itself and pause WeaselServer must call both.
- **`installation_update` rewrites `installation.yaml` on `distribution_code_name` / `distribution_version` / `rime_version` mismatch** ([deployment_tasks.cc:123-128](weasel/librime/src/rime/lever/deployment_tasks.cc#L123-L128)). A second process driving librime against the same `user_data_dir` must pass the *same* distribution_* strings as WeaselServer to avoid churning the yaml on every alternation.
- **librime headers in [include/](weasel/include/)** (`rime_*.h`) are *output* of the librime build, not source. Don't hand-edit; rerun `build.bat rime` after pulling the submodule.
