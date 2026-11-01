using System.Text.RegularExpressions;
using static JeonCheck.Services.Misra.MisraHelpers;

namespace JeonCheck.Services.Misra;

// misra.py misra_1_2 ~ misra_8_14
public sealed partial class MisraChecker
{
    private bool _ctuTagnamesDone;

    private static int NumSignificantNamingChars(Configuration cfg) => cfg.Standards.C == "c89" ? 31 : 63;

    private void Misra_1_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (SimpleMatch(token, "? :"))
                ReportError(token, 1, 2);
            else if (SimpleMatch(token, "( {") && SimpleMatch(token.Next!.Link!.Previous, "; } )"))
                ReportError(token, 1, 2);
        }
    }

    private void Misra_1_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is "_Atomic" or "_Noreturn" or "_Generic" or "_Thread_local" or "_Alignas" or "_Alignof")
                ReportError(token, 1, 4);
            if (token.Str.EndsWith("_s", StringComparison.Ordinal) && IsFunctionCall(token.Next, cfg.Standards.C) &&
                Array.IndexOf(MisraTables.MISRA_1_4_FUNCTIONS, token.Str) >= 0)
                ReportError(token, 1, 4);
        }
    }

    private void Misra_2_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.IsExpandedMacro) continue;
            if (Py.In(token.Str, "+-") && token.AstOperand2 != null)
            {
                if (SimpleMatch(token.AstOperand1, "0")) ReportError(token.AstOperand1, 2, 2);
                else if (SimpleMatch(token.AstOperand2, "0")) ReportError(token.AstOperand2, 2, 2);
            }
            if (token.Str == "*" && token.AstOperand2 != null)
            {
                if (SimpleMatch(token.AstOperand2, "0")) ReportError(token.AstOperand1, 2, 2);
                else if (SimpleMatch(token.AstOperand1, "0")) ReportError(token.AstOperand2, 2, 2);
                else if (SimpleMatch(token.AstOperand1, "1")) ReportError(token.AstOperand1, 2, 2);
                else if (SimpleMatch(token.AstOperand2, "1")) ReportError(token.AstOperand2, 2, 2);
            }
        }
    }

    /// <summary>misra_2_4 / misra_5_7: CTU 태그 이름 요약 (보고는 없지만 원본과 같은 지점에서 예외가 날 수 있다)</summary>
    private void Misra_2_4(Configuration cfg)
    {
        if (_ctuTagnamesDone) return;
        _ctuTagnamesDone = true;
        foreach (var scope in cfg.Scopes)
        {
            if (scope.ClassName == null) continue;
            if (scope.ClassName.StartsWith("Anonymous", StringComparison.Ordinal)) continue;
            if (scope.Type is not ("Struct" or "Enum")) continue;
            _ = scope.BodyStart!.File; // 원본: scope.bodyStart.file
        }
    }

    private void Misra_2_7(Configuration cfg)
    {
        foreach (var func in cfg.Functions)
        {
            if (func.Argument.Count == 0) continue;
            var funcParamList = new List<Variable>();
            foreach (var funcArg in func.Argument.Values)
            {
                if (funcArg!.TypeStartToken != null && funcArg.TypeStartToken.Str == "...") continue;
                funcParamList.Add(funcArg);
            }
            foreach (var scope in cfg.Scopes)
            {
                if (scope.Type != "Function" || scope.Function != func) continue;
                var token = scope.BodyStart!;
                while (token.Next != null && token != scope.BodyEnd && funcParamList.Count > 0)
                {
                    if (token.Variable != null)
                    {
                        int idx = funcParamList.FindIndex(v => ReferenceEquals(v, token.Variable));
                        if (idx >= 0) funcParamList.RemoveAt(idx);
                    }
                    token = token.Next;
                }
                // 원본은 줄 번호 대신 토큰 객체(또는 정수)를 집합에 넣는다
                var reported = new HashSet<object>();
                foreach (var funcParam in funcParamList)
                {
                    if (funcParam.NameToken != null)
                    {
                        if (reported.Add(funcParam.NameToken)) ReportError(funcParam.NameToken, 2, 7);
                    }
                    else
                    {
                        if (reported.Add(func.TokenDef!.Linenr)) ReportError(func.TokenDef, 2, 7);
                    }
                }
            }
        }
    }

    private static readonly Regex UriSuffix = new(@"\w+:$", RegexOptions.CultureInvariant);

    private void Misra_3_1(List<Token> rawTokens)
    {
        foreach (var token in rawTokens)
        {
            bool startsWithDoubleSlash = token.Str.StartsWith("//", StringComparison.Ordinal);
            bool startsWithBlockComment = token.Str.StartsWith("/*", StringComparison.Ordinal);
            string s = token.Str.TrimStart('/');
            if ((startsWithDoubleSlash || startsWithBlockComment) && s.Contains("/*", StringComparison.Ordinal))
            {
                ReportError(token, 3, 1);
            }
            else if (startsWithBlockComment && s.Contains("//", StringComparison.Ordinal))
            {
                while (s.Contains("//", StringComparison.Ordinal))
                {
                    int i = s.IndexOf("//", StringComparison.Ordinal);
                    string possibleUri = s[..i];
                    s = s[(i + 2)..];
                    if (!UriSuffix.IsMatch(possibleUri))
                    {
                        ReportError(token, 3, 1);
                        break;
                    }
                }
            }
        }
    }

    private void Misra_4_1(List<Token> rawTokens)
    {
        foreach (var token in rawTokens)
        {
            if (token.Str[0] != '"' && token.Str[0] != '\'') continue;
            if (token.Str.Length < 3) continue;
            char delimiter = token.Str[0];
            string symbols = token.Str[1..^1];
            if (token.Str[^1] != delimiter) continue;
            if (symbols.Length < 2) continue;
            if (!HasNumericEscapeSequence(symbols)) continue;
            foreach (var t in symbols.Split('\\').Skip(1))
            {
                string sequence = "\\" + t;
                if (IsHexEscapeSequence(sequence) || IsOctalEscapeSequence(sequence) || IsSimpleEscapeSequence(sequence))
                    continue;
                ReportError(token, 4, 1);
            }
        }
    }

    private static readonly string[] Trigraphs = { "??=", "??(", "??/", "??)", "??'", "??<", "??!", "??>", "??-" };

    private void Misra_4_2(List<Token> rawTokens)
    {
        foreach (var token in rawTokens)
        {
            if (token.Str[0] != '"' || token.Str[^1] != '"') continue;
            string inner = token.Str.Length >= 2 ? token.Str[1..^1] : "";
            foreach (var sequence in Trigraphs)
            {
                if (inner.Contains(sequence, StringComparison.Ordinal))
                {
                    ReportError(token, 4, 2);
                    break;
                }
            }
        }
    }

    private void Misra_5_1(Configuration cfg)
    {
        var longVars = new Dictionary<string, List<Token>>(StringComparer.Ordinal);
        int numSignChars = NumSignificantNamingChars(cfg);
        foreach (var var in cfg.Variables)
        {
            if (var.NameToken == null) continue;
            if (var.NameToken.Str.Length <= numSignChars) continue;
            if (!HasExternalLinkage(var)) continue;
            string key = Py.Prefix(var.NameToken.Str, numSignChars);
            if (!longVars.TryGetValue(key, out var list)) longVars[key] = list = new List<Token>();
            list.Add(var.NameToken);
        }
        foreach (var tokens in longVars.Values)
        {
            if (tokens.Count < 2) continue;
            foreach (var tok in tokens.OrderBy(t => t.Linenr).ThenBy(t => t.Column).Skip(1))
                ReportError(tok, 5, 1);
        }
    }

    private sealed class ScopeVars
    {
        public List<Variable> Varlist { get; } = new();
        public List<Scope> Scopelist { get; } = new();
    }

    private static readonly object NullScopeKey = new();

    private void Misra_5_2(Configuration cfg)
    {
        var scopeVars = new Dictionary<object, ScopeVars>(ReferenceEqualityComparer.Instance);
        var order = new List<object>();
        ScopeVars Get(Scope? s)
        {
            object key = (object?)s ?? NullScopeKey;
            if (!scopeVars.TryGetValue(key, out var sv))
            {
                scopeVars[key] = sv = new ScopeVars();
                order.Add(key);
            }
            return sv;
        }

        int n = NumSignificantNamingChars(cfg);
        foreach (var var in cfg.Variables)
        {
            if (var.NameToken == null) continue;
            if (var.NameToken.Str.Length <= n) continue;
            Get(var.NameToken.Scope).Varlist.Add(var);
        }
        foreach (var scope in cfg.Scopes)
        {
            if (scope.NestedIn != null && !string.IsNullOrEmpty(scope.ClassName))
                Get(scope.NestedIn).Scopelist.Add(scope);
        }
        foreach (var key in order)
        {
            var sv = scopeVars[key];
            if (sv.Varlist.Count <= 1) continue;
            for (int i = 0; i < sv.Varlist.Count; i++)
            {
                var variable1 = sv.Varlist[i];
                for (int j = i + 1; j < sv.Varlist.Count; j++)
                {
                    var variable2 = sv.Varlist[j];
                    if (variable1.IsArgument && variable2.IsArgument) continue;
                    if (HasExternalLinkage(variable1) || HasExternalLinkage(variable2)) continue;
                    if (Py.Prefix(variable1.NameToken!.Str, n) == Py.Prefix(variable2.NameToken!.Str, n) &&
                        !ReferenceEquals(variable1, variable2))
                    {
                        if (variable1.NameToken.Linenr > variable2.NameToken.Linenr) ReportError(variable1.NameToken, 5, 2);
                        else ReportError(variable2.NameToken, 5, 2);
                    }
                }
                foreach (var innerscope in sv.Scopelist)
                {
                    if (Py.Prefix(variable1.NameToken!.Str, n) == Py.Prefix(innerscope.ClassName!, n))
                    {
                        if (variable1.NameToken.Linenr > innerscope.BodyStart!.Linenr) ReportError(variable1.NameToken, 5, 2);
                        else ReportError(innerscope.BodyStart, 5, 2);
                    }
                }
            }
            if (sv.Scopelist.Count <= 1) continue;
            for (int i = 0; i < sv.Scopelist.Count; i++)
            {
                for (int j = i + 1; j < sv.Scopelist.Count; j++)
                {
                    var s1 = sv.Scopelist[i];
                    var s2 = sv.Scopelist[j];
                    if (Py.Prefix(s1.ClassName!, n) == Py.Prefix(s2.ClassName!, n))
                    {
                        if (s1.BodyStart!.Linenr > s2.BodyStart!.Linenr) ReportError(s1.BodyStart, 5, 2);
                        else ReportError(s2.BodyStart, 5, 2);
                    }
                }
            }
        }
    }

    private static readonly Regex DefineName = new(@"^#define ([a-zA-Z0-9_]+)", RegexOptions.CultureInvariant);
    private static readonly Regex DefineParams = new(@"^#define ([a-zA-Z0-9_]+)[(]([a-zA-Z0-9_, ]+)[)]", RegexOptions.CultureInvariant);

    private sealed class MacroInfo
    {
        public string Name { get; set; } = "";
        public List<string> Params { get; } = new();
    }

    private void Misra_5_4(Configuration cfg)
    {
        int n = NumSignificantNamingChars(cfg);
        var macro = new Dictionary<Directive, MacroInfo>(ReferenceEqualityComparer.Instance);
        var shortNames = new Dictionary<string, Directive>(StringComparer.Ordinal);
        var macroWArg = new List<Directive>();
        foreach (var dir in cfg.Directives)
        {
            var res1 = DefineName.Match(dir.Str);
            if (res1.Success)
            {
                if (!macro.ContainsKey(dir)) macro[dir] = new MacroInfo();
                string fullName = res1.Groups[1].Value;
                macro[dir].Name = fullName;
                string shortName = Py.Prefix(fullName, n);
                if (shortNames.TryGetValue(shortName, out var other))
                {
                    if (fullName != macro[other].Name) ReportError(dir, 5, 4);
                }
                else
                {
                    shortNames[shortName] = dir;
                }
            }
            var res2 = DefineParams.Match(dir.Str);
            if (res2.Success)
            {
                macro[dir].Params.AddRange(res2.Groups[2].Value.Split(',').Select(p => p.Replace(" ", "")));
                macroWArg.Add(dir);
            }
        }
        foreach (var mvar in macroWArg)
        {
            var ps = macro[mvar].Params;
            for (int i = 0; i < ps.Count; i++)
            {
                for (int j = 0; j < ps.Count; j++)
                    if (j > i && Py.Prefix(ps[i], n) == Py.Prefix(ps[j], n)) ReportError(mvar, 5, 4);
                string param = ps[i];
                if (shortNames.TryGetValue(Py.Prefix(param, n), out var mVar1))
                {
                    if (mVar1.Linenr > mvar.Linenr) ReportError(mVar1, 5, 4);
                    else ReportError(mvar, 5, 4);
                }
            }
        }
    }

    private void Misra_5_5(Configuration cfg)
    {
        int n = NumSignificantNamingChars(cfg);
        var macroNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in cfg.Directives)
        {
            var res = DefineName.Match(dir.Str);
            if (res.Success) macroNames.Add(Py.Prefix(res.Groups[1].Value, n));
        }
        foreach (var var in cfg.Variables)
            if (var.NameToken != null && macroNames.Contains(Py.Prefix(var.NameToken.Str, n)))
                ReportError(var.NameToken, 5, 5);
        foreach (var scope in cfg.Scopes)
            if (!string.IsNullOrEmpty(scope.ClassName) && macroNames.Contains(Py.Prefix(scope.ClassName, n)))
                ReportError(scope.BodyStart, 5, 5);
    }

    private void Misra_6_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.ValueType == null) continue;
            if (token.ValueType.Bits == null) continue;
            if (token.Variable == null) continue;
            if (token.Scope == null) continue;
            if (!Py.In(token.Scope.Type, "Struct")) continue;

            if (cfg.Standards.C == "c89")
            {
                if (token.ValueType.Type != "int" && !IsUnsignedType(token.Variable.TypeStartToken!.Str))
                    ReportError(token, 6, 1);
            }
            else if (cfg.Standards.C is "c99" or "c11" or "c17" or "c18")
            {
                if (token.ValueType.Type == "bool") continue;
            }

            bool isExplicitlySignedOrUnsigned = false;
            var typeToken = token.Variable.TypeStartToken;
            while (typeToken != null)
            {
                if (typeToken.IsUnsigned || typeToken.IsSigned || IsUnsignedType(typeToken.Str))
                {
                    isExplicitlySignedOrUnsigned = true;
                    break;
                }
                if (typeToken == token.Variable.TypeEndToken) break;
                typeToken = typeToken.Next;
            }
            if (!isExplicitlySignedOrUnsigned) ReportError(token, 6, 1);
        }
    }

    private void Misra_6_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.ValueType == null) continue;
            if (token.Scope == null) continue;
            if (!Py.In(token.Scope.Type, "Struct")) continue;
            if (token.ValueType.Bits == 1 && token.ValueType.Sign == "signed") ReportError(token, 6, 2);
        }
    }

    private static readonly Regex OctalConstant = new(@"^0[0-7]+$", RegexOptions.CultureInvariant);
    private static readonly Regex LowercaseLSuffix = new(@"^(0[xX])?[0-9a-fA-FpP.]+[Uu]*l+[Uu]*$", RegexOptions.CultureInvariant);

    private void Misra_7_1(List<Token> rawTokens)
    {
        foreach (var tok in rawTokens)
            if (OctalConstant.IsMatch(tok.Str)) ReportError(tok, 7, 1);
    }

    private void Misra_7_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.IsInt && !token.Str.ToUpperInvariant().Contains('U') && token.ValueType != null && token.ValueType.Sign == "unsigned")
                ReportError(token, 7, 2);
    }

    private void Misra_7_3(List<Token> rawTokens)
    {
        foreach (var tok in rawTokens)
            if (LowercaseLSuffix.IsMatch(tok.Str)) ReportError(tok, 7, 3);
    }

    private static readonly string[] ConstCharPtr = { "const", "char", "*" };

    private void Misra_7_4(Configuration cfg)
    {
        void ReportErrorIfVariableIsNotConst(Token variable, Token stringLiteral)
        {
            if (variable.ValueType != null && variable.ValueType.Constness % 2 != 1)
                ReportError(stringLiteral, 7, 4);
        }

        foreach (var token in cfg.Tokenlist)
        {
            if (token.IsString)
            {
                var variable = GetAssignedVariableToken(token);
                if (variable != null) ReportErrorIfVariableIsNotConst(variable, token);

                var function = GetFunctionUsingReturnValue(token);
                if (function != null && !TokenFollowsSequence(function.TokenDef, ConstCharPtr))
                    ReportError(token, 7, 4);
            }

            if (IsFunctionCall(token, cfg.Standards.C) && token.AstOperand1?.Function != null)
            {
                var decl = token.AstOperand1.Function;
                if (decl.TokenDef != null)
                {
                    if (decl.TokenDef == token.AstOperand1) continue;
                    var parametersUsed = GetArguments(token);
                    for (int i = 0; i < parametersUsed.Count; i++)
                    {
                        var used = parametersUsed[i];
                        decl.Argument.TryGetValue(i + 1, out var def);
                        if (used.IsString && def?.NameToken != null)
                            ReportErrorIfVariableIsNotConst(def.NameToken, used);
                    }
                }
            }
        }
    }

    private void Misra_8_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.IsImplicitInt && !token.IsUnsigned && !token.IsSigned) ReportError(token, 8, 1);
    }

    private void Misra_8_2(Configuration cfg, List<Token> rawTokens)
    {
        // 원본은 매번 rawTokens 전체를 훑는다. 같은 결과를 위치 색인으로 빠르게 찾는다.
        var rawIndex = new Dictionary<(string?, int, int), List<Token>>();
        foreach (var rt in rawTokens)
        {
            var key = (rt.File, rt.Linenr, rt.Column);
            if (!rawIndex.TryGetValue(key, out var l)) rawIndex[key] = l = new List<Token>();
            l.Add(rt);
        }

        List<Token> GetFollowingRawTokens(Token token, int count)
        {
            var following = new List<Token>();
            if (!rawIndex.TryGetValue((token.File, token.Linenr, token.Column), out var matches)) return following;
            foreach (var match in matches)
            {
                Token? rawToken = match;
                for (int k = 0; k < count; k++)
                {
                    rawToken = rawToken!.Next;
                    while (rawToken != null && (rawToken.Str.StartsWith("/*", StringComparison.Ordinal) ||
                                                rawToken.Str.StartsWith("//", StringComparison.Ordinal)))
                        rawToken = rawToken.Next;
                    if (rawToken == null) break;
                    following.Add(rawToken);
                }
            }
            return following;
        }

        void CheckZeroArguments(Function func, Token startCall)
        {
            if (!startCall.IsRemovedVoidParameter && func.Argument.Count == 0)
            {
                if (func.TokenDef!.Next != null) ReportError(func.TokenDef.Next, 8, 2);
                else ReportError(func.TokenDef, 8, 2);
            }
        }

        void CheckDeclarationArgumentsViolations(Function func, Token? startCall, Token endCall)
        {
            var argNameTokens = new HashSet<Token>(ReferenceEqualityComparer.Instance);
            foreach (var argument in func.Argument.Values)
            {
                var typeStartToken = argument!.TypeStartToken;
                if (typeStartToken == null) continue;
                if (argument.NameToken == null) continue;
                argNameTokens.Add(argument.NameToken);
            }
            int foundVariables = 0;
            while (startCall != null && startCall != endCall)
            {
                if (startCall.VarId is not null and not 0) foundVariables++;
                startCall = startCall.Next;
            }
            if (argNameTokens.Count != foundVariables)
            {
                if (func.TokenDef!.Next != null) ReportError(func.TokenDef.Next, 8, 2);
                else ReportError(func.TokenDef, 8, 2);
            }
        }

        void CheckDefinitionArgumentsViolations(Function func, Token endCall)
        {
            foreach (var argument in func.Argument.Values)
            {
                var typeStartToken = argument!.TypeStartToken;
                if (typeStartToken == null) continue;
                if (argument.NameToken == null && typeStartToken.Str != "...") ReportError(typeStartToken, 8, 2);
                if (typeStartToken.Linenr > endCall.Linenr) ReportError(typeStartToken, 8, 2);
            }
        }

        foreach (var func in cfg.Functions)
        {
            var tokenImpl = func.Token;
            if (tokenImpl != null)
            {
                var startCall = tokenImpl.Next;
                if (startCall == null || startCall.Str != "(") continue;
                var endCall = startCall.Link;
                if (endCall == null || endCall.Str != ")") continue;
                CheckZeroArguments(func, startCall);
                CheckDefinitionArgumentsViolations(func, endCall);
            }
            var tokenDef = func.TokenDef;
            if (tokenDef != null)
            {
                var startCall = tokenDef.Next;
                if (startCall == null || startCall.Str != "(") continue;
                var endCall = startCall.Link;
                if (endCall == null || endCall.Str != ")") continue;
                CheckZeroArguments(func, startCall);
                if (tokenImpl != null) CheckDeclarationArgumentsViolations(func, startCall, endCall);
                else CheckDefinitionArgumentsViolations(func, endCall);
            }
        }

        foreach (var var in cfg.Variables)
        {
            if (!var.IsPointer) continue;
            if (var.NameToken == null) continue;
            var following = GetFollowingRawTokens(var.NameToken, 3);
            if (following.Count != 3) continue;
            if (following[0].Str == ")" && following[1].Str == "(" && following[2].Str == ")")
                ReportError(var.NameToken, 8, 2);
        }
    }

    private void Misra_8_4(Configuration cfg)
    {
        foreach (var func in cfg.Functions)
        {
            if (func.IsStatic) continue;
            if (func.Token == null) continue;
            if (!IsSourceFile(func.Token.File)) continue;
            if (func.Token != func.TokenDef) continue;
            if (HasPriorFunctionPrototype(cfg, func)) continue;
            if (func.TokenDef!.Str == "main") continue;
            ReportError(func.TokenDef, 8, 4);
        }

        var withDef = new Dictionary<string, List<Token>>(StringComparer.Ordinal);
        var withoutDef = new Dictionary<string, List<Token>>(StringComparer.Ordinal);
        var withoutDefOrder = new List<string>();
        void Insert(Dictionary<string, List<Token>> d, string key, Token value, List<string>? order = null)
        {
            if (!d.TryGetValue(key, out var l))
            {
                d[key] = l = new List<Token>();
                order?.Add(key);
            }
            l.Add(value);
        }

        foreach (var var in cfg.Variables)
        {
            if (!var.IsGlobal) continue;
            if (var.IsStatic) continue;
            if (var.NameToken == null) continue;
            var tok = var.NameToken;
            if (tok.Next!.Str == ";")
            {
                if (tok.Next.IsSplittedVarDeclEq) Insert(withDef, tok.Str, tok);
                else Insert(withoutDef, tok.Str, tok, withoutDefOrder);
            }
            else
            {
                Insert(withoutDef, var.NameToken.Str, var.NameToken, withoutDefOrder);
            }
        }

        foreach (var (name, toks) in withDef)
            if (!withoutDef.ContainsKey(name))
                foreach (var t in toks) ReportError(t, 8, 4);

        foreach (var name in withoutDefOrder)
        {
            if (withDef.ContainsKey(name)) continue;
            var toks = withoutDef[name];
            bool warn = true;
            foreach (var t in toks)
            {
                if (t.Variable!.IsExtern)
                {
                    warn = false;
                    break;
                }
            }
            if (warn)
                foreach (var t in toks) ReportError(t, 8, 4);
        }
    }

    private void Misra_8_8(Configuration cfg)
    {
        var vars = new Dictionary<string, List<Variable>>(StringComparer.Ordinal);
        foreach (var var in cfg.Variables)
        {
            if (var.Access != "Global") continue;
            if (var.NameToken == null) continue;
            if (!vars.TryGetValue(var.NameToken.Str, out var l)) vars[var.NameToken.Str] = l = new List<Variable>();
            l.Add(var);
        }
        foreach (var varlist in vars.Values)
        {
            Variable? staticVar = null, externVar = null;
            foreach (var var in varlist)
            {
                if (var.IsStatic) staticVar = var;
                else if (var.IsExtern) externVar = var;
            }
            if (staticVar != null && externVar != null) ReportError(externVar.NameToken, 8, 8);
        }
    }

    private void Misra_8_9(Configuration cfg)
    {
        var variables = new Dictionary<Variable, int>(ReferenceEqualityComparer.Instance);
        var order = new List<Variable>();
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Function") continue;
            var used = new List<Variable>();
            var tok = scope.BodyStart;
            while (tok != scope.BodyEnd)
            {
                if (tok!.Variable != null && tok.Variable.Access == "Global" && tok.Variable.IsStatic &&
                    !used.Any(v => ReferenceEquals(v, tok.Variable)))
                    used.Add(tok.Variable);
                tok = tok.Next;
            }
            foreach (var var in used)
            {
                if (variables.TryGetValue(var, out int c)) variables[var] = c + 1;
                else
                {
                    variables[var] = 1;
                    order.Add(var);
                }
            }
        }
        foreach (var var in order)
            if (variables[var] == 1) ReportError(var.NameToken, 8, 9);
    }

    private void Misra_8_10(Configuration cfg)
    {
        foreach (var func in cfg.Functions)
            if (func.IsInlineKeyword && !func.IsStatic) ReportError(func.TokenDef, 8, 10);
    }

    private void Misra_8_11(Configuration cfg)
    {
        foreach (var var in cfg.Variables)
            if (var.IsExtern && SimpleMatch(var.NameToken!.Next, "[ ]") && var.NameToken.Scope!.Type == "Global")
                ReportError(var.NameToken, 8, 11);
    }

    private void Misra_8_12(Configuration cfg)
    {
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Enum") continue;
            var enumValues = new List<long?>();
            var implicitEnumValues = new List<long?>();
            var eToken = scope.BodyStart!.Next;
            while (eToken != scope.BodyEnd)
            {
                if (eToken!.Str == "(")
                {
                    eToken = eToken.Link;
                    continue;
                }
                if (!Py.In(eToken.Previous!.Str, ",{"))
                {
                    eToken = eToken.Next;
                    continue;
                }
                if (eToken.IsName && eToken.Values is { Count: > 0 } && eToken.ValueType != null && eToken.ValueType.TypeScope == scope)
                {
                    var tokenValues = eToken.Values.Select(v => v.IntValue).ToList();
                    enumValues.AddRange(tokenValues);
                    if (eToken.Next!.Str != "=") implicitEnumValues.AddRange(tokenValues);
                }
                eToken = eToken.Next;
            }
            foreach (var value in implicitEnumValues)
                if (enumValues.Count(v => v == value) != 1) ReportError(scope.BodyStart, 8, 12);
        }
    }

    private void Misra_8_14(List<Token> rawTokens)
    {
        foreach (var token in rawTokens)
            if (token.Str == "restrict") ReportError(token, 8, 14);
    }
}
