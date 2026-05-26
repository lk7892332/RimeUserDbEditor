// Dump RimeTraits / RimeModule / RimeApi / RimeLeversApi layouts from rime.pdb
// via llvm-pdbutil, plus the WEASEL_IPC_COMMAND enum and weasel::PipeMessage
// wire layout from WeaselServer.pdb. Emits paste-ready C# Sequential struct
// declarations for Rime.cs and RimeIpc.cs — `int` for 4-byte fields,
// `IntPtr` for pointers / function pointers / unsigned 64-bit slots.
//
// .NET 10 file-based program — no csproj of its own.
//
//     dotnet run dump-rime-structs.cs <llvm-pdbutil.exe> <pdb-dir>
//
// <pdb-dir> must contain rime.pdb (required) and may contain WeaselServer.pdb
// (the IPC and PipeMessage emit are skipped if absent).

using System.Diagnostics;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run dump-rime-structs.cs <llvm-pdbutil.exe> <pdb-dir>");
    return 1;
}
string LlvmPdbutil = args[0];
string SymPath     = args[1];

if (!File.Exists(LlvmPdbutil)) { Console.Error.WriteLine($"llvm-pdbutil.exe not found: {LlvmPdbutil}"); return 1; }
string pdb = Path.Combine(SymPath, "rime.pdb");
if (!File.Exists(pdb))         { Console.Error.WriteLine($"rime.pdb not found under: {SymPath}");      return 1; }

// cTag → CsName mapping. Insertion order drives output order.
var BindMap = new Dictionary<string, string>
{
    ["rime_traits_t"]     = "RimeTraits",
    ["rime_module_t"]     = "RimeModule",
    ["rime_api_t"]        = "RimeApi",
    ["rime_levers_api_t"] = "RimeLeversApi",
};

string WeaselServerPdb = Path.Combine(SymPath, "WeaselServer.pdb");
string[] IpcWantedMembers = [
    "WEASEL_IPC_START_MAINTENANCE", "WEASEL_IPC_END_MAINTENANCE",
];

// Read both PDBs in parallel — each is a ~50MB external process spawn +
// regex scan (~5s); concurrent saves ~3s wall time.
var rimeTask = Task.Run(() =>
    new TpiIndex(RunCapture(LlvmPdbutil, ["dump", "--types", pdb])));
var wsTask = File.Exists(WeaselServerPdb)
    ? Task.Run(() => new TpiIndex(RunCapture(LlvmPdbutil, ["dump", "--types", WeaselServerPdb])))
    : null;

var rimeTpi = rimeTask.Result;

// ---- rime.pdb structs ---------------------------------------------------
foreach (var (cTag, csName) in BindMap)
{
    var def = rimeTpi.FindByName(cTag, "LF_STRUCTURE");
    if (def is null) { Console.Error.WriteLine($"WARNING: {cTag} not found"); continue; }
    var flBody = rimeTpi.GetBody(def.FieldListIdx);
    if (flBody is null) { Console.Error.WriteLine($"WARNING: {cTag} field list missing"); continue; }
    EmitStruct(cTag, csName, ParseMembers(flBody),
        csType: m => m.TypeName == "int" ? "int" : "IntPtr");
}

// ---- WeaselServer.pdb: IPC enum + PipeMessage ---------------------------
if (wsTask is not null)
{
    var wsTpi = wsTask.Result;

    var enumDef = wsTpi.FindByName("WEASEL_IPC_COMMAND", "LF_ENUM");
    if (enumDef is not null)
    {
        var enumFl = wsTpi.GetBody(enumDef.FieldListIdx);
        if (enumFl is not null)
        {
            // Preserve declared (source) order; filter to the subset we want.
            var consts = ParseEnumerators(enumFl)
                .Where(kv => IpcWantedMembers.Contains(kv.Key))
                .ToList();
            if (consts.Count > 0)
            {
                int width = consts.Max(c => c.Key.Length);
                Console.WriteLine("// WEASEL_IPC_COMMAND");
                foreach (var c in consts)
                    Console.WriteLine($"    private const int {c.Key.PadRight(width)} = 0x{c.Value:X4};");
                Console.WriteLine();
            }
        }
    }
    else { Console.Error.WriteLine("WARNING: WEASEL_IPC_COMMAND not found"); }

    // Msg's PDB TypeName is empty (it's the WEASEL_IPC_COMMAND enum, not a
    // primitive) and falls through to the int default — same wire layout.
    var pmDef = wsTpi.FindByName("weasel::PipeMessage", "LF_STRUCTURE");
    if (pmDef is not null)
    {
        var pmFl = wsTpi.GetBody(pmDef.FieldListIdx);
        if (pmFl is not null)
            EmitStruct("weasel::PipeMessage", "PipeMessage", ParseMembers(pmFl),
                csType: m => m.TypeName switch
                {
                    "unsigned long" or "unsigned int" => "uint",
                    _                                 => "int",
                });
    }
    else { Console.Error.WriteLine("WARNING: weasel::PipeMessage not found"); }
}
else
{
    Console.Error.WriteLine($"WARNING: {WeaselServerPdb} absent — RimeIpc.cs output skipped");
}

return 0;

// ============================================================================
// Helpers
// ============================================================================

static string RunCapture(string exe, string[] argList)
{
    var psi = new ProcessStartInfo(exe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true,
    };
    foreach (var a in argList) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return stdout;
}

static List<Member> ParseMembers(string body)
{
    var rows = new List<Member>();
    foreach (Match m in Patterns.Member.Matches(body))
    {
        rows.Add(new Member(
            Name:     m.Groups[1].Value,
            TypeIdx:  m.Groups[2].Value,
            TypeName: m.Groups[3].Value,
            Offset:   int.Parse(m.Groups[4].ValueSpan)));
    }
    return rows;
}

// Returned in source order via the regex-match iteration order — same as the
// declared order in the header. The caller filters; we never re-sort.
static List<KeyValuePair<string, int>> ParseEnumerators(string body)
{
    var rows = new List<KeyValuePair<string, int>>();
    foreach (Match m in Patterns.Enumerator.Matches(body))
    {
        rows.Add(new(m.Groups[1].Value, int.Parse(m.Groups[2].ValueSpan)));
    }
    return rows;
}

// Print a full C# Sequential struct declaration matching Rime.cs's in-class
// indentation (4-space declaration, 8-space body). `csType` picks per-field
// C# type from the PDB type name.
static void EmitStruct(
    string cTag,
    string csName,
    List<Member> members,
    Func<Member, string> csType)
{
    Console.WriteLine($"// {cTag} → {csName}");
    Console.WriteLine("    [StructLayout(LayoutKind.Sequential)]");
    Console.WriteLine($"    private struct {csName}");
    Console.WriteLine("    {");
    foreach (var m in members)
        Console.WriteLine($"        public {csType(m)} {m.Name};");
    Console.WriteLine("    }");
    Console.WriteLine();
}

// ============================================================================
// Types
// ============================================================================

internal sealed record Member(string Name, string TypeIdx, string TypeName, int Offset);

internal sealed record StructDef(string FieldListIdx, int SizeOf);

// Compiled regexes — defined here so both top-level statements and the
// TpiIndex class share them. RegexOptions.Compiled JITs to IL once per regex.
internal static class Patterns
{
    // Top-level TPI record header: \n + indent + 0xHHHH + " | " + KIND.
    // Using `\n` rather than `(?m)^` makes the engine skip non-line-start
    // positions instead of checking every character. llvm-pdbutil right-aligns
    // the `|` separator, so the leading-space count varies with hex width —
    // ` *` (not a fixed column) handles all widths.
    public static readonly Regex Header =
        new(@"\n *(0x[0-9A-Fa-f]+) +\| +(\w+)", RegexOptions.Compiled);

    public static readonly Regex FieldListRef =
        new(@"field\s+list:\s*(0x[0-9A-Fa-f]+|<no type>)", RegexOptions.Compiled);

    public static readonly Regex SizeOf =
        new(@"sizeof\s+(\d+)", RegexOptions.Compiled);

    public static readonly Regex Member =
        new(@"LF_MEMBER\s*\[\s*name\s*=\s*`([^`]+)`,\s*Type\s*=\s*(0x[0-9A-Fa-f]+)(?:\s*\(([^)]+)\))?,\s*offset\s*=\s*(\d+)",
            RegexOptions.Compiled);

    public static readonly Regex Enumerator =
        new(@"LF_ENUMERATE\s*\[(\w+)\s*=\s*(\d+)\]", RegexOptions.Compiled);
}

// Wraps a llvm-pdbutil --types dump with O(1) lookup by hex TPI index.
// Bodies are sliced on demand from the original dump string — we don't
// pre-materialise 170K substrings.
internal sealed class TpiIndex
{
    readonly string                _dump;
    readonly MatchCollection       _headers;
    readonly Dictionary<string,int> _hexIdx;

    public int Count => _headers.Count;

    public TpiIndex(string dump)
    {
        _dump    = dump;
        _headers = Patterns.Header.Matches(dump);
        _hexIdx  = new Dictionary<string, int>(_headers.Count);
        for (int i = 0; i < _headers.Count; i++)
            _hexIdx[_headers[i].Groups[1].Value] = i;
    }

    public string? GetBody(string hexIdx)
        => _hexIdx.TryGetValue(hexIdx, out int i) ? GetBodyByIdx(i) : null;

    string GetBodyByIdx(int i)
    {
        int start = _headers[i].Index;
        int end   = i + 1 < _headers.Count ? _headers[i + 1].Index : _dump.Length;
        return _dump.Substring(start, end - start);
    }

    // Find the real (non-forward-ref) record matching `name` + `kind`.
    public StructDef? FindByName(string name, string kind)
    {
        string needle = "`" + name + "`";   // llvm-pdbutil backtick-quotes type names
        for (int i = 0; i < _headers.Count; i++)
        {
            if (_headers[i].Groups[2].Value != kind) continue;
            int start = _headers[i].Index;
            int end   = i + 1 < _headers.Count ? _headers[i + 1].Index : _dump.Length;
            // IndexOf(value, start, count) scans the original $dump in place —
            // no per-record Substring allocation.
            if (_dump.IndexOf(needle, start, end - start) < 0) continue;

            string body = _dump.Substring(start, end - start);
            var fm = Patterns.FieldListRef.Match(body);
            if (!fm.Success || fm.Groups[1].Value == "<no type>") continue;
            var sm = Patterns.SizeOf.Match(body);
            return new StructDef(
                FieldListIdx: fm.Groups[1].Value,
                SizeOf:       sm.Success ? int.Parse(sm.Groups[1].ValueSpan) : 0);
        }
        return null;
    }
}
