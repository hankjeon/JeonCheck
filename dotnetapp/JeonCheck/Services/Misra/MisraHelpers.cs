using System.Text.RegularExpressions;

namespace JeonCheck.Services.Misra;

/// <summary>원본 Python 코드와 같은 동작을 내기 위한 문자열·색인 보조 함수</summary>
internal static class Py
{
    /// <summary>'%s' % x: None은 "None"으로 표시된다.</summary>
    public static string Str(string? s) => s ?? "None";

    /// <summary>Python의 `sub in container` (문자열 포함 검사, 빈 문자열은 항상 포함)</summary>
    public static bool In(string? sub, string container)
    {
        if (sub == null) throw new InvalidOperationException("'in <string>' requires string as left operand, not NoneType");
        return container.Contains(sub, StringComparison.Ordinal);
    }

    /// <summary>str.find(sub, start)</summary>
    public static int Find(string s, string sub, int start = 0)
    {
        if (start < 0) start = Math.Max(0, s.Length + start);
        if (start > s.Length) return -1;
        return s.IndexOf(sub, start, StringComparison.Ordinal);
    }

    /// <summary>s[i] (음수 색인 허용, 범위를 벗어나면 IndexError처럼 예외)</summary>
    public static char At(string s, int i) => i < 0 ? s[s.Length + i] : s[i];

    /// <summary>s[:n]</summary>
    public static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>str.split(sep) 마지막 요소</summary>
    public static string LastWord(string s) => s.Split(' ')[^1];

    public static string FirstWord(string s) => s.Split(' ')[0];

    /// <summary>str.isalnum() 한 글자</summary>
    public static bool IsAlnum(char c) => char.IsLetterOrDigit(c);
}

/// <summary>misra.py의 #define 해석 (class Define)</summary>
internal sealed class Define
{
    private static readonly Regex WithArgs = new(@"^#define ([A-Za-z0-9_]+)\(([A-Za-z0-9_, ]+)\)[ ]+(.*)", RegexOptions.CultureInvariant);
    private static readonly Regex NoArgs = new(@"^#define ([A-Za-z0-9_]+)[ ]+(.*)", RegexOptions.CultureInvariant);

    public string Name { get; } = "";
    public List<string> Args { get; } = new();
    public string ExpansionList { get; } = "";

    public Define(Directive directive)
    {
        var res = WithArgs.Match(directive.Str);
        if (res.Success)
        {
            Name = res.Groups[1].Value;
            Args.AddRange(res.Groups[2].Value.Trim().Split(','));
            ExpansionList = res.Groups[3].Value;
            return;
        }
        res = NoArgs.Match(directive.Str);
        if (res.Success)
        {
            Name = res.Groups[1].Value;
            ExpansionList = res.Groups[2].Value;
        }
    }
}

/// <summary>misra.py 모듈 함수 (형 비트 수와 관계없는 것)</summary>
internal static class MisraHelpers
{
    public static bool SimpleMatch(Token? token, string pattern) => CppcheckDataFunctions.SimpleMatch(token, pattern);

    public static bool IsUnsignedType(string? ty)
    {
        if (ty == null) throw new InvalidOperationException("isUnsignedType(None)");
        return ty == "unsigned" || ty.StartsWith("uint", StringComparison.Ordinal);
    }

    public static Token? RawLink(Token? rawtoken)
    {
        if (rawtoken!.Str == "}")
        {
            int indent = 0;
            while (rawtoken != null)
            {
                if (rawtoken.Str == "}") indent++;
                else if (rawtoken.Str == "{")
                {
                    indent--;
                    if (indent == 0) break;
                }
                rawtoken = rawtoken.Previous;
            }
            return rawtoken;
        }
        return null;
    }

    public static KeyValuePair<string, string[]>[] GetStdLib(string? standard) => standard switch
    {
        "c89" => MisraTables.C90_STDLIB_IDENTIFIERS,
        "c99" => MisraTables.C99_STDLIB_IDENTIFIERS,
        _ => MisraTables.C11_STDLIB_IDENTIFIERS,
    };

    public static string[]? StdLibGet(KeyValuePair<string, string[]>[] lib, string header)
    {
        foreach (var kv in lib)
            if (kv.Key == header) return kv.Value;
        return null;
    }

    public static bool IsStdLibId(string? id, string? standard = "c99")
    {
        foreach (var kv in GetStdLib(standard))
            if (Array.IndexOf(kv.Value, id) >= 0) return true;
        return false;
    }

    public static bool IsKeyword(string? keyword, string? standard = "c99")
    {
        if (keyword == null) return false;
        if (MisraTables.C90_KEYWORDS.Contains(keyword)) return true;
        if (standard == "c89") return false;
        if (MisraTables.C99_ADDED_KEYWORDS.Contains(keyword)) return true;
        if (standard == "c99") return false;
        return MisraTables.C11_ADDED_KEYWORDS.Contains(keyword);
    }

    public static bool IsSourceFile(string? file) => file!.EndsWith(".c", StringComparison.Ordinal);

    public static bool HasPriorFunctionPrototype(Configuration cfg, Function function)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token == function.TokenDef) break;
            if (token.Str != function.Name || token.Scope!.Type != "Global") continue;
            var opening = token.Next;
            if (opening != null && opening.Str == "(" && opening.Link != null &&
                opening.Link.Next != null && opening.Link.Next.Str == ";")
                return true;
        }
        return false;
    }

    public static bool IsErrnoSettingFunction(string? name) =>
        !string.IsNullOrEmpty(name) && Array.IndexOf(MisraTables.ERRNO_SETTING_FUNCTIONS, name) >= 0;

    public static (Token To, Token From)? GetTypeConversionToFrom(Token token)
    {
        static Token? GetVartok(Token? expr)
        {
            while (expr != null)
            {
                if (IsCast(expr))
                    expr = expr.AstOperand2 == null ? expr.AstOperand1 : expr.AstOperand2;
                else if (expr.Str is "." or "::")
                    expr = expr.AstOperand2;
                else if (expr.Str == "[")
                    expr = expr.AstOperand1;
                else
                    break;
            }
            return expr != null && expr.Variable != null ? expr : null;
        }

        if (IsCast(token))
        {
            var vartok = GetVartok(token);
            if (vartok != null)
                return (token.Next!, vartok.Variable!.TypeStartToken!);
        }
        else if (token.Str == "=")
        {
            var lhs = GetVartok(token.AstOperand1);
            var rhs = GetVartok(token.AstOperand2);
            if (lhs != null && rhs != null)
                return (lhs.Variable!.TypeStartToken!, rhs.Variable!.TypeStartToken!);
        }
        return null;
    }

    public static bool IsCompositeExpr(Token? expr, bool compositeOperator = false)
    {
        if (expr == null) return false;
        if (!compositeOperator)
        {
            if (expr.Str == "?" && SimpleMatch(expr.AstOperand2, ":"))
            {
                var colon = expr.AstOperand2!;
                return IsCompositeExpr(colon.AstOperand1, true) || IsCompositeExpr(colon.AstOperand2, true);
            }
            if (expr.Str is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or ">>" or "<<" or "?" or ":" or "~")
                return IsCompositeExpr(expr.AstOperand1, true) || IsCompositeExpr(expr.AstOperand2, true);
            return false;
        }
        if (expr.IsNumber) return false;
        if (expr.AstOperand1 != null || expr.AstOperand2 != null)
            return IsCompositeExpr(expr.AstOperand1, true) || IsCompositeExpr(expr.AstOperand2, true);
        return true;
    }

    public static string? GetEssentialTypeCategory(Token? expr)
    {
        if (expr == null) return null;
        if (expr.Str == ",") return GetEssentialTypeCategory(expr.AstOperand2);
        if (expr.Str is "<" or "<=" or "==" or "!=" or ">=" or ">" or "&&" or "||" or "!") return "bool";
        if (expr.Str is "<<" or ">>") return GetEssentialTypeCategory(expr.AstOperand1);
        if (expr.Str.Length == 1 && "+-*/%&|^".Contains(expr.Str[0]))
        {
            var e1 = GetEssentialTypeCategory(expr.AstOperand1);
            var e2 = GetEssentialTypeCategory(expr.AstOperand2);
            if (!string.IsNullOrEmpty(e1) && !string.IsNullOrEmpty(e2) && e1 == e2) return e1;
            if (expr.ValueType != null) return expr.ValueType.Sign;
        }
        if (expr.ValueType?.TypeScope != null && !string.IsNullOrEmpty(expr.ValueType.TypeScope.ClassName))
            return "enum<" + expr.ValueType.TypeScope.ClassName + ">";

        // 멤버 접근, 역참조, 배열 색인을 풀어낸다
        Token? vartok = expr;
        while (true)
        {
            if (SimpleMatch(vartok, "[") || (vartok != null && vartok.Str == "*" && vartok.AstOperand2 == null))
                vartok = vartok!.AstOperand1;
            else if (SimpleMatch(vartok, "."))
                vartok = vartok!.AstOperand2;
            else
                break;
        }
        if (vartok?.Variable != null)
        {
            var typeToken = vartok.Variable.TypeStartToken;
            while (typeToken != null && typeToken.IsName)
            {
                if (typeToken.Str == "char" && !typeToken.IsSigned && !typeToken.IsUnsigned) return "char";
                if (typeToken.ValueType != null)
                {
                    if (typeToken.ValueType.Type == "bool") return typeToken.ValueType.Type;
                    if (typeToken.ValueType.Type is "float" or "double" or "long double") return "float";
                    if (!string.IsNullOrEmpty(typeToken.ValueType.Sign)) return typeToken.ValueType.Sign;
                }
                typeToken = typeToken.Next;
            }
        }

        // Appendix D, D.6 문자 상수
        if (expr.Str[0] == '\'' && expr.Str[^1] == '\'')
        {
            if (expr.Str.Length == 3 || (expr.Str.Length == 4 && expr.Str[1] == '\\')) return "char";
            return expr.ValueType!.Sign;
        }

        if (expr.IsCast && expr.Str == "(")
        {
            var castTok = expr.Next!;
            while (castTok.IsName || castTok.Str == "*")
            {
                if (castTok.Str == "char" && !castTok.IsSigned && !castTok.IsUnsigned) return "char";
                castTok = castTok.Next!;
            }
        }

        if (expr.ValueType != null) return expr.ValueType.Sign;
        return null;
    }

    public static (string? E1, string? E2) GetEssentialCategorylist(Token? operand1, Token? operand2)
    {
        if (operand1 == null || operand2 == null) return (null, null);
        if (operand1.Str is "++" or "--" || operand2.Str is "++" or "--") return (null, null);
        if ((operand1.ValueType != null && operand1.ValueType.Pointer != 0) ||
            (operand2.ValueType != null && operand2.ValueType.Pointer != 0))
            return (null, null);
        return (GetEssentialTypeCategory(operand1), GetEssentialTypeCategory(operand2));
    }

    public static string? GetFunctionPointerType(Token? tok)
    {
        string ret = "";
        while (tok != null && (tok.IsName || tok.Str == "*"))
        {
            ret += " " + tok.Str;
            tok = tok.Next;
        }
        if (tok == null || tok.Str != "(") return null;
        tok = tok.Link;
        if (!SimpleMatch(tok, ") (")) return null;
        ret += "(";
        tok = tok!.Next!.Next;
        while (tok != null && !Py.In(tok.Str, "()"))
        {
            if (tok.VarId == null) ret += " " + tok.Str;
            tok = tok.Next;
        }
        if (tok == null || tok.Str != ")") return null;
        return ret[1..] + ")";
    }

    public static bool IsCast(Token? expr)
    {
        if (expr == null || expr.Str != "(" || expr.AstOperand1 == null || expr.AstOperand2 != null) return false;
        if (SimpleMatch(expr, "( )")) return false;
        return true;
    }

    public static bool IsConstantIntegerExpression(Token? expr)
    {
        if (expr == null) return false;
        if (expr.IsInt) return true;
        if (!expr.IsArithmeticalOp) return false;
        if (expr.AstOperand1 != null && !IsConstantIntegerExpression(expr.AstOperand1)) return false;
        if (expr.AstOperand2 != null && !IsConstantIntegerExpression(expr.AstOperand2)) return false;
        return true;
    }

    public static bool IsFunctionCall(Token? expr, string? std = "c99")
    {
        if (expr == null) return false;
        if (expr.Str != "(" || expr.AstOperand1 == null) return false;
        if (expr.AstOperand1 != expr.Previous) return false;
        if (IsKeyword(expr.AstOperand1.Str, std)) return false;
        return true;
    }

    public static bool HasExternalLinkage(Variable var) => var.IsGlobal && !var.IsStatic;

    public static int CountSideEffects(Token? expr)
    {
        if (expr == null || expr.Str is "," or ";") return 0;
        int ret = expr.Str is "++" or "--" or "=" ? 1 : 0;
        return ret + CountSideEffects(expr.AstOperand1) + CountSideEffects(expr.AstOperand2);
    }

    public static Token?[]? GetForLoopExpressions(Token? forToken)
    {
        if (forToken == null || forToken.Str != "for") return null;
        var lpar = forToken.Next;
        if (lpar == null || lpar.Str != "(") return null;
        if (lpar.AstOperand2 == null || lpar.AstOperand2.Str != ";") return null;
        if (lpar.AstOperand2.AstOperand2 == null || lpar.AstOperand2.AstOperand2.Str != ";") return null;
        return new[]
        {
            lpar.AstOperand2.AstOperand1,
            lpar.AstOperand2.AstOperand2.AstOperand1,
            lpar.AstOperand2.AstOperand2.AstOperand2,
        };
    }

    public static Scope? GetFunctionScope(Configuration cfg, Function? func)
    {
        if (func != null)
            foreach (var scope in cfg.Scopes)
                if (scope.Function == func) return scope;
        return null;
    }

    public static bool IsVariableChanged(Token? startToken, Token? endToken, Variable? var)
    {
        var tok = startToken;
        while (tok != endToken)
        {
            if (tok!.IsAssignmentOp)
            {
                var vartok = tok.AstOperand1!;
                while (vartok.AstOperand1 != null) vartok = vartok.AstOperand1;
                if (vartok.Variable == var) return true;
            }
            tok = tok.Next;
        }
        return false;
    }

    public static (HashSet<Variable> FirstClause, HashSet<Variable> ExitModified)? GetForLoopCounterVariables(Token? forToken, Configuration cfg)
    {
        if (forToken == null || forToken.Str != "for") return null;
        var tn = forToken.Next;
        if (tn == null || tn.Str != "(") return null;
        var varsDefined = new HashSet<Variable>(ReferenceEqualityComparer.Instance);
        var varsInitialized = new HashSet<Variable>(ReferenceEqualityComparer.Instance);
        var varsExit = new HashSet<Variable>(ReferenceEqualityComparer.Instance);
        var varsModified = new HashSet<Variable>(ReferenceEqualityComparer.Instance);
        int curClause = 1;
        var te = tn.Link;
        while (tn != null && tn != te)
        {
            if (tn.Variable != null)
            {
                if (curClause == 1 && tn.Variable.NameToken == tn)
                {
                    varsDefined.Add(tn.Variable);
                }
                else if (curClause == 2)
                {
                    varsExit.Add(tn.Variable);
                }
                else if (curClause == 3)
                {
                    if (tn.Next != null && CountSideEffectsRecursive(tn.Next) > 0)
                    {
                        varsModified.Add(tn.Variable);
                    }
                    else if (tn.Previous != null && tn.Previous.Str is "++" or "--")
                    {
                        var tnAst = tn.AstParent;
                        if (tnAst != null && tnAst == tn.Previous)
                            varsModified.Add(tn.Variable);
                        else if (tnAst != null && tnAst.Str == "." && tnAst.AstOperand2?.Variable != null)
                            varsModified.Add(tnAst.AstOperand2.Variable);
                    }
                }
            }
            if (curClause == 1 && tn.IsAssignmentOp)
            {
                var varToken = tn.AstOperand1;
                while (varToken != null && varToken.Str == ".") varToken = varToken.AstOperand2;
                if (varToken?.Variable != null) varsInitialized.Add(varToken.Variable);
            }
            if (curClause == 1 && tn.IsName && tn.Next!.Str == "(")
            {
                var functionArgsInInit = GetArguments(tn.Next);
                var functionScope = GetFunctionScope(cfg, tn.Function);
                for (int argNr = 0; argNr < functionArgsInInit.Count; argNr++)
                {
                    var initArg = functionArgsInInit[argNr];
                    if (initArg == null || !initArg.IsUnaryOp("&")) continue;
                    var varToken = initArg.AstOperand1;
                    while (varToken != null && varToken.Str == ".") varToken = varToken.AstOperand2;
                    if (varToken?.Variable == null) continue;
                    bool changed;
                    if (functionScope == null || tn.Function == null)
                    {
                        changed = true;
                    }
                    else
                    {
                        var args = tn.Function.Argument[argNr + 1];
                        changed = IsVariableChanged(functionScope.BodyStart, functionScope.BodyEnd, args);
                    }
                    if (changed) varsInitialized.Add(varToken.Variable);
                }
            }
            if (tn.Str == ";") curClause++;
            tn = tn.Next;
        }
        varsDefined.UnionWith(varsInitialized);
        varsExit.IntersectWith(varsModified);
        return (varsDefined, varsExit);
    }

    public static List<Token> FindCounterTokens(Token? cond)
    {
        if (cond == null) return new List<Token>();
        if (cond.Str is "&&" or "||")
        {
            var c = FindCounterTokens(cond.AstOperand1);
            c.AddRange(FindCounterTokens(cond.AstOperand2));
            return c;
        }
        var ret = new List<Token>();
        if ((cond.IsArithmeticalOp && cond.AstOperand1 != null && cond.AstOperand2 != null) ||
            (cond.IsComparisonOp && cond.AstOperand1 != null && cond.AstOperand2 != null))
        {
            if (cond.AstOperand1!.IsName) ret.Add(cond.AstOperand1);
            if (cond.AstOperand2!.IsName) ret.Add(cond.AstOperand2);
            if (cond.AstOperand1.IsOp) ret.AddRange(FindCounterTokens(cond.AstOperand1));
            if (cond.AstOperand2.IsOp) ret.AddRange(FindCounterTokens(cond.AstOperand2));
        }
        return ret;
    }

    public static bool IsFloatCounterInWhileLoop(Token whileToken)
    {
        if (!SimpleMatch(whileToken, "while (")) return false;
        var lpar = whileToken.Next!;
        var rpar = lpar.Link;
        var counterTokens = FindCounterTokens(lpar.AstOperand2);
        var tokVarid = counterTokens.Where(t => t.VarId is not null and not 0).Select(t => t.VarId).ToHashSet();
        Token whileBodyStart;
        if (SimpleMatch(rpar, ") {"))
            whileBodyStart = rpar!.Next!;
        else if (SimpleMatch(whileToken.Previous, "} while") && SimpleMatch(whileToken.Previous!.Link!.Previous, "do {"))
            whileBodyStart = whileToken.Previous.Link;
        else
            return false;
        var token = whileBodyStart;
        while (token != whileBodyStart.Link)
        {
            token = token!.Next!;
            if (token.VarId is null or 0) continue;
            if (!tokVarid.Contains(token.VarId)) continue;
            if (token.AstParent == null || token.ValueType == null || !token.ValueType.IsFloat()) continue;
            var parent = token.AstParent;
            if (parent.Str is "++" or "--") return true;
            while (parent != null)
            {
                if (parent.IsAssignmentOp && parent.Str != "=" && parent.AstOperand1 == token) return true;
                if (parent.Str == "=" && parent.AstOperand1!.Str == token.Str && parent.AstOperand1 != token) return true;
                parent = parent.AstParent;
            }
        }
        return false;
    }

    public static int CountSideEffectsRecursive(Token? expr)
    {
        if (expr == null || expr.Str == ";") return 0;
        if (expr.Str == "=" && expr.AstOperand1 != null && expr.AstOperand1.Str == "[")
        {
            var prev = expr.AstOperand1.Previous;
            if (prev != null && prev.Str == "{") return CountSideEffectsRecursive(expr.AstOperand2);
        }
        if (expr.Str == "=" && expr.AstOperand1 != null && expr.AstOperand1.Str == ".")
        {
            Token? e = expr.AstOperand1;
            while (e != null && e.Str == "." && e.AstOperand2 != null) e = e.AstOperand1;
            if (e != null && e.Str == ".") return 0;
        }
        if (expr.IsAssignmentOp || expr.Str is "++" or "--") return 1;
        return CountSideEffectsRecursive(expr.AstOperand1) + CountSideEffectsRecursive(expr.AstOperand2);
    }

    public static bool IsBoolExpression(Token? expr)
    {
        if (expr == null) return false;
        if (expr.ValueType != null && (expr.ValueType.Type == "bool" || expr.ValueType.Bits == 1)) return true;
        return expr.Str is "!" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" or "0" or "1" or "true" or "false";
    }

    public static bool IsEnumConstant(Token? expr)
    {
        if (expr == null || expr.Values == null || expr.Values.Count == 0) return false;
        return expr.Values.Count == 1 && expr.Values[0].ValueKind == "known";
    }

    public static bool IsConstantExpression(Token expr)
    {
        if (expr.IsNumber) return true;
        if (expr.IsName && !IsEnumConstant(expr)) return false;
        if (SimpleMatch(expr.Previous, "sizeof (")) return true;
        if (expr.AstOperand1 != null && !IsConstantExpression(expr.AstOperand1)) return false;
        if (expr.AstOperand2 != null && !IsConstantExpression(expr.AstOperand2)) return false;
        return true;
    }

    public static bool IsUnknownConstantExpression(Token expr)
    {
        if (expr.IsName && !IsEnumConstant(expr) && expr.Variable == null) return true;
        if (expr.AstOperand1 != null && IsUnknownConstantExpression(expr.AstOperand1)) return true;
        if (expr.AstOperand2 != null && IsUnknownConstantExpression(expr.AstOperand2)) return true;
        return false;
    }

    public static int GetPrecedence(Token? expr)
    {
        if (expr == null) return 16;
        if (expr.AstOperand1 == null || expr.AstOperand2 == null) return 16;
        switch (expr.Str)
        {
            case "*" or "/" or "%": return 12;
            case "+" or "-": return 11;
            case "<<" or ">>": return 10;
            case "<" or ">" or "<=" or ">=": return 9;
            case "==" or "!=": return 8;
            case "&": return 7;
            case "^": return 6;
            case "|": return 5;
            case "&&": return 4;
            case "||": return 3;
            case "?" or ":": return 2;
        }
        if (expr.IsAssignmentOp) return 1;
        if (expr.Str == ",") return 0;
        return -1;
    }

    public static Token? FindRawLink(Token? token)
    {
        string tok1, tok2;
        bool forward;
        if (Py.In(token!.Str, "{(["))
        {
            tok1 = token.Str;
            tok2 = "})]"["{([".IndexOf(token.Str, StringComparison.Ordinal)].ToString();
            forward = true;
        }
        else if (Py.In(token.Str, "})]"))
        {
            tok1 = token.Str;
            tok2 = "{(["["})]".IndexOf(token.Str, StringComparison.Ordinal)].ToString();
            forward = false;
        }
        else
        {
            return null;
        }
        int indent = 0;
        while (token != null)
        {
            if (token.Str == tok1)
            {
                indent++;
            }
            else if (token.Str == tok2)
            {
                if (indent <= 1) return token;
                indent--;
            }
            token = forward ? token.Next : token.Previous;
        }
        return null;
    }

    public static bool NumberOfParentheses(Token? tok1, Token? tok2)
    {
        while (tok1 != null && tok1 != tok2)
        {
            if (tok1.Str is "(" or ")") return false;
            tok1 = tok1.Next;
        }
        return tok1 == tok2;
    }

    public static Token? FindGotoLabel(Token gotoToken)
    {
        string label = gotoToken.Next!.Str;
        var tok = gotoToken.Next.Next;
        while (tok != null)
        {
            if (tok.Str == "}" && tok.Scope!.Type == "Function") break;
            if (tok.Str == label && tok.Next!.Str == ":") return tok;
            tok = tok.Next;
        }
        return null;
    }

    public static Directive? FindInclude(List<Directive> directives, string header)
    {
        foreach (var d in directives)
            if (d.Str == "#include " + header) return d;
        return null;
    }

    private static void GetArgumentsRecursive(Token? tok, List<Token> arguments)
    {
        if (tok == null) return;
        if (tok.Str == ",")
        {
            GetArgumentsRecursive(tok.AstOperand1, arguments);
            GetArgumentsRecursive(tok.AstOperand2, arguments);
        }
        else
        {
            arguments.Add(tok);
        }
    }

    /// <summary>misra.py getArguments: '(' 토큰의 astOperand2에서 인자를 모은다.</summary>
    public static List<Token> GetArguments(Token ftok)
    {
        var arguments = new List<Token>();
        GetArgumentsRecursive(ftok.AstOperand2, arguments);
        return arguments;
    }

    public static bool IsAlnumAscii(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private const string HexDigits = "0123456789abcdefABCDEF";
    private const string OctDigits = "01234567";

    public static bool IsHexEscapeSequence(string symbols)
    {
        if (symbols.Length < 3 || !symbols.StartsWith("\\x", StringComparison.Ordinal)) return false;
        return symbols[2..].All(s => HexDigits.Contains(s));
    }

    public static bool IsOctalEscapeSequence(string symbols)
    {
        if (symbols.Length is < 2 or > 4 || symbols[0] != '\\') return false;
        return symbols[1..].All(s => OctDigits.Contains(s));
    }

    public static bool IsSimpleEscapeSequence(string symbols)
    {
        if (symbols.Length != 2 || symbols[0] != '\\') return false;
        return symbols[1] is '\'' or '"' or '?' or '\\' or 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v';
    }

    public static bool IsTernaryOperator(Token? token)
    {
        if (token == null || token.AstOperand2 == null) return false;
        return token.Str == "?" && token.AstOperand2.Str == ":";
    }

    public static List<Token> GetTernaryOperandsRecursive(Token? token)
    {
        if (!IsTernaryOperator(token)) return new List<Token>();
        var result = new List<Token>();
        var colon = token!.AstOperand2!;
        result.AddRange(GetTernaryOperandsRecursive(colon.AstOperand1));
        if (colon.AstOperand1 != null && !IsTernaryOperator(colon.AstOperand1)) result.Add(colon.AstOperand1);
        result.AddRange(GetTernaryOperandsRecursive(colon.AstOperand2));
        if (colon.AstOperand2 != null && !IsTernaryOperator(colon.AstOperand2)) result.Add(colon.AstOperand2);
        return result;
    }

    public static bool HasNumericEscapeSequence(string symbols)
    {
        if (!symbols.Contains('\\')) return false;
        // grouped(symbols, 2): 겹치지 않는 두 글자 묶음, 남는 마지막 글자는 버린다
        for (int i = 0; i + 1 < symbols.Length; i += 2)
            if (symbols[i] == '\\' && ("x" + OctDigits).Contains(symbols[i + 1])) return true;
        return false;
    }

    public static bool IsNoReturnScope(Token? tok)
    {
        if (tok == null || tok.Str != "}") return false;
        if (tok.Previous == null || tok.Previous.Str != ";") return false;
        if (SimpleMatch(tok.Previous.Previous, "break ;")) return true;
        var prev = tok.Previous.Previous;
        while (prev != null && !Py.In(prev.Str, ";{}"))
        {
            if (Py.In(prev.Str, "])")) prev = prev.Link;
            prev = prev!.Previous;
        }
        if (prev != null && prev.Next!.Str is "throw" or "return") return true;
        return false;
    }

    public static Token? GetAssignedVariableToken(Token? vartok)
    {
        if (vartok == null) return null;
        var parent = vartok.AstParent;
        while (parent != null && parent.IsArithmeticalOp) parent = parent.AstParent;
        if (parent != null && parent.IsAssignmentOp) return parent.AstOperand1;
        return null;
    }

    public static Function? GetFunctionUsingReturnValue(Token? valueToken)
    {
        if (valueToken?.AstParent == null) return null;
        var op = valueToken.AstParent;
        if (op.Str == "return") return op.Scope!.Function;
        if (op.IsArithmeticalOp) return GetFunctionUsingReturnValue(op);
        return null;
    }

    public static bool TokenFollowsSequence(Token? token, string[] sequence)
    {
        if (token == null) return false;
        for (int i = sequence.Length - 1; i >= 0; i--)
        {
            var prev = token!.Previous;
            if (prev == null || prev.Str != sequence[i]) return false;
            token = prev;
        }
        return true;
    }
}
