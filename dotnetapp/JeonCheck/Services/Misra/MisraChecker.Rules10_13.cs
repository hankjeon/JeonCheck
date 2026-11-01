using System.Text.RegularExpressions;
using static JeonCheck.Services.Misra.MisraHelpers;

namespace JeonCheck.Services.Misra;

// misra.py misra_10_1 ~ misra_13_6
public sealed partial class MisraChecker
{
    private void Misra_10_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!token.IsOp) continue;
            var list1 = GetTernaryOperandsRecursive(token.AstOperand1);
            var list2 = GetTernaryOperandsRecursive(token.AstOperand2);
            var ops1 = list1.Count > 0 ? list1.Cast<Token?>().ToList() : new List<Token?> { token.AstOperand1 };
            var ops2 = list2.Count > 0 ? list2.Cast<Token?>().ToList() : new List<Token?> { token.AstOperand2 };
            foreach (var t1 in ops1)
            {
                foreach (var t2 in ops2)
                {
                    var e1 = GetEssentialTypeCategory(t1);
                    var e2 = GetEssentialTypeCategory(t2);
                    if (string.IsNullOrEmpty(e1) || string.IsNullOrEmpty(e2)) continue;
                    if (token.Str is "<<" or ">>")
                    {
                        if (!IsUnsignedType(e1)) ReportError(token, 10, 1);
                        else if (!IsUnsignedType(e2) && !token.AstOperand2!.IsNumber) ReportError(token, 10, 1);
                    }
                    else if (token.Str is "~" or "&" or "|" or "^")
                    {
                        var e1Et = GetEssentialType(token.AstOperand1);
                        var e2Et = GetEssentialType(token.AstOperand2);
                        if (e1Et == "char" || e2Et == "char") ReportError(token, 10, 1);
                    }
                }
            }
        }
    }

    private void Misra_10_2(Configuration cfg)
    {
        bool IsEssentiallySignedOrUnsigned(Token? op)
        {
            var e = GetEssentialType(op);
            return !string.IsNullOrEmpty(e) && Py.FirstWord(e) is "unsigned" or "signed";
        }

        static bool IsEssentiallyChar(Token? op)
        {
            if (op == null) return false;
            if (op.Str == "+") return IsEssentiallyChar(op.AstOperand1) || IsEssentiallyChar(op.AstOperand2);
            return op.IsChar;
        }

        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("+" or "-")) continue;
            if (!IsEssentiallyChar(token.AstOperand1) && !IsEssentiallyChar(token.AstOperand2)) continue;
            if (token.Str == "+")
            {
                if (IsEssentiallyChar(token.AstOperand1) && !IsEssentiallySignedOrUnsigned(token.AstOperand2)) ReportError(token, 10, 2);
                if (IsEssentiallyChar(token.AstOperand2) && !IsEssentiallySignedOrUnsigned(token.AstOperand1)) ReportError(token, 10, 2);
            }
            if (token.Str == "-")
            {
                var e1 = GetEssentialType(token.AstOperand1);
                if (!string.IsNullOrEmpty(e1) && Py.LastWord(e1) != "char") ReportError(token, 10, 2);
                if (!IsEssentiallyChar(token.AstOperand2) && !IsEssentiallySignedOrUnsigned(token.AstOperand2)) ReportError(token, 10, 2);
            }
        }
    }

    /// <summary>misra_10_3 get_category / misra_10_5 _get_essential_category</summary>
    private static string? EssentialCategoryOf(string? essentialType)
    {
        if (!string.IsNullOrEmpty(essentialType))
        {
            if (essentialType is "bool" or "char") return essentialType;
            if (Py.LastWord(essentialType) is "float" or "double") return "floating";
            if (Py.FirstWord(essentialType) is "unsigned" or "signed") return Py.FirstWord(essentialType);
        }
        return null;
    }

    private void Misra_10_3(Configuration cfg)
    {
        foreach (var tok in cfg.Tokenlist)
        {
            if (!tok.IsAssignmentOp) continue;
            var lhs = GetEssentialType(tok.AstOperand1);
            var rhs = GetEssentialType(tok.AstOperand2);
            if (lhs == null || rhs == null) continue;

            string? findStd = !string.IsNullOrEmpty(cfg.Standards.C) ? cfg.Standards.C : _stdversion;
            var rhsTok = tok.AstOperand2!;
            string? rhsMacroName = rhsTok.MacroName;
            string rhsSpelling = rhsMacroName is "true" or "false" ? rhsMacroName : rhsTok.Str;
            bool rhsIsSourceBoolLiteral = rhsSpelling is "true" or "false";
            bool rhsIsSourceIntLiteral01 = rhsSpelling is "0" or "1";

            if (lhs == "bool")
            {
                if (rhsIsSourceBoolLiteral) continue;
                if (findStd == "c89" && rhsIsSourceIntLiteral01) continue;
            }

            var lhsCategory = EssentialCategoryOf(lhs);
            var rhsCategory = EssentialCategoryOf(rhs);
            if (lhsCategory != null && rhsCategory != null && lhsCategory != rhsCategory && rhsCategory is not ("signed" or "unsigned"))
                ReportError(tok, 10, 3);
            if (BitsOfEssentialType(lhs) < BitsOfEssentialType(rhs))
                ReportError(tok, 10, 3);
        }
    }

    private static readonly HashSet<string> Op104 = new(StringComparer.Ordinal) { "+", "-", "*", "/", "%", "&", "|", "^", "+=", "-=", ":" };

    private void Misra_10_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!Op104.Contains(token.Str) && !token.IsComparisonOp) continue;
            if (token.AstOperand1 == null || token.AstOperand2 == null) continue;
            if (token.AstOperand1.ValueType == null || token.AstOperand2.ValueType == null) continue;
            bool op1 = Op104.Contains(token.AstOperand1.Str) || token.AstOperand1.IsComparisonOp;
            bool op2 = Op104.Contains(token.AstOperand2.Str) || token.AstOperand2.IsComparisonOp;
            (string? e1, string? e2) = op1 && op2
                ? GetEssentialCategorylist(token.AstOperand1.AstOperand2, token.AstOperand2.AstOperand1)
                : op1 ? GetEssentialCategorylist(token.AstOperand1.AstOperand2, token.AstOperand2)
                : op2 ? GetEssentialCategorylist(token.AstOperand1, token.AstOperand2.AstOperand1)
                : GetEssentialCategorylist(token.AstOperand1, token.AstOperand2);

            if (token.Str is "+=" or "+")
            {
                if (e1 == "char" && e2 is "signed" or "unsigned") continue;
                if (e2 == "char" && e1 is "signed" or "unsigned") continue;
            }
            if (token.Str is "-=" or "-")
            {
                if (e1 == "char" && e2 is "signed" or "unsigned") continue;
            }
            bool both = !string.IsNullOrEmpty(e1) && !string.IsNullOrEmpty(e2);
            if (both && e1!.Contains("Anonymous", StringComparison.Ordinal) && e2 is "signed" or "unsigned") continue;
            if (both && e2!.Contains("Anonymous", StringComparison.Ordinal) && e1 is "signed" or "unsigned") continue;
            if (both && e1 != e2) ReportError(token, 10, 4);
        }
    }

    private void Misra_10_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            var toType = EssentialCategoryOf(GetEssentialType(token));
            if (toType == null) continue;
            var fromType = EssentialCategoryOf(GetEssentialType(token.AstOperand1));
            if (fromType == null) continue;
            if (toType == fromType) continue;
            if (toType == "bool" || fromType == "bool")
            {
                if (token.AstOperand1!.IsInt && token.AstOperand1.GetKnownIntValue() == 1) continue; // 예외
                ReportError(token, 10, 5);
                continue;
            }
            if (toType == "enum")
            {
                ReportError(token, 10, 5);
                continue;
            }
            if ((fromType == "float" && toType == "char") || (fromType == "char" && toType == "float"))
                ReportError(token, 10, 5);
        }
    }

    private void Misra_10_6(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "=" || token.AstOperand1 == null || token.AstOperand2 == null) continue;
            if (!IsCompositeExpr(token.AstOperand2)) continue;
            var vt1 = token.AstOperand1.ValueType;
            var vt2 = token.AstOperand2.ValueType;
            if (vt1 == null || vt1.Pointer > 0) continue;
            if (vt2 == null || vt2.Pointer > 0) continue;
            string? e = IsCast(token.AstOperand2) ? vt2.Type : GetEssentialType(token.AstOperand2);
            if (string.IsNullOrEmpty(e)) continue;
            if (e == "char" && vt1.Type == "int") continue; // char 연산은 보통 int로 승격된다
            int lhsbits = vt1.Bits is not null and not 0 ? vt1.Bits.Value : BitsOfEssentialType(vt1.Type);
            if (lhsbits > BitsOfEssentialType(e)) ReportError(token, 10, 6);
        }
    }

    private void Misra_10_7(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.AstOperand1 == null || token.AstOperand2 == null) continue;
            if (!token.IsArithmeticalOp) continue;
            if (!IsCompositeExpr(token)) continue;
            var parent = token.AstParent;
            if (parent == null) continue;
            if (!parent.IsArithmeticalOp)
            {
                if (!parent.IsAssignmentOp) continue;
                if (parent.Str == "=") continue;
            }
            var tokenType = GetEssentialType(token);
            if (tokenType == null) continue;
            var sibling = token == parent.AstOperand2 ? parent.AstOperand1 : parent.AstOperand2;
            var siblingType = GetEssentialType(sibling);
            if (siblingType == null) continue;
            int b1 = BitsOfEssentialType(tokenType);
            int b2 = BitsOfEssentialType(siblingType);
            if (b1 > 0 && b1 < b2) ReportError(token, 10, 7);
        }
    }

    private void Misra_10_8(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            if (token.ValueType == null || token.ValueType.Pointer > 0) continue;
            var op = token.AstOperand1!;
            if (op.ValueType == null || op.ValueType.Pointer > 0) continue;
            if (op.AstOperand1 == null) continue;
            if (op.Str is not ("+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or ">>" or "<<" or "?" or ":" or "~")) continue;
            if (op.Str != "~" && op.AstOperand2 == null) continue;
            string? e2;
            if (op.Str == "~")
            {
                e2 = GetEssentialTypeCategory(op.AstOperand1);
            }
            else
            {
                (e2, var e3) = GetEssentialCategorylist(op.AstOperand1, op.AstOperand2);
                if (e2 != e3) continue;
            }
            var e1 = GetEssentialTypeCategory(token);
            if (e1 != e2)
            {
                ReportError(token, 10, 8);
            }
            else
            {
                var e = GetEssentialType(op);
                if (string.IsNullOrEmpty(e)) continue;
                if (BitsOfEssentialType(token.ValueType.Type) > BitsOfEssentialType(e)) ReportError(token, 10, 8);
            }
        }
    }

    private void Misra_11_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            var toFrom = GetTypeConversionToFrom(token);
            if (toFrom == null) continue;
            var fromType = GetFunctionPointerType(toFrom.Value.From);
            if (fromType == null) continue;
            var toType = GetFunctionPointerType(toFrom.Value.To);
            if (toType == null || toType != fromType) ReportError(token, 11, 1);
        }
    }

    private void Misra_11_2(Configuration cfg)
    {
        static Token? GetPointerType(Token? typeToken)
        {
            while (typeToken != null && typeToken.Str is "const" or "struct") typeToken = typeToken.Next;
            if (typeToken == null) return null;
            if (!typeToken.IsName) return null;
            return typeToken.Next != null && typeToken.Next.Str == "*" ? typeToken : null;
        }

        var incompleteTypes = new List<string>();
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str == "struct" && token.Next?.Next != null && token.Next.IsName && token.Next.Next.Str == ";")
                incompleteTypes.Add(token.Next.Str);
            var toFrom = GetTypeConversionToFrom(token);
            if (toFrom == null) continue;
            var toPtr = GetPointerType(toFrom.Value.To);
            if (toPtr == null) continue;
            var fromPtr = GetPointerType(toFrom.Value.From);
            if (fromPtr == null) continue;
            if (toPtr.Str == fromPtr.Str) continue;
            if (fromPtr.TypeScope == null && incompleteTypes.Contains(fromPtr.Str)) ReportError(token, 11, 2);
            else if (toPtr.TypeScope == null && incompleteTypes.Contains(toPtr.Str)) ReportError(token, 11, 2);
        }
    }

    private void Misra_11_3(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            var vt1 = token.ValueType;
            var vt2 = token.AstOperand1!.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (vt1.Type == "void" || vt2.Type == "void") continue;
            if (vt1.Pointer > 0 && vt1.Type == "record" && vt2.Pointer > 0 && vt2.Type == "record" && vt1.TypeScopeId != vt2.TypeScopeId)
                ReportError(token, 11, 3);
            else if (vt1.Pointer == vt2.Pointer && vt1.Pointer > 0 && vt1.Type != vt2.Type && vt1.Type != "char")
                ReportError(token, 11, 3);
        }
    }

    private static readonly Regex DefineAny = new(@"^#define ([A-Za-z0-9_]+).*", RegexOptions.CultureInvariant);

    private void Misra_11_4(Configuration cfg)
    {
        var macros = new Dictionary<string, List<Directive>>(StringComparer.Ordinal);
        foreach (var directive in cfg.Directives)
        {
            var res = DefineAny.Match(directive.Str);
            if (!res.Success) continue;
            if (!macros.TryGetValue(res.Groups[1].Value, out var l)) macros[res.Groups[1].Value] = l = new List<Directive>();
            l.Add(directive);
        }

        // 매크로 정의가 위반이면 사용 위치 대신 정의 위치에 한 번만 보고한다
        var badMacros = new List<string>();
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            var vt1 = token.ValueType;
            var vt2 = token.AstOperand1!.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (vt2.Pointer > 0 && vt1.Pointer == 0 && (vt1.IsIntegral() || vt1.IsEnum()) && vt2.Type != "void")
            {
                ReportError(token, 11, 4);
            }
            else if (vt1.Pointer > 0 && vt2.Pointer == 0 && (vt2.IsIntegral() || vt2.IsEnum()) && vt1.Type != "void")
            {
                if (token.MacroName != null &&
                    token.MacroName == token.AstOperand1.MacroName &&
                    token.AstOperand1.IsInt &&
                    token.Link!.Previous!.Str == "*" &&
                    token.MacroName == token.Link.Previous.MacroName &&
                    macros.TryGetValue(token.MacroName, out var defs) &&
                    defs.Count == 1)
                {
                    if (!badMacros.Contains(token.MacroName))
                    {
                        badMacros.Add(token.MacroName);
                        ReportError(defs[0], 11, 4);
                    }
                    continue;
                }
                ReportError(token, 11, 4);
            }
        }
    }

    private void Misra_11_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token))
            {
                if (token.AstOperand1 != null && token.AstOperand2 != null && token.Str == "=" && token.Next!.Str != "(")
                {
                    var a = token.AstOperand1.ValueType;
                    var b = token.AstOperand2.ValueType;
                    if (a == null || b == null) continue;
                    if (a.Pointer > 0 && a.Type != "void" && b.Pointer == a.Pointer && b.Type == "void") ReportError(token, 11, 5);
                }
                continue;
            }
            if (token.AstOperand1!.AstOperand1 != null && token.AstOperand1.AstOperand1.Str is "malloc" or "calloc" or "realloc" or "free")
                continue;
            var vt1 = token.ValueType;
            var vt2 = token.AstOperand1.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (vt1.Pointer > 0 && vt1.Type != "void" && vt2.Pointer == vt1.Pointer && vt2.Type == "void") ReportError(token, 11, 5);
        }
    }

    private void Misra_11_6(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            var vt1 = token.ValueType;
            var vt2 = token.AstOperand1!.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (vt1.Pointer == 1 && vt1.Type == "void" && vt2.Pointer == 0 && token.AstOperand1.GetKnownIntValue() != 0)
                ReportError(token, 11, 6);
            else if (vt1.Pointer == 0 && vt1.Type != "void" && vt2.Pointer == 1 && vt2.Type == "void")
                ReportError(token, 11, 6);
        }
    }

    private void Misra_11_7(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (!IsCast(token)) continue;
            var vt1 = token.ValueType;
            var vt2 = token.AstOperand1!.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (token.AstOperand1.AstOperand1 != null) continue;
            if (vt2.Pointer > 0 && vt1.Pointer == 0 && !vt1.IsIntegral() && !vt1.IsEnum() && vt1.Type != "void")
                ReportError(token, 11, 7);
            else if (vt1.Pointer > 0 && vt2.Pointer == 0 && !vt2.IsIntegral() && !vt2.IsEnum() && vt1.Type != "void")
                ReportError(token, 11, 7);
        }
    }

    private void Misra_11_8(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (IsCast(token))
            {
                if (token.ValueType == null) continue;
                if (token.AstOperand1!.ValueType == null) continue;
                if (token.ValueType.Pointer == 0) continue;
                if (token.AstOperand1.ValueType.Pointer == 0) continue;
                if (token.ValueType.Constness % 2 < token.AstOperand1.ValueType.Constness % 2) ReportError(token, 11, 8);
            }
            else if (token.Str == "(" && token.AstOperand1 != null && token.AstOperand2 != null && token.AstOperand1.Function != null)
            {
                var function = token.AstOperand1.Function;
                var arguments = GetArguments(token);
                foreach (var (argnr, argvar) in function.Argument)
                {
                    if (argnr < 1 || argnr > arguments.Count) continue;
                    if (!argvar!.IsPointer) continue;
                    var argtok = arguments[argnr - 1];
                    if (argtok.ValueType == null) continue;
                    if (argtok.ValueType.Pointer == 0) continue;
                    if (argvar.Constness % 2 < argtok.ValueType.Constness % 2) ReportError(token, 11, 8);
                }
            }
        }
    }

    private void Misra_11_9(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.AstOperand1 == null || token.AstOperand2 == null || token.Str is not ("=" or "==" or "!=" or "?" or ":")) continue;
            var vt1 = token.AstOperand1.ValueType;
            var vt2 = token.AstOperand2.ValueType;
            if (vt1 == null || vt2 == null) continue;
            if (vt1.Pointer > 0 && vt2.Pointer == 0 && token.AstOperand2.Str == "NULL") continue;
            if (token.AstOperand2.Values is { Count: > 0 } && vt1.Pointer > 0 && vt2.Pointer == 0 && token.AstOperand2.GetValue(0) != null)
                ReportError(token, 11, 9);
        }
    }

    private static readonly Regex StartsWithIdentifier = new(@"^[a-zA-Z_]", RegexOptions.CultureInvariant);

    private void Misra_12_1_sizeof(List<Token> rawTokens)
    {
        int state = 0;
        foreach (var tok in rawTokens)
        {
            if (tok.Str.StartsWith("//", StringComparison.Ordinal) || tok.Str.StartsWith("/*", StringComparison.Ordinal)) continue;
            if (tok.Str == "sizeof")
            {
                state = 1;
            }
            else if (state == 1)
            {
                state = StartsWithIdentifier.IsMatch(tok.Str) ? 2 : 0;
            }
            else if (state == 2)
            {
                if (tok.Str is "+" or "-" or "*" or "/" or "%") ReportError(tok, 12, 1);
                else state = 0;
            }
        }
    }

    private void Misra_12_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            int p = GetPrecedence(token);
            if (p < 2 || p > 12) continue;
            int p1 = GetPrecedence(token.AstOperand1);
            if (p < p1 && p1 <= 12 && NumberOfParentheses(token.AstOperand1, token))
            {
                ReportError(token, 12, 1);
                continue;
            }
            int p2 = GetPrecedence(token.AstOperand2);
            if (p < p2 && p2 <= 12 && NumberOfParentheses(token, token.AstOperand2))
                ReportError(token, 12, 1);
        }
    }

    private void Misra_12_2(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("<<" or ">>")) continue;
            if (token.AstOperand2?.Values is not { Count: > 0 }) continue;
            long maxval = 0;
            foreach (var val in token.AstOperand2.Values)
                if (val.IntValue is not null and not 0 && val.IntValue > maxval) maxval = val.IntValue.Value;
            if (maxval == 0) continue;
            int sz = BitsOfEssentialType(GetEssentialType(token.AstOperand1));
            if (sz <= 0) continue;
            if (maxval >= sz) ReportError(token, 12, 2);
        }
    }

    private void Misra_12_3(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str == ";" && token.IsSplittedVarDeclComma) ReportError(token, 12, 3);
            if (token.Str == "," && token.AstParent != null && token.AstParent.Str == ";") ReportError(token, 12, 3);
            if (token.Str == "," && token.AstParent == null)
            {
                if (token.Scope!.Type is "Class" or "Struct")
                {
                    // 생성자 초기화 목록의 쉼표는 제외
                    Token? tok = token;
                    while (tok != null && tok.Str == ",")
                    {
                        tok = tok.Next;
                        if (tok?.Next != null && tok.IsName && tok.Next.Str == "(")
                            tok = tok.Next.Link!.Next;
                    }
                    if (tok!.Str == "{") continue;
                }
                var prev = token.Previous;
                while (prev != null)
                {
                    if (prev.Str == ";")
                    {
                        ReportError(token, 12, 3);
                        break;
                    }
                    if (Py.In(prev.Str, "({[")) break;
                    if (Py.In(prev.Str, ")}]")) prev = prev.Link;
                    prev = prev!.Previous;
                }
            }
        }
    }

    private void Misra_12_4_check_expr(Token expr)
    {
        if (expr.AstOperand2 == null || expr.AstOperand1 == null) return;
        if (expr.ValueType == null) return;
        if (expr.ValueType.Sign == null || expr.ValueType.Sign != "unsigned") return;
        if (expr.ValueType.Pointer > 0) return;
        if (!expr.ValueType.IsIntegral()) return;
        long? op1 = expr.AstOperand1.GetKnownIntValue();
        if (op1 == null) return;
        long? op2 = expr.AstOperand2.GetKnownIntValue();
        if (op2 == null) return;
        int bits = BitsOfEssentialType("unsigned " + Py.Str(expr.ValueType.Type));
        if (bits <= 0 || bits >= 64) return;
        Int128 maxValue = ((Int128)1 << bits) - 1;
        if (!IsConstantIntegerExpression(expr)) return;
        Int128 a = op1.Value, b = op2.Value;
        if (expr.Str == "+" && a + b > maxValue) ReportError(expr, 12, 4);
        else if (expr.Str == "-" && a - b < 0) ReportError(expr, 12, 4);
        else if (expr.Str == "*" && a * b > maxValue) ReportError(expr, 12, 4);
    }

    private void Misra_12_4(Configuration cfg)
    {
        if (cfg.Tokenlist.Count == 0) return;
        var expr = cfg.Tokenlist[0];
        while (expr.Next != null)
        {
            expr = expr.Next;
            if (expr.Str == "?" && expr.AstOperand2!.Str == ":")
            {
                long? knownValue = expr.AstOperand1!.GetKnownIntValue();
                if (knownValue == 1)
                {
                    var tok = expr;
                    while (tok != expr.AstOperand2)
                    {
                        Misra_12_4_check_expr(tok!);
                        tok = tok!.Next;
                    }
                    expr = tok!;
                    while (expr.Str is not (";" or "{" or "}")) expr = expr.Next!;
                    continue;
                }
                if (knownValue == 0) expr = expr.AstOperand2;
            }
            Misra_12_4_check_expr(expr);
        }
    }

    private void Misra_13_1(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (SimpleMatch(token, ") {") && token.Next!.AstParent == token.Link)
            {
                // 복합 리터럴
            }
            else if (!SimpleMatch(token, "= {"))
            {
                continue;
            }
            var init = token.Next!;
            var end = init.Link;
            if (end == null) continue;
            Token? tn = init;
            while (tn != null && tn != end)
            {
                if (tn.Str == "[" && tn.Link != null)
                {
                    tn = tn.Link;
                    if (tn.Next != null && tn.Next.Str == "=")
                    {
                        tn = tn.Next.Next;
                        continue;
                    }
                    break;
                }
                if (tn.Str == "." && tn.Next != null && tn.Next.IsName)
                {
                    tn = tn.Next;
                    if (tn.Next != null && tn.Next.Str == "=") tn = tn.Next.Next;
                    continue;
                }
                if (tn.Str is "++" or "--" || tn.IsAssignmentOp) ReportError(init, 13, 1);
                tn = tn.Next;
            }
        }
    }

    private void Misra_13_3(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str is not ("++" or "--")) continue;
            var astTop = token;
            while (astTop.AstParent != null && astTop.AstParent.Str is not ("," or ";")) astTop = astTop.AstParent;
            if (CountSideEffects(astTop) >= 2) ReportError(astTop, 13, 3);
        }
    }

    private void Misra_13_4(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
        {
            if (token.Str != "=") continue;
            if (token.AstParent == null) continue;
            if (token.AstOperand1 == null || token.AstOperand2 == null) continue;
            if (token.AstOperand1.Str == "[" && token.AstOperand1.Previous!.Str is "{" or ",") continue;
            if (token.AstParent.Str is not ("," or ";" or "{")) ReportError(token, 13, 4);
        }
    }

    private void Misra_13_5(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.IsLogicalOp && CountSideEffectsRecursive(token.AstOperand2) > 0) ReportError(token, 13, 5);
    }

    private void Misra_13_6(Configuration cfg)
    {
        foreach (var token in cfg.Tokenlist)
            if (token.Str == "sizeof" && CountSideEffectsRecursive(token.Next) > 0) ReportError(token, 13, 6);
    }
}
