using System.IO;
using System.Text.RegularExpressions;
using JeonCheck.Models;

namespace JeonCheck.Services;

// Cppcheck --dump에서 코드 뷰어 호버 팝업용 심볼을 만든다.
// 함수: FunctionId 토큰, 변수: VariableId 토큰, 매크로: MacroName 토큰 + #define 지시문.
// 정보가 없으면 항목을 만들지 않으며, 호출 측은 항목이 없을 때 팝업을 표시하지 않는다.
public static class SymbolTooltipBuilder
{
    private static readonly Regex DefineNameRegex = new(
        @"^\s*#\s*define\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // 파일별 심볼 목록 (정규화 경로 -> 심볼). 같은 파일·줄·이름은 첫 항목만 유지한다.
    public static Dictionary<string, List<SnapshotSymbol>> Build(DumpDocument dump)
    {
        var merged = new Dictionary<string, Dictionary<string, SnapshotSymbol>>(StringComparer.OrdinalIgnoreCase);

        foreach (var unit in dump.Configurations)
        {
            var perFile = BuildFromUnit(unit, dump.Platform);
            foreach (var kvp in perFile)
            {
                if (!merged.TryGetValue(kvp.Key, out var byKey))
                {
                    byKey = new Dictionary<string, SnapshotSymbol>(StringComparer.Ordinal);
                    merged[kvp.Key] = byKey;
                }
                foreach (var sym in kvp.Value)
                {
                    string key = $"{sym.Line}\n{sym.Name}";
                    byKey.TryAdd(key, sym);
                }
            }
        }

        return merged.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Values.OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<SnapshotSymbol>> BuildFromUnit(DumpCfgUnit unit, DumpPlatform platform)
    {
        var result = new Dictionary<string, List<SnapshotSymbol>>(StringComparer.OrdinalIgnoreCase);
        void Add(SnapshotSymbol sym)
        {
            string norm = AnalysisIssue.NormalizePath(sym.FilePath);
            if (!result.TryGetValue(norm, out var list))
            {
                list = new List<SnapshotSymbol>();
                result[norm] = list;
            }
            list.Add(sym);
        }

        // 매크로 정의: #define 이름 -> 지시문 원문
        var defineByName = new Dictionary<string, DumpDirective>(StringComparer.Ordinal);
        var defineByFileAndName = new Dictionary<string, DumpDirective>(StringComparer.Ordinal);
        foreach (var dir in unit.Directives)
        {
            var m = DefineNameRegex.Match(dir.Str ?? string.Empty);
            if (!m.Success)
                continue;
            string name = m.Groups[1].Value;
            defineByName.TryAdd(name, dir);
            defineByFileAndName.TryAdd($"{AnalysisIssue.NormalizePath(dir.File)}\n{name}", dir);
        }

        foreach (var kvp in defineByFileAndName)
        {
            int sep = kvp.Key.IndexOf('\n');
            string fileNorm = kvp.Key.Substring(0, sep);
            string name = kvp.Key.Substring(sep + 1);
            var dir = kvp.Value;
            Add(new SnapshotSymbol
            {
                FilePath = dir.File,
                Line = dir.LineNr,
                Name = name,
                Kind = "macro",
                Tooltip = $"매크로 {name}\n정의: {dir.Str.Trim()}\n위치: {Path.GetFileName(dir.File)}:{dir.LineNr}"
            });
            _ = fileNorm;
        }

        foreach (var tok in unit.Tokens)
        {
            if (string.IsNullOrEmpty(tok.File) || tok.LineNr <= 0)
                continue;

            // 변수 또는 함수 이름 토큰
            if (!string.IsNullOrEmpty(tok.VariableId) && tok.Variable != null && IsIdentifier(tok.Str))
            {
                Add(new SnapshotSymbol
                {
                    FilePath = tok.File,
                    Line = tok.LineNr,
                    Name = tok.Str,
                    Kind = "variable",
                    Tooltip = BuildVariableTooltip(tok, platform)
                });
            }
            else if (!string.IsNullOrEmpty(tok.FunctionId) &&
                     unit.FunctionById.TryGetValue(tok.FunctionId, out var func) &&
                     string.Equals(tok.Str, func.Name, StringComparison.Ordinal))
            {
                Add(new SnapshotSymbol
                {
                    FilePath = tok.File,
                    Line = tok.LineNr,
                    Name = tok.Str,
                    Kind = "function",
                    Tooltip = BuildFunctionTooltip(func, unit, platform)
                });
            }

            // 매크로 사용처: 전개된 토큰의 원래 매크로 이름으로 정의 원문을 연결
            if (!string.IsNullOrEmpty(tok.MacroName))
            {
                string normFile = AnalysisIssue.NormalizePath(tok.File);
                DumpDirective? dir = null;
                defineByFileAndName.TryGetValue($"{normFile}\n{tok.MacroName}", out dir);
                if (dir == null)
                    defineByName.TryGetValue(tok.MacroName, out dir);
                if (dir == null)
                    continue;
                Add(new SnapshotSymbol
                {
                    FilePath = tok.File,
                    Line = tok.LineNr,
                    Name = tok.MacroName,
                    Kind = "macro",
                    Tooltip = $"매크로 {tok.MacroName}\n정의: {dir.Str.Trim()}\n위치: {Path.GetFileName(dir.File)}:{dir.LineNr}"
                });
            }
        }

        // 같은 파일·줄·이름 중복 제거 (여러 설정 조합에서 같은 토큰이 반복됨)
        foreach (var key in result.Keys.ToList())
        {
            result[key] = result[key]
                .GroupBy(s => $"{s.Line}\n{s.Name}", StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
        }

        return result;
    }

    private static string BuildVariableTooltip(DumpToken tok, DumpPlatform platform)
    {
        var v = tok.Variable!;
        string declType = ReconstructDeclType(v) ?? tok.FormatTypeDisplay(platform);
        string underlying = tok.FormatTypeDisplay(platform);
        string accessKo = v.Access switch
        {
            "Local" => "지역 변수",
            "Argument" => "매개변수",
            "Global" => "전역 변수",
            _ => string.IsNullOrWhiteSpace(v.Access) ? "변수" : v.Access
        };
        string declLoc = v.NameToken != null && !string.IsNullOrEmpty(v.NameToken.File) && v.NameToken.LineNr > 0
            ? $"{Path.GetFileName(v.NameToken.File)}:{v.NameToken.LineNr}"
            : $"{Path.GetFileName(tok.File)}:{tok.LineNr}";
        return $"변수 {tok.Str}\n타입: {declType} ({underlying})\n구분: {accessKo}\n선언: {declLoc}";
    }

    private static string BuildFunctionTooltip(DumpFunction func, DumpCfgUnit unit, DumpPlatform platform)
    {
        string returnType = "타입 정보 없음";
        var parenTok = FindNextToken(func.Token) ?? FindNextToken(func.TokenDef);
        if (parenTok != null && parenTok.Str == "(" && !string.IsNullOrEmpty(parenTok.ValueType))
            returnType = parenTok.FormatTypeDisplay(platform);

        var argDisplays = new List<string>();
        foreach (var arg in func.Args)
        {
            if (arg == null)
                continue;
            string argName = arg.NameToken?.Str ?? "(이름 없음)";
            string argType = ReconstructDeclType(arg) ?? arg.NameToken?.FormatTypeDisplay(platform) ?? "타입 정보 없음";
            argDisplays.Add($"{argType} {argName}");
        }
        string args = argDisplays.Count > 0 ? string.Join(", ", argDisplays) : "()";
        if (argDisplays.Count > 0)
            args = $"({args})";

        string defLoc = func.Token != null && !string.IsNullOrEmpty(func.Token.File)
            ? $"{Path.GetFileName(func.Token.File)}:{func.Token.LineNr}"
            : "위치 정보 없음";
        string tooltip = $"함수 {func.Name}\n원형: {returnType} {func.Name}{args}\n정의: {defLoc}";
        if (func.TokenDef != null && !string.IsNullOrEmpty(func.TokenDef.File) &&
            (func.Token == null || func.TokenDef.Id != func.Token.Id))
        {
            tooltip += $"\n선언: {Path.GetFileName(func.TokenDef.File)}:{func.TokenDef.LineNr}";
        }
        _ = unit;
        return tooltip;
    }

    private static DumpToken? FindNextToken(DumpToken? tok)
    {
        var next = tok?.Next;
        // 함수 이름 뒤에 바로 '(' 가 오지 않는 경우(주석성 토큰 등) 최대 3칸까지 건너뛴다.
        for (int i = 0; i < 3 && next != null; i++)
        {
            if (next.Str == "(")
                return next;
            if (!string.IsNullOrWhiteSpace(next.Str) && next.Str != "*" && next.Str != "const" && next.Str != "volatile")
                break;
            next = next.Next;
        }
        return null;
    }

    private static string? ReconstructDeclType(DumpVariable v)
    {
        var start = v.TypeStartToken;
        if (start == null)
            return null;
        var parts = new List<string>();
        var cur = start;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cur != null && seen.Add(cur.Id))
        {
            if (!string.IsNullOrWhiteSpace(cur.Str))
                parts.Add(cur.Str);
            if (cur.Id == v.TypeEndTokenId)
                break;
            cur = cur.Next;
            // 타입 토큰열이 비정상적으로 길면 중단 (오탐 방지)
            if (parts.Count > 8)
                break;
        }
        if (parts.Count == 0)
            return null;
        string decl = string.Join(" ", parts);
        if (v.IsPointer)
            decl += " *";
        if (v.IsArray)
            decl += "[]";
        return decl;
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        if (!(s[0] == '_' || char.IsLetter(s[0])))
            return false;
        for (int i = 1; i < s.Length; i++)
        {
            if (!(s[i] == '_' || char.IsLetterOrDigit(s[i])))
                return false;
        }
        return true;
    }
}
