#!/usr/bin/env bash
#
# Dump librime struct field order via `gdb -batch ... ptype /o`. Output is
# paste-ready C# `[StructLayout(LayoutKind.Sequential)]` declarations for
# Rime.cs — `int` for 4-byte fields, `IntPtr` for everything else
# (pointers, function pointers).
#
# Linux/macOS counterpart of the Windows dump-rime-structs.cs (which reads
# rime.pdb via llvm-pdbutil). gdb 12+ supports `ptype /o`.
#
# Usage:
#   ./dump-rime-structs-gdb.sh /path/to/librime.so          # works if not stripped
#   ./dump-rime-structs-gdb.sh /path/to/librime.so.debug    # split-debug file
#
# Requires: gdb (12+), gawk. On WSL: `apt install gdb gawk`.
#
# Rime.cs declares only the prefix through the last fn pointer we bind, not
# the whole struct — this dumper emits everything librime has, so you can
# trim newly-added trailing fields or extend bindings as you like.
#
set -euo pipefail

DBG="${1:?usage: $0 <librime.so or .debug>}"

DUMP=$(gdb -batch -nx \
  -ex 'set pagination off' \
  -ex 'ptype /o struct rime_traits_t' \
  -ex 'ptype /o struct rime_module_t' \
  -ex 'ptype /o struct rime_api_t' \
  -ex 'ptype /o struct rime_levers_api_t' \
  -- "$DBG" 2>&1)

# Emit a C# Sequential struct mirroring `$1` (the C tag). `$2` is the C# name.
#
# Each `ptype /o` field line looks like:
#   /*    16      |       8 */    const char *user_data_dir;          ← plain ptr
#   /*    64      |       4 */    int min_log_level;                  ← plain int
#   /*   400      |       8 */    RimeModule *(*find_module)(...);    ← fn ptr
# Padding-hole lines like `/* XXX 4-byte hole */` have no `N | M` pair so the
# offset-match regex skips them naturally.
emit_struct() {
  local struct="$1" csharp="$2"
  echo "// $struct → $csharp"
  echo "    [StructLayout(LayoutKind.Sequential)]"
  echo "    private struct $csharp"
  echo "    {"
  printf '%s\n' "$DUMP" | gawk -v s="struct $struct" '
    index($0, s " {")           { inside = 1; next }
    inside && /^[[:space:]]*}/  { exit }
    inside && match($0, /\/\*[[:space:]]*[0-9]+[[:space:]]*\|[[:space:]]*[0-9]+[[:space:]]*\*\//) {
      # Pull the field identifier — fn pointer "(*name)(" or plain "name;".
      name = ""
      if (match($0, /\*[[:space:]]*([a-zA-Z_][a-zA-Z_0-9]*)[[:space:]]*\)[[:space:]]*\(/, n)) {
        name = n[1]
      } else if (match($0, /([a-zA-Z_][a-zA-Z_0-9]*)[[:space:]]*;/, n)) {
        name = n[1]
      }
      if (name == "") next

      # Type: int when the declaration (everything between `*/` and `;`)
      # contains no `*`. Anything with a `*` (pointer / fn-pointer) → IntPtr.
      decl = $0
      sub(/.*\*\//, "", decl)
      sub(/;.*/,    "", decl)
      if (decl ~ /\*/) print "        public IntPtr " name ";"
      else              print "        public int " name ";"
    }
  '
  echo "    }"
  echo
}

emit_struct rime_traits_t      RimeTraits
emit_struct rime_module_t      RimeModule
emit_struct rime_api_t         RimeApi
emit_struct rime_levers_api_t  RimeLeversApi
