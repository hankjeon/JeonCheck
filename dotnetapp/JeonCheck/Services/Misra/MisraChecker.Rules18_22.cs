using System.Text.RegularExpressions;
using static JeonCheck.Services.Misra.MisraHelpers;

namespace JeonCheck.Services.Misra;

// misra.py misra_18_4 ~ misra_22_10
public sealed partial class MisraChecker
{
    private void Misra_18_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("+" or "-" or "+=" or "-=")) continue;
            if (token.AstOperand1 == null || token.AstOperand2 == null) continue;
            var vt1 = token.AstOperand1.ValueType;
            var vt2 = token.AstOperand2.ValueType;
            if (vt1 != null && vt1.Pointer > 0) ReportError(token, 18, 4);
            else if (vt2 != null && vt2.Pointer > 0) ReportError(token, 18, 4);
        }
    }

    private void Misra_18_5(Configuration cfg)
    {
        foreach (var var in cfg.Variables)
        {
            if (!var.IsPointer) continue;
            var typetok = var.NameToken;
            int count = 0;
            while (typetok != null)
            {
                if (typetok.Str == "*") count++;
                else if (!typetok.IsName) break;
                typetok = typetok.Previous;
            }
            if (count > 2) ReportError(var.NameToken, 18, 5);
        }
    }

    private void Misra_18_7(Configuration cfg)
    {
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Struct") continue;
            var token = scope.BodyStart!.Next;
            while (token != scope.BodyEnd && token != null)
            {
                // 중첩 구조체에서 중복 보고하지 않도록 건너뛴다
                if (token.Str == "{") token = token.Link;
                if (token!.AstOperand1 == null)
                {
                    // 함수 포인터 매개변수 형식은 건너뛴다
                }
                else if (SimpleMatch(token, "[ ]"))
                {
                    ReportError(token, 18, 7);
                    break;
                }
                token = token.Next;
            }
        }
    }

    private void Misra_18_8(Configuration cfg)
    {
        foreach (var var in cfg.Variables)
        {
            if (!var.IsArray || !var.IsLocal) continue;
            var typetok = var.NameToken!.Next;
            if (typetok == null || typetok.Str != "[") continue;
            if (typetok.AstOperand2 == null) continue;
            if (!IsConstantExpression(typetok.AstOperand2) && !IsUnknownConstantExpression(typetok.AstOperand2))
                ReportError(var.NameToken, 18, 8);
        }
    }

    private void Misra_19_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "union") ReportError(token, 19, 2);
    }

    private void Misra_20_1(Configuration cfg)
    {
        var tokenInFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var nullFileLine = (int?)null;
        foreach (var token in cfg.Tokenlist)
        {
            if (token.File == null)
            {
                nullFileLine = nullFileLine == null ? token.Linenr : Math.Min(nullFileLine.Value, token.Linenr);
                continue;
            }
            tokenInFile[token.File] = tokenInFile.TryGetValue(token.File, out int line) ? Math.Min(line, token.Linenr) : token.Linenr;
        }
        foreach (var directive in cfg.Directives)
        {
            if (!directive.Str.StartsWith("#include", StringComparison.Ordinal)) continue;
            int? first = directive.File == null ? nullFileLine : tokenInFile.TryGetValue(directive.File, out int l) ? l : null;
            if (first == null) continue;
            if (first < directive.Linenr) ReportError(directive, 20, 1);
        }
    }

    private void Misra_20_2(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            if (!directive.Str.StartsWith("#include ", StringComparison.Ordinal)) continue;
            foreach (var pattern in new[] { "\\", "//", "/*", ",", "'" })
            {
                if (directive.Str.Contains(pattern, StringComparison.Ordinal))
                {
                    ReportError(directive, 20, 2);
                    break;
                }
            }
        }
    }

    private void Misra_20_3(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            if (!directive.Str.StartsWith("#include ", StringComparison.Ordinal)) continue;
            var words = directive.Str.Split(' ');
            if (words.Length > 2)
            {
                ReportError(directive, 20, 3);
            }
            else if (words.Length > 1)
            {
                string filename = words[1];
                if (!((filename.StartsWith('"') && filename.EndsWith('"')) || (filename.StartsWith('<') && filename.EndsWith('>'))) &&
                    filename.Contains('.'))
                    ReportError(directive, 20, 3);
            }
        }
    }

    private static readonly Regex DefineLowercase = new(@"#define ([a-z][a-z0-9_]+)", RegexOptions.CultureInvariant);

    private void Misra_20_4(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            var res = DefineLowercase.Match(directive.Str);
            if (res.Success && IsKeyword(res.Groups[1].Value, cfg.Standards.C)) ReportError(directive, 20, 4);
        }
    }

    private void Misra_20_5(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
            if (directive.Str.StartsWith("#undef ", StringComparison.Ordinal)) ReportError(directive, 20, 5);
    }

    private void Misra_20_7(Configuration cfg)
    {
        // 문자열 리터럴 연결(#define MACRO(A, B) (A " " B))은 두 인자 모두 보고하지 않는다
        static (bool NeedCheck, bool SkipNext) FindStringConcat(string exp, string arg, List<string> directiveArgs)
        {
            int argPos = Py.Find(exp, arg, 0);
            bool needCheck = false, skipNext = false, stateInString = false;
            int posSearch = argPos + 1;
            var otherArgs = directiveArgs.Where(a => a != arg).Select(a => a.Trim()).ToList();
            arg = arg.Trim();
            while (posSearch < exp.Length)
            {
                char c = Py.At(exp, posSearch);
                if (c == '"')
                {
                    stateInString = !stateInString;
                    posSearch++;
                }
                else if (Py.IsAlnum(c))
                {
                    string word = "";
                    while (posSearch < exp.Length && Py.IsAlnum(Py.At(exp, posSearch)))
                    {
                        word += Py.At(exp, posSearch);
                        posSearch++;
                    }
                    if (word == arg)
                    {
                        posSearch++;
                    }
                    else if (otherArgs.Contains(word))
                    {
                        skipNext = true;
                        break;
                    }
                }
                else if (c == ' ' || stateInString)
                {
                    posSearch++;
                }
                else
                {
                    needCheck = true;
                    break;
                }
            }
            return (needCheck, skipNext);
        }

        foreach (var directive in cfg.Directives)
        {
            var d = new Define(directive);
            string exp = "(" + d.ExpansionList + ")";
            bool skipNext = false;
            foreach (var arg in d.Args)
            {
                if (skipNext)
                {
                    (_, skipNext) = FindStringConcat(exp, arg, d.Args);
                    continue;
                }
                (bool needCheck, skipNext) = FindStringConcat(exp, arg, d.Args);
                if (!needCheck) continue;
                if (arg.Length == 0) continue; // 원본은 빈 인자에서 끝나지 않는다 (무한 반복 방지)

                int pos = 0;
                while (pos < exp.Length)
                {
                    pos = Py.Find(exp, arg, pos);
                    if (pos < 0) break;
                    int pos1 = pos - 1;
                    int pos2 = pos + arg.Length;
                    pos = pos2;
                    if (pos1 >= 0 && (IsAlnumAscii(exp[pos1]) || exp[pos1] == '_')) continue;
                    if (pos2 < exp.Length && (IsAlnumAscii(exp[pos2]) || exp[pos2] == '_')) continue;
                    while (pos1 >= 0 && exp[pos1] == ' ') pos1--;
                    if (Py.At(exp, pos1) == '#') continue;
                    if (!"([,.".Contains(Py.At(exp, pos1)))
                    {
                        ReportError(directive, 20, 7);
                        break;
                    }
                    while (pos2 < exp.Length && exp[pos2] == ' ') pos2++;
                    if (pos2 < exp.Length && !")]#,".Contains(exp[pos2]))
                    {
                        ReportError(directive, 20, 7);
                        break;
                    }
                }
            }
        }
    }

    private void Misra_20_8(Configuration cfg)
    {
        foreach (var cond in cfg.PreprocessorIfConditions)
            if (cond.Result != 0 && cond.Result is not (0 or 1)) ReportError(cond, 20, 8);
    }

    private static readonly Regex DefinedParen = new(@"[^_a-zA-Z0-9]defined[ ]*\([ ]*([_a-zA-Z0-9]+)[ ]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex DefinedBare = new(@"[^_a-zA-Z0-9]defined[ ]*([_a-zA-Z0-9]+)", RegexOptions.CultureInvariant);

    private void Misra_20_9(Configuration cfg)
    {
        foreach (var cond in cfg.PreprocessorIfConditions)
        {
            if (cond.E == null) continue;
            var defined = new List<string>();
            foreach (var directive in cfg.Directives)
            {
                if (directive.File == cond.File && directive.Linenr == cond.Linenr)
                {
                    foreach (Match m in DefinedParen.Matches(directive.Str)) defined.Add(m.Groups[1].Value);
                    foreach (Match m in DefinedBare.Matches(directive.Str)) defined.Add(m.Groups[1].Value);
                    break;
                }
            }
            foreach (var s in cond.E.Split(' '))
            {
                char c0 = s[0]; // 원본도 빈 문자열이면 IndexError
                if (c0 is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                {
                    if (IsKeyword(s, cfg.Standards.C)) continue;
                    if (defined.Contains(s)) continue;
                    ReportError(cond, 20, 9);
                }
            }
        }
    }

    private void Misra_20_10(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            var d = new Define(directive);
            if (d.ExpansionList.Contains('#')) ReportError(directive, 20, 10);
        }
    }

    private void Misra_20_11(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            var d = new Define(directive);
            foreach (var arg in d.Args)
            {
                // 원본은 인자 이름을 그대로 정규식에 넣는다 (인자는 영숫자·_·공백만 가능)
                if (Regex.IsMatch(" " + d.ExpansionList, "[^#]#[ ]*" + arg + "[ ]*##", RegexOptions.CultureInvariant))
                    ReportError(directive, 20, 11);
            }
        }
    }

    private void Misra_20_12(Configuration cfg)
    {
        static bool IsHashHashOp(string expansionList, string arg) =>
            Regex.IsMatch(expansionList, "##[ ]*" + arg + "[^a-zA-Z0-9_]", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(expansionList, "[^a-zA-Z0-9_]" + arg + "[ ]*##", RegexOptions.CultureInvariant);

        static bool IsOtherOp(string expansionList, string arg)
        {
            int pos = Py.Find(expansionList, arg);
            while (pos >= 0)
            {
                int pos1 = pos - 1;
                int pos2 = pos + arg.Length;
                pos = Py.Find(expansionList, arg, pos2);
                if (IsAlnumAscii(Py.At(expansionList, pos1)) || Py.At(expansionList, pos1) == '_') continue;
                if (IsAlnumAscii(Py.At(expansionList, pos2)) || Py.At(expansionList, pos2) == '_') continue;
                while (Py.At(expansionList, pos1) == ' ') pos1--;
                if (Py.At(expansionList, pos1) == '#') continue;
                while (Py.At(expansionList, pos2) == ' ') pos2++;
                if (Py.At(expansionList, pos2) == '#') continue;
                return true;
            }
            return false;
        }

        bool IsArgMacroUsage(Directive directive)
        {
            foreach (var mu in cfg.MacroUsage)
            {
                if (mu.File != directive.File || mu.Linenr != directive.Linenr) continue;
                foreach (var other in cfg.MacroUsage)
                {
                    if (ReferenceEquals(other, mu)) continue;
                    // 원본은 존재하지 않는 uselinenr 속성(항상 None)을 비교하므로 줄 번호는 비교되지 않는다
                    if (mu.UseFile == other.UseFile && mu.UseColumn == other.UseColumn) return true;
                }
            }
            return false;
        }

        foreach (var directive in cfg.Directives)
        {
            var define = new Define(directive);
            string expansionList = "(" + define.ExpansionList + ")";
            foreach (var arg in define.Args)
            {
                if (!IsHashHashOp(expansionList, arg)) continue;
                if (!IsOtherOp(expansionList, arg)) continue;
                if (IsArgMacroUsage(directive))
                {
                    ReportError(directive, 20, 12);
                    break;
                }
            }
        }
    }

    private static readonly Regex DirectiveKeyword = new(@"^#[ ]*([^ (<]*)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "define", "elif", "else", "endif", "error", "if", "ifdef", "ifndef", "include", "pragma", "undef", "warning",
    };

    private void Misra_20_13(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            string dir = directive.Str;
            var mo = DirectiveKeyword.Match(dir);
            if (mo.Success) dir = mo.Groups[1].Value;
            if (!KnownDirectives.Contains(dir)) ReportError(directive, 20, 13);
        }
    }

    private void Misra_20_14(Configuration cfg)
    {
        var ifStack = new List<Directive>();
        foreach (var directive in cfg.Directives)
        {
            string s = directive.Str;
            if (s.StartsWith("#if ", StringComparison.Ordinal) || s.StartsWith("#ifdef ", StringComparison.Ordinal) ||
                s.StartsWith("#ifndef ", StringComparison.Ordinal))
            {
                ifStack.Add(directive);
            }
            else if (s == "#else" || s.StartsWith("#elif ", StringComparison.Ordinal))
            {
                if (ifStack.Count == 0)
                {
                    ReportError(directive, 20, 14);
                    ifStack.Add(directive);
                }
                else if (directive.File != ifStack[^1].File)
                {
                    ReportError(directive, 20, 14);
                }
            }
            else if (s == "#endif")
            {
                if (ifStack.Count == 0)
                {
                    ReportError(directive, 20, 14);
                }
                else if (directive.File != ifStack[^1].File)
                {
                    ReportError(directive, 20, 14);
                    ifStack.RemoveAt(ifStack.Count - 1);
                }
            }
        }
    }

    private static readonly Regex ForbiddenMacro = new(@"#(?:define|undef) _[_A-Z]+", RegexOptions.CultureInvariant);
    private static readonly Regex MacroNameRe = new(@"#(?:define|undef) (.+)[ $]", RegexOptions.CultureInvariant);
    private static readonly Regex BuiltinName = new(@"^_+BUILTIN_.*", RegexOptions.CultureInvariant);

    private void Misra_21_1(Configuration cfg)
    {
        foreach (var d in cfg.Directives)
        {
            if (ForbiddenMacro.IsMatch(d.Str))
            {
                ReportError(d, 21, 1);
                continue;
            }
            var m = MacroNameRe.Match(d.Str);
            if (!m.Success) continue;
            if (IsStdLibId(m.Groups[1].Value, cfg.Standards.C)) ReportError(d, 21, 1);
        }
    }

    private void Misra_21_2(Configuration cfg)
    {
        foreach (var directive in cfg.Directives)
        {
            var define = new Define(directive);
            if (BuiltinName.IsMatch(define.Name.ToUpperInvariant())) ReportError(directive, 21, 2);
        }
        foreach (var func in cfg.Functions)
        {
            if (IsStdLibId(func.Name, cfg.Standards.C))
                ReportError(func.TokenDef ?? func.Token, 21, 2);
        }
    }

    private void ReportStdCall(Configuration cfg, int n1, int n2, params string[] names)
    {
        foreach (var token in cfg.Tokenlist)
            if (IsFunctionCall(token, cfg.Standards.C) && Array.IndexOf(names, token.AstOperand1!.Str) >= 0)
                ReportError(token, n1, n2);
    }

    private void Misra_21_3(Configuration cfg) => ReportStdCall(cfg, 21, 3, "malloc", "calloc", "realloc", "free");

    private void Misra_21_4(Configuration cfg)
    {
        var directive = FindInclude(cfg.Directives, "<setjmp.h>");
        if (directive != null) ReportError(directive, 21, 4);
    }

    private void Misra_21_5(Configuration cfg)
    {
        var directive = FindInclude(cfg.Directives, "<signal.h>");
        if (directive != null) ReportError(directive, 21, 5);
    }

    private void Misra_21_6(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            // 원본은 isFunctionCall(token)을 기본 표준(c99)으로 호출한다
            if (!IsFunctionCall(token) || token.Previous!.Function != null) continue;
            var standardId = GetStdLib(cfg.Standards.C);
            string funcname = token.Previous.Str;
            if (Array.IndexOf(StdLibGet(standardId, "stdio.h") ?? Array.Empty<string>(), funcname) >= 0 ||
                Array.IndexOf(StdLibGet(standardId, "wchar.h") ?? Array.Empty<string>(), funcname) >= 0)
                ReportError(token, 21, 6);
        }
    }

    private void Misra_21_7(Configuration cfg) => ReportStdCall(cfg, 21, 7, "atof", "atoi", "atol", "atoll");

    private void Misra_21_8(Configuration cfg) => ReportStdCall(cfg, 21, 8, "abort", "exit", "getenv");

    private void Misra_21_9(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str is "bsearch" or "qsort" && token.Next != null && token.Next.Str == "(") ReportError(token, 21, 9);
    }

    private void Misra_21_10(Configuration cfg)
    {
        var directive = FindInclude(cfg.Directives, "<time.h>");
        if (directive != null) ReportError(directive, 21, 10);
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "wcsftime" && token.Next != null && token.Next.Str == "(") ReportError(token, 21, 10);
    }

    private void Misra_21_11(Configuration cfg)
    {
        var directive = FindInclude(cfg.Directives, "<tgmath.h>");
        if (directive != null) ReportError(directive, 21, 11);
    }

    private void Misra_21_12(Configuration cfg)
    {
        if (FindInclude(cfg.Directives, "<fenv.h>") == null) return;
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str == "fexcept_t" && token.IsName) ReportError(token, 21, 12);
            if (IsFunctionCall(token, cfg.Standards.C) &&
                token.AstOperand1!.Str is "feclearexcept" or "fegetexceptflag" or "feraiseexcept" or "fesetexceptflag" or "fetestexcept")
                ReportError(token, 21, 12);
        }
    }

    private void Misra_21_14(Configuration cfg)
    {
        // strcpy/strlen 등에 쓰인 버퍼 (원본처럼 varId가 None인 항목도 담길 수 있다)
        var stringBuffers = new List<int?>();
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str[0] != 's' || !IsFunctionCall(token.Next, cfg.Standards.C)) continue;
            var (name, args) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
            if (name == null) continue;

            List<int?> GetStringBuffers(bool match, int[] argnum)
            {
                var ret = new List<int?>();
                if (!match) return ret;
                foreach (int a in argnum)
                {
                    if (a >= args!.Count) continue;
                    Token? arg = args[a];
                    while (arg != null && arg.Str is "." or "::") arg = arg.AstOperand2;
                    if (arg != null && arg.VarId != 0 && !ret.Contains(arg.VarId)) ret.Add(arg.VarId);
                }
                return ret;
            }

            stringBuffers.AddRange(GetStringBuffers(name == "strcpy", new[] { 0, 1 }));
            stringBuffers.AddRange(GetStringBuffers(name == "strncpy", new[] { 0, 1 }));
            stringBuffers.AddRange(GetStringBuffers(name == "strlen", new[] { 0 }));
            stringBuffers.AddRange(GetStringBuffers(name == "strcmp", new[] { 0, 1 }));
            stringBuffers.AddRange(GetStringBuffers(name == "sprintf", new[] { 0 }));
            stringBuffers.AddRange(GetStringBuffers(name == "snprintf", new[] { 0, 3 }));
        }

        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "memcmp") continue;
            var (name, args) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
            if (name == null) continue;
            if (args!.Count != 3) continue;
            foreach (var arg0 in args.Take(2))
            {
                Token? arg = arg0;
                if (arg.Str[^1] == '"')
                {
                    ReportError(arg, 21, 14);
                    continue;
                }
                while (arg != null && arg.Str is "." or "::") arg = arg.AstOperand2;
                if (arg != null && arg.VarId is not null and not 0 && stringBuffers.Contains(arg.VarId)) ReportError(arg, 21, 14);
            }
        }
    }

    private void Misra_21_15(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("memcpy" or "memmove" or "memcmp")) continue;
            var (name, args) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
            if (name == null) continue;
            if (args!.Count != 3) continue;
            if (args[0].ValueType == null || args[1].ValueType == null) continue;
            if (args[0].ValueType!.Type == args[1].ValueType!.Type) continue;
            if (args[0].ValueType!.Type == "void" || args[1].ValueType!.Type == "void") continue;
            ReportError(token, 21, 15);
        }
    }

    private void Misra_21_16(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "memcmp") continue;
            var (name, args) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
            if (name == null) continue;
            if (args!.Count != 3) continue;
            foreach (var arg in args.Take(2))
            {
                if (arg.ValueType == null) continue;
                if (arg.ValueType.Pointer > 1) continue;
                if (GetEssentialTypeCategory(arg) is "unsigned" or "signed" or "bool") continue;
                if (arg.ValueType.IsEnum()) continue;
                ReportError(token, 21, 16);
            }
        }
    }

    private void Misra_21_19(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is "localeconv" or "getenv" or "setlocale" or "strerror" && SimpleMatch(token.Next, "("))
            {
                var (name, _) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
                if (name == null || name != token.Str) continue;
                var parent = token.Next!;
                while (SimpleMatch(parent.AstParent, "+")) parent = parent.AstParent!;
                // x = f()
                if (SimpleMatch(parent.AstParent, "=") && parent == parent.AstParent!.AstOperand2)
                {
                    var lhs = parent.AstParent.AstOperand1;
                    if (lhs?.ValueType != null && lhs.ValueType.Pointer > 0 && lhs.ValueType.Constness == 0)
                        ReportError(token, 21, 19);
                }
            }
            if (token.Str == "=")
            {
                var lhs = token.AstOperand1;
                while (SimpleMatch(lhs, "*") && lhs!.AstOperand2 == null) lhs = lhs.AstOperand1;
                if (!SimpleMatch(lhs, ".")) continue;
                while (SimpleMatch(lhs, ".")) lhs = lhs!.AstOperand1;
                if (lhs?.Variable != null && SimpleMatch(lhs.Variable.TypeStartToken, "lconv")) ReportError(token, 21, 19);
            }
        }
    }

    private void Misra_21_20(Configuration cfg)
    {
        var assigned = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var invalid = new List<int>();
        foreach (var token in cfg.Tokenlist)
        {
            // 데이터 흐름 분석 없이, 제어 흐름이 끊기면 초기화한다
            if (token.Str is "{" or "}" or "break" or "continue" or "return")
            {
                assigned.Clear();
                invalid.Clear();
                continue;
            }

            // 포인터에 새 값을 대입하면 목록에서 제거
            if (token.VarId is > 0 && SimpleMatch(token.Next, "="))
            {
                int id = token.VarId.Value;
                foreach (var list in assigned.Values) list.RemoveAll(v => v == id);
                invalid.RemoveAll(v => v == id);
                continue;
            }

            if (token.Str is "asctime" or "ctime" or "gmtime" or "localtime" or "localeconv" or "getenv" or "setlocale" or "strerror")
            {
                var (name, _) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
                if (!string.IsNullOrEmpty(name) && name == token.Str)
                {
                    // 이전에 대입된 포인터는 무효가 된다
                    if (assigned.TryGetValue(name, out var ids))
                        foreach (var varId in ids)
                            if (!invalid.Contains(varId)) invalid.Add(varId);

                    var parent = token.Next!;
                    while (parent.AstParent != null && (parent.AstParent.Str == "+" || IsCast(parent.AstParent))) parent = parent.AstParent;
                    if (SimpleMatch(parent.AstParent, "="))
                    {
                        var eq = parent.AstParent!;
                        var vartok = eq.Previous;
                        if (vartok?.VarId is > 0)
                        {
                            if (!assigned.TryGetValue(name, out var l)) assigned[name] = new List<int> { vartok.VarId.Value };
                            else if (!l.Contains(vartok.VarId.Value)) l.Add(vartok.VarId.Value);
                        }
                    }
                }
                continue;
            }

            // 무효가 된 포인터 값 사용
            if (token.AstParent != null && token.VarId is not null and not 0 && invalid.Contains(token.VarId.Value))
                ReportError(token, 21, 20);
        }
    }

    private void Misra_21_21(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "system") continue;
            var (name, args) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
            if (name == "system" && args!.Count == 1) ReportError(token, 21, 21);
        }
    }

    private void Misra_22_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.IsUnaryOp("*") || (token.IsBinaryOp() && token.Str == "."))
            {
                var fileptr = token.AstOperand1!;
                if (fileptr.Variable != null && SimpleMatch(fileptr.Variable.TypeStartToken, "FILE *")) ReportError(token, 22, 5);
            }
        }
    }

    private void Misra_22_7(Configuration cfg)
    {
        foreach (var eofToken in cfg.Tokenlist)
        {
            if (eofToken.Str != "EOF") continue;
            if (eofToken.AstParent == null || !eofToken.AstParent.IsComparisonOp) continue;
            var sibling = eofToken.AstParent.AstOperand1 == eofToken ? eofToken.AstParent.AstOperand2 : eofToken.AstParent.AstOperand1;
            while (IsCast(sibling) && sibling!.ValueType != null && !string.IsNullOrEmpty(sibling.ValueType.Type) && sibling.ValueType.Type == "int")
                sibling = sibling.AstOperand2 ?? sibling.AstOperand1;
            if (sibling?.ValueType != null && sibling.ValueType.Type is "bool" or "char" or "short")
                ReportError(eofToken, 22, 7);
        }
    }

    private void Misra_22_8(Configuration cfg)
    {
        bool isZero = false;
        foreach (var token in cfg.Tokenlist)
        {
            if (SimpleMatch(token, "errno = 0")) isZero = true;
            if (token.Str == "(" && !SimpleMatch(token.Link, ") {"))
            {
                var (name, _) = CppcheckDataFunctions.GetFunctionCallNameArgs(token.Previous);
                if (name == null) continue;
                if (IsErrnoSettingFunction(name))
                {
                    if (!isZero) ReportError(token, 22, 8);
                }
                else
                {
                    isZero = false;
                }
            }
        }
    }

    private void Misra_22_9(Configuration cfg)
    {
        bool errnoIsSet = false;
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str == "(" && !SimpleMatch(token.Link, ") {"))
            {
                var (name, _) = CppcheckDataFunctions.GetFunctionCallNameArgs(token.Previous);
                if (name == null) continue;
                errnoIsSet = IsErrnoSettingFunction(name);
            }
            if (errnoIsSet && Py.In(token.Str, "{};"))
            {
                errnoIsSet = false;
                var tok = token.Next;
                while (tok != null && tok.Str is not ("{" or "}" or ";" or "errno")) tok = tok.Next;
                if (tok == null || tok.Str != "errno") ReportError(token, 22, 9);
                else if (tok.AstParent == null || !tok.AstParent.IsComparisonOp) ReportError(token, 22, 9);
            }
        }
    }

    private void Misra_22_10(Configuration cfg)
    {
        string? lastFunctionCall = null;
        foreach (var token in cfg.Tokenlist)
        {
            if (token.IsName && token.Next != null && token.Next.Str == "(" && !SimpleMatch(token.Next.Link, ") {"))
            {
                var (name, _) = CppcheckDataFunctions.GetFunctionCallNameArgs(token);
                lastFunctionCall = name;
            }
            if (token.Str == "}") lastFunctionCall = null;
            if (token.Str == "errno" && token.AstParent != null && token.AstParent.IsComparisonOp)
            {
                if (lastFunctionCall == null) ReportError(token, 22, 10);
                else if (!IsErrnoSettingFunction(lastFunctionCall)) ReportError(token, 22, 10);
            }
        }
    }
}
