"""tools/cppcheck/addons/misra.py의 모듈 상수를 읽어 MisraTables.cs를 생성한다. 순서는 원본 그대로 유지한다.

사용 (저장소 루트): python dotnetapp/JeonCheck.Analysis.Tests/MisraParity/gen_misra_tables.py
"""
import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "cppcheck", "addons"))
import misra  # noqa: E402

def lit(s): return json.dumps(s, ensure_ascii=False)
def arr(xs): return "new[] { " + ", ".join(lit(x) for x in xs) + " }"
out = ["// 이 파일은 Cppcheck 2.22 addons/misra.py의 모듈 상수에서 자동 생성했다. 직접 고치지 말고 원본 기준으로 다시 생성한다.",
       "namespace JeonCheck.Services.Misra;", "", "internal static class MisraTables", "{"]
for name in ["INT_TYPES", "STDINT_TYPES", "STDINT_H_DEFINES_MIN", "STDINT_H_DEFINES_MAX", "STDINT_H_DEFINES_C",
             "INTTYPES_H_DEFINES"]:
    out.append(f"    public static readonly string[] {name} = {arr(getattr(misra, name))};")
for name in ["C90_KEYWORDS", "C99_ADDED_KEYWORDS", "C11_ADDED_KEYWORDS"]:
    out.append(f"    public static readonly HashSet<string> {name} = new(StringComparer.Ordinal) {{ "
               + ", ".join(lit(x) for x in sorted(getattr(misra, name))) + " };")
for name in ["C90_STDLIB_IDENTIFIERS", "C99_STDLIB_IDENTIFIERS", "C11_STDLIB_IDENTIFIERS"]:
    d = getattr(misra, name)
    out.append(f"    public static readonly KeyValuePair<string, string[]>[] {name} =")
    out.append("    {")
    for k, v in d.items():
        out.append(f"        new({lit(k)}, {arr(v)}),")
    out.append("    };")
def tuple_const(fn, member):
    for c in fn.__code__.co_consts:
        if isinstance(c, tuple) and member in c:
            return c
    raise SystemExit("상수 튜플을 찾지 못함: " + member)
out.append(f"    public static readonly string[] ERRNO_SETTING_FUNCTIONS = {arr(tuple_const(misra.is_errno_setting_function, 'ftell'))};")
out.append(f"    public static readonly string[] MISRA_1_4_FUNCTIONS = {arr(tuple_const(misra.MisraChecker.misra_1_4, 'tmpfile_s'))};")
out.append("}")
open(os.path.join(ROOT, "dotnetapp", "JeonCheck", "Services", "Misra", "MisraTables.cs"), "w", encoding="utf-8", newline="\r\n").write("\n".join(out) + "\n")
print("ok", len(out), "lines")
