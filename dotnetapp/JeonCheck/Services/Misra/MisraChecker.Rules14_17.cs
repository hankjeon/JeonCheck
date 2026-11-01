using static JeonCheck.Services.Misra.MisraHelpers;

namespace JeonCheck.Services.Misra;

// misra.py misra_14_1 ~ misra_17_8, misra_config
public sealed partial class MisraChecker
{
    private void Misra_14_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str == "for")
            {
                var exprs = GetForLoopExpressions(token);
                if (exprs == null) continue;
                foreach (var counter in FindCounterTokens(exprs[1]))
                    if (counter.ValueType != null && counter.ValueType.IsFloat()) ReportError(token, 14, 1);
            }
            else if (token.Str == "while")
            {
                if (IsFloatCounterInWhileLoop(token)) ReportError(token, 14, 1);
            }
        }
    }

    private void Misra_14_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "for") continue;
            var expressions = GetForLoopExpressions(token);
            if (expressions == null) continue;
            if (expressions[0] != null && !expressions[0]!.IsAssignmentOp)
            {
                if (expressions[0]!.Str != "(" || !expressions[0]!.Previous!.IsName) ReportError(token, 14, 2);
            }
            if (CountSideEffectsRecursive(expressions[1]) > 0) ReportError(token, 14, 2);
            if (CountSideEffectsRecursive(expressions[2]) > 1) ReportError(token, 14, 2);

            var counters = GetForLoopCounterVariables(token, cfg)
                ?? throw new InvalidOperationException("cannot unpack non-iterable NoneType object");
            var (firstClause, exitModified) = counters;
            if (exitModified.Count == 0)
            {
                // 루프 카운터를 알 수 없으면 세 절이 모두 비어 있어야 한다
                foreach (var e in expressions)
                {
                    if (e != null)
                    {
                        ReportError(token, 14, 2);
                        break;
                    }
                }
            }
            else if (exitModified.Count > 1)
            {
                ReportError(token, 14, 2);
            }
            else
            {
                var loopCounter = exitModified.First();
                if (expressions[0] != null && !firstClause.Contains(loopCounter)) ReportError(token, 14, 2);

                // 루프 본문에서 카운터 변경 검사
                var bodyScope = token.Next!.Link!.Next!.Scope;
                if (bodyScope == null) continue;
                var tn = bodyScope.BodyStart;
                while (tn != null && tn != bodyScope.BodyEnd)
                {
                    if (tn.Variable == loopCounter && tn.Next != null && CountSideEffectsRecursive(tn.Next) > 0)
                        ReportError(tn, 14, 2);
                    tn = tn.Next;
                }
            }
        }
    }

    private void Misra_14_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "(") continue;
            if (token.AstOperand1 == null || token.AstOperand1.Str is not ("if" or "while")) continue;
            if (IsBoolExpression(token.AstOperand2)) continue;
            if (token.AstOperand2!.ValueType != null) ReportError(token, 14, 4);
        }
    }

    private void Misra_15_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "goto") ReportError(token, 15, 1);
    }

    private void Misra_15_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "goto") continue;
            if (token.Next == null || !token.Next.IsName) continue;
            if (FindGotoLabel(token) == null) ReportError(token, 15, 2);
        }
    }

    private void Misra_15_3(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "goto") continue;
            if (token.Next == null || !token.Next.IsName) continue;
            var tok = FindGotoLabel(token);
            if (tok == null) continue;
            var scope = token.Scope;
            while (scope != null && scope != tok.Scope) scope = scope.NestedIn;
            if (scope == null)
            {
                ReportError(token, 15, 3);
            }
            else if (scope.Type == "Switch")
            {
                // 다른 switch 절의 레이블로 건너뛰는 것도 위반
                var tcaseStart = token;
                while (tcaseStart != null && tcaseStart.Str is not ("case" or "default")) tcaseStart = tcaseStart.Previous;
                if (tcaseStart != null)
                {
                    var t = scope.BodyStart;
                    bool inThisCase = false;
                    while (t != null && t != scope.BodyEnd)
                    {
                        if (t == tcaseStart) inThisCase = true;
                        if (inThisCase && t.Str is not ("case" or "default")) inThisCase = false;
                        if (t == tok && !inThisCase)
                        {
                            ReportError(token, 15, 3);
                            break;
                        }
                        t = t.Next;
                    }
                }
            }
        }
    }

    private void Misra_15_4(Configuration cfg)
    {
        static void GetLoopsAffectedByBreak(List<Scope> knownLoops, Scope? scope, bool isGoto)
        {
            if (scope == null || string.IsNullOrEmpty(scope.Type) || scope.Type is "Global" or "Function") return;
            if (!isGoto && scope.Type == "Switch") return;
            if (scope.Type is "For" or "While" or "Do")
            {
                knownLoops.Add(scope);
                if (!isGoto) return;
            }
            GetLoopsAffectedByBreak(knownLoops, scope.NestedIn, isGoto);
        }

        var loopWithBreaks = new Dictionary<Scope, int>(ReferenceEqualityComparer.Instance);
        var order = new List<Scope>();
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("break" or "goto")) continue;
            var affected = new List<Scope>();
            GetLoopsAffectedByBreak(affected, token.Scope, token.Str == "goto");
            foreach (var scope in affected)
            {
                if (loopWithBreaks.TryGetValue(scope, out int c)) loopWithBreaks[scope] = c + 1;
                else
                {
                    loopWithBreaks[scope] = 1;
                    order.Add(scope);
                }
            }
        }
        foreach (var scope in order)
            if (loopWithBreaks[scope] > 1) ReportError(scope.BodyStart, 15, 4);
    }

    private void Misra_15_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "return" && token.Scope!.Type != "Function") ReportError(token, 15, 5);
    }

    private static bool IsComment(Token t) =>
        t.Str.StartsWith("//", StringComparison.Ordinal) || t.Str.StartsWith("/*", StringComparison.Ordinal);

    private void Misra_15_6(List<Token> rawTokens)
    {
        int state = 0, indent = 0;
        Token? tok1 = null;

        static Token? TokAt(Token? tok, int i)
        {
            while (i < 0 && tok != null)
            {
                tok = tok.Previous;
                if (IsComment(tok!)) continue;
                i++;
            }
            while (i > 0 && tok != null)
            {
                tok = tok.Next;
                if (IsComment(tok!)) continue;
                i--;
            }
            return tok;
        }

        static string StrTokens(Token tok0, int i1, int i2)
        {
            var t1 = TokAt(tok0, i1);
            var t2 = TokAt(tok0, i2);
            var tok = t1;
            string s = "";
            while (tok != t2)
            {
                if (IsComment(tok!))
                {
                    tok = tok!.Next;
                    continue;
                }
                s += " " + tok!.Str;
                tok = tok.Next;
            }
            s += " " + tok!.Str;
            return s[1..];
        }

        foreach (var token in rawTokens)
        {
            if (token.Str is "if" or "for" or "while")
            {
                if (StrTokens(token, -1, 0) == "# if") continue;
                if (StrTokens(token, -1, 0) == "} while")
                {
                    // do { .. } while 인가?
                    var start = RawLink(TokAt(token, -1));
                    if (start != null && StrTokens(start, -1, 0) == "do {") continue;
                }
                if (state == 2) ReportError(tok1, 15, 6);
                state = 1;
                indent = 0;
                tok1 = token;
            }
            else if (token.Str == "else")
            {
                if (StrTokens(token, -1, 0) == "# else") continue;
                if (StrTokens(token, 0, 1) == "else if") continue;
                if (state == 2) ReportError(tok1, 15, 6);
                state = 2;
                indent = 0;
                tok1 = token;
            }
            else if (state == 1)
            {
                if (indent == 0 && token.Str != "(")
                {
                    state = 0;
                    continue;
                }
                if (token.Str == "(")
                {
                    indent++;
                }
                else if (token.Str == ")")
                {
                    if (indent == 0) state = 0;
                    else if (indent == 1) state = 2;
                    indent--;
                }
            }
            else if (state == 2)
            {
                if (IsComment(token)) continue;
                state = 0;
                if (token.Str is not ("{" or "#")) ReportError(tok1, 15, 6);
            }
        }
    }

    private void Misra_15_7(Configuration cfg)
    {
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Else") continue;
            if (!SimpleMatch(scope.BodyStart, "{ if (")) continue;
            if (scope.BodyStart!.Column > 0) continue;
            var tok = scope.BodyStart.Next!.Next!.Link;
            if (!SimpleMatch(tok, ") {")) continue;
            tok = tok!.Next!.Link;
            if (!SimpleMatch(tok, "} else")) ReportError(tok, 15, 7);
        }
    }

    private void Misra_16_1(Configuration cfg)
    {
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Switch") continue;
            bool inCaseOrDefault = false;
            var tok = scope.BodyStart!.Next;
            while (tok != scope.BodyEnd)
            {
                if (!inCaseOrDefault)
                {
                    if (tok!.Str is not ("case" or "default")) ReportError(tok, 16, 1);
                    else inCaseOrDefault = true;
                }
                else if (SimpleMatch(tok, "break ;"))
                {
                    inCaseOrDefault = false;
                    tok = tok!.Next;
                }
                if (tok!.Str == "{")
                {
                    tok = tok.Link!;
                    if (tok.Scope!.Type == "Unconditional" && SimpleMatch(tok.Previous!.Previous, "break ;"))
                        inCaseOrDefault = false;
                }
                tok = tok.Next;
            }
        }
    }

    private void Misra_16_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "case" && token.Scope!.Type != "Switch") ReportError(token, 16, 2);
    }

    private void Misra_16_3(List<Token> rawTokens)
    {
        const int StateNone = 0, StateBreak = 1, StateOk = 2, StateSwitch = 3;
        Token? directive = null;
        int state = StateNone;
        Token? endSwitchToken = null;
        foreach (var token in rawTokens)
        {
            if (SimpleMatch(token, "# define") || SimpleMatch(token, "# pragma")) directive = token;
            if (directive != null)
            {
                if (token.Linenr != directive.Linenr) directive = null;
                else continue;
            }

            if (token.Str == "switch") state = StateSwitch;
            if (state == StateSwitch)
            {
                if (token.Str == "{") endSwitchToken = FindRawLink(token);
                else continue;
            }

            if (token.Str is "break" or "return" or "throw")
            {
                state = StateBreak;
            }
            else if (token.Str == ";")
            {
                if (state == StateBreak) state = StateOk;
                else if (token.Next != null && token.Next == endSwitchToken) ReportError(token.Next, 16, 3);
                else state = StateNone;
            }
            else if (IsComment(token))
            {
                if (token.Str.ToLowerInvariant().Contains("fallthrough", StringComparison.Ordinal)) state = StateOk;
            }
            else if (SimpleMatch(token, "[ [ fallthrough ] ] ;"))
            {
                state = StateBreak;
            }
            else if (token.Str == "{")
            {
                state = StateOk;
            }
            else if (token.Str == "}" && state == StateOk)
            {
                // 무조건 실행되는 {} 블록인가?
                var prev = FindRawLink(token);
                if (prev != null)
                {
                    prev = prev.Previous;
                    while (prev != null && Py.Prefix(prev.Str, 2) is "//" or "/*") prev = prev.Previous;
                }
                if (prev == null || !Py.In(prev.Str, ":;{}")) state = StateNone;
            }
            else if (token.Str is "case" or "default")
            {
                if (state != StateOk) ReportError(token, 16, 3);
                state = StateOk;
            }
        }
    }

    private void Misra_16_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "switch") continue;
            if (!SimpleMatch(token, "switch (")) continue;
            if (!SimpleMatch(token.Next!.Link, ") {")) continue;
            var startTok = token.Next.Link!.Next!;
            var tok = startTok.Next;
            while (tok != null && tok.Str != "}")
            {
                if (tok.Str == "{") tok = tok.Link;
                else if (tok.Str == "default") break;
                tok = tok!.Next;
            }
            if (tok != null && tok.Str != "default") ReportError(token, 16, 4);
        }
    }

    private void Misra_16_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "default") continue;
            if (token.Previous != null && token.Previous.Str == "{") continue;
            Token? tok2 = token;
            while (tok2 != null)
            {
                if (tok2.Str is "}" or "case") break;
                if (tok2.Str == "{") tok2 = tok2.Link;
                tok2 = tok2!.Next;
            }
            if (tok2 != null && tok2.Str == "case") ReportError(token, 16, 5);
        }
    }

    private void Misra_16_6(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!(SimpleMatch(token, "switch (") && SimpleMatch(token.Next!.Link, ") {"))) continue;
            var tok = token.Next.Link!.Next!.Next;
            int count = 0;
            while (tok != null)
            {
                if (tok.Str is "break" or "return" or "throw")
                {
                    count++;
                }
                else if (tok.Str == "{")
                {
                    tok = tok.Link;
                    if (IsNoReturnScope(tok)) count++;
                }
                else if (tok.Str == "}")
                {
                    break;
                }
                tok = tok!.Next;
            }
            if (count < 2) ReportError(token, 16, 6);
        }
    }

    private void Misra_16_7(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (SimpleMatch(token, "switch (") && IsBoolExpression(token.Next!.AstOperand2)) ReportError(token, 16, 7);
    }

    private void Misra_17_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (IsFunctionCall(token, cfg.Standards.C) && token.AstOperand1!.Str is "va_list" or "va_arg" or "va_start" or "va_end" or "va_copy")
                ReportError(token, 17, 1);
            else if (token.Str == "va_list")
                ReportError(token, 17, 1);
        }
    }

    private void Misra_17_2(Configuration cfg)
    {
        static bool FindRecursiveCall(Function? searchFor, Function? directCall, Dictionary<object, List<Function>> callsMap, HashSet<Function>? visited = null)
        {
            visited ??= new HashSet<Function>(ReferenceEqualityComparer.Instance);
            if (directCall == searchFor) return true;
            if (directCall != null && callsMap.TryGetValue(directCall, out var calls))
            {
                foreach (var indirect in calls)
                {
                    if (indirect == searchFor) return true;
                    if (!visited.Add(indirect)) continue;
                    if (FindRecursiveCall(searchFor, indirect, callsMap, visited)) return true;
                }
            }
            return false;
        }

        // 함수별 호출 목록 (원본 dict: 키는 scope.function, None 키도 가능)
        var functionCalls = new Dictionary<object, List<Function>>(ReferenceEqualityComparer.Instance);
        var order = new List<object>();
        foreach (var scope in cfg.Scopes)
        {
            if (scope.Type != "Function") continue;
            var calls = new List<Function>();
            var tok = scope.BodyStart;
            while (tok != scope.BodyEnd)
            {
                tok = tok!.Next;
                if (!IsFunctionCall(tok, cfg.Standards.C)) continue;
                var f = tok!.AstOperand1!.Function;
                if (f != null && !calls.Contains(f)) calls.Add(f);
            }
            object key = (object?)scope.Function ?? NullScopeKey;
            if (!functionCalls.ContainsKey(key)) order.Add(key);
            functionCalls[key] = calls;
        }

        foreach (var key in order)
        {
            var func = key as Function;
            foreach (var call in functionCalls[key])
            {
                if (!FindRecursiveCall(func, call, functionCalls)) continue;
                foreach (var scope in cfg.Scopes)
                {
                    if (scope.Type != "Function" || scope.Function != func) continue;
                    var tok = scope.BodyStart;
                    while (tok != scope.BodyEnd)
                    {
                        if (tok!.Function != null && tok.Function == call) ReportError(tok, 17, 2);
                        tok = tok.Next;
                    }
                }
            }
        }
    }

    private void Misra_17_3(Configuration cfg)
    {
        foreach (var (location, message) in cfg.ClangWarnings)
            if (message.EndsWith("[-Wimplicit-function-declaration]", StringComparison.Ordinal)) ReportError(location, 17, 3);

        foreach (var token in cfg.Tokenlist)
        {
            if (!(token.IsName && token.Scope!.Type != "Global" && token.Function == null && token.ValueType == null)) continue;
            if (token.Next != null && token.Next.Str == "(" && token.Next.ValueType == null && IsFunctionCall(token.Next, cfg.Standards.C))
            {
                var n2 = token.Next.Next!;
                if (n2.Str == "*" && n2.Next!.IsName && n2.Next.ValueType != null && n2.Next.ValueType.Pointer > 0)
                    continue; // 함수 포인터 정의: int16_t ( * p ) ()
                if (!IsKeyword(token.Str, cfg.Standards.C) && !IsStdLibId(token.Str, cfg.Standards.C))
                    ReportError(token, 17, 3);
            }
        }
    }

    private void MisraConfig(Configuration cfg)
    {
        foreach (var var in cfg.Variables)
        {
            if (!var.IsArray || var.NameToken == null || !SimpleMatch(var.NameToken.Next, "[")) continue;
            var tok = var.NameToken.Next!;
            while (tok.Str == "[")
            {
                var sz = tok.AstOperand2;
                if (sz != null && sz.GetKnownIntValue() == null)
                {
                    bool hasVar = false, unknownConstant = false;
                    var tokens = new List<Token?> { sz };
                    while (tokens.Count > 0)
                    {
                        var t = tokens[^1];
                        tokens.RemoveAt(tokens.Count - 1);
                        if (t == null) continue;
                        if (t.IsName && t.GetKnownIntValue() == null)
                        {
                            if (t.VarId is not null and not 0 || t.Variable != null)
                            {
                                hasVar = true;
                                continue;
                            }
                            unknownConstant = true;
                            ReportConfigError(tok);
                        }
                        if (t.IsArithmeticalOp)
                        {
                            tokens.Add(t.AstOperand1);
                            tokens.Add(t.AstOperand2);
                        }
                    }
                    if (!unknownConstant && !hasVar) ReportConfigError(tok);
                }
                tok = tok.Link!.Next!;
            }
        }

        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("while" or "if")) continue;
            var tok = token.Next;
            if (tok!.Str != "(") continue;
            var endToken = tok.Link;
            while (tok != endToken)
            {
                tok = tok!.Next!;
                if (tok.Str == "sizeof" && tok.Next!.Str == "(")
                {
                    tok = tok.Next.Link;
                    continue;
                }
                if (tok.Str == "(" && tok.IsCast)
                {
                    tok = tok.Link;
                    continue;
                }
                if (!tok.IsName) continue;
                if (tok.Function != null || tok.Variable != null || tok.VarId is not null and not 0 || tok.ValueType != null || tok.TypeScope != null) continue;
                if (tok.Next!.Str == "(" || tok.Str == "EOF") continue;
                if (IsKeyword(tok.Str, cfg.Standards.C) || IsStdLibId(tok.Str, cfg.Standards.C)) continue;
                if (tok.AstParent == null) continue;
                if (tok.AstParent.Str == "." && tok.AstParent.ValueType != null) continue;
                ReportConfigError(tok);
            }
        }
    }

    private void Misra_17_6(List<Token> rawTokens)
    {
        foreach (var token in rawTokens)
            if (SimpleMatch(token, "[ static")) ReportError(token, 17, 6);
    }

    private void Misra_17_7(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!token.Scope!.IsExecutable) continue;
            if (token.Str != "(" || token.AstParent != null) continue;
            if (token.AstOperand1 == null || !token.AstOperand1.IsName) continue;
            if (token.AstOperand1.VarId is not null and not 0 &&
                (token.AstOperand1.Variable == null || GetFunctionPointerType(token.AstOperand1.Variable.TypeStartToken) == null))
                continue;
            if (token.ValueType == null) continue;
            if (token.ValueType.Type == "void" && token.ValueType.Pointer == 0) continue;
            ReportError(token, 17, 7);
        }
    }

    private void Misra_17_8(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!(token.IsAssignmentOp || token.Str is "++" or "--")) continue;
            if (token.AstOperand1 == null) continue;
            var var = token.AstOperand1.Variable;
            if (var != null && var.IsArgument) ReportError(token, 17, 8);
        }
    }
}
