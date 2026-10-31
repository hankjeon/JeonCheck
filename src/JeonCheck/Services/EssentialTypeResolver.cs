using System.Globalization;
using JeonCheck.Models;

namespace JeonCheck.Services;

/// <summary>
/// MISRA C 2012 필수 타입(Essential Type) 범주 (부록 D)
/// </summary>
public enum EssentialCategory
{
    Boolean,
    Character,
    Signed,
    Unsigned,
    Floating
}

public readonly record struct EssentialType(EssentialCategory Category, int Bits, string TypeName)
{
    /// <summary>부호 있는/없는 정수 범주 여부 (문자형·불리언 제외)</summary>
    public bool IsSignedOrUnsigned => Category is EssentialCategory.Signed or EssentialCategory.Unsigned;

    /// <summary>정수처럼 비트 폭을 비교할 수 있는 범주 여부 (부호 있는/없는 정수 + 문자형)</summary>
    public bool IsIntegerLike => IsSignedOrUnsigned || Category == EssentialCategory.Character;

    public string Display => Bits > 0 ? $"{TypeName} ({Bits}-bit)" : TypeName;
}

/// <summary>
/// Cppcheck misra.py의 getEssentialType() 로직을 C#으로 변환한 필수 타입 계산기.
/// C 언어의 정수 승격(int promotion) 결과 타입이 아니라 MISRA가 정의한 필수 타입을 계산하므로,
/// 예를 들어 'uint8_t + uint8_t'는 signed int가 아닌 unsigned char(8-bit)로 판정된다.
/// 판정할 수 없는 수식은 null을 반환하며, 호출 측은 null이면 검사를 생략하여 오탐을 피한다.
/// </summary>
public static class EssentialTypeResolver
{
    private static readonly string[] IntegerRankOrder = { "char", "short", "int", "long", "long long" };

    public static EssentialType? Resolve(DumpToken? expr, DumpPlatform platform)
    {
        if (expr == null || expr.ValuePointer > 0)
            return null;

        // 부록 D.6: 문자 상수('a', '\n')는 문자형
        if (IsCharLiteral(expr.Str))
            return new EssentialType(EssentialCategory.Character, platform.CharBit, "char");

        // 부록 D.6: 정수 상수는 값을 표현할 수 있는 가장 작은 타입 (int 이외의 접미사 타입은 그대로)
        if (expr.IsIntLiteral)
        {
            if (expr.ValueType == "int" && TryEvalConstInt(expr, out long literalValue))
                return FromValue(literalValue, expr.ValueSign != "unsigned", platform) ?? FromValueType(expr, platform, isPlainChar: false);
            return FromValueType(expr, platform, isPlainChar: false);
        }

        if (expr.IsFloatLiteral)
            return FromValueType(expr, platform, isPlainChar: false);

        // 변수: 선언된 타입 (typedef 해제 후 'char' 토큰에 signed/unsigned가 없으면 일반 문자형)
        if (expr.Variable != null)
            return FromValueType(expr, platform, IsPlainCharType(expr.Variable.TypeStartToken));

        // 캐스트: 캐스트 대상 타입
        if (expr.IsCast)
            return FromValueType(expr, platform, IsPlainCharType(expr.Next));

        switch (expr.Str)
        {
            case "<" or "<=" or ">" or ">=" or "==" or "!=" or "&&" or "||" or "!":
                return new EssentialType(EssentialCategory.Boolean, 1, "bool");
            case ",":
                return Resolve(expr.AstOperand2, platform);
            case "?":
                return Resolve(expr.AstOperand2, platform); // astOperand2는 ':' 토큰
            case "~" when expr.AstOperand2 == null:
                return Resolve(expr.AstOperand1, platform);
            case "-" or "+" when expr.AstOperand2 == null && expr.AstOperand1 != null:
                if (IsIntegerValueType(expr) && TryEvalConstInt(expr, out long unaryValue))
                    return FromValue(unaryValue, expr.ValueSign != "unsigned", platform);
                return Resolve(expr.AstOperand1, platform);
        }

        // 이항 산술/비트/시프트 연산 및 조건 연산자의 ':'
        if (expr.AstOperand1 != null && expr.AstOperand2 != null &&
            expr.Str is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<<" or ">>" or ":")
        {
            if (expr.AstOperand1.ValuePointer > 0 || expr.AstOperand2.ValuePointer > 0)
                return null;

            // 정수 상수 수식은 계산된 값으로 필수 타입 결정 (예: 1U << 12 → unsigned short)
            if (expr.Str != ":" && IsIntegerValueType(expr) && TryEvalConstInt(expr, out long constValue))
            {
                var byValue = FromValue(constValue, expr.ValueSign != "unsigned", platform);
                if (byValue != null)
                    return byValue;
            }

            var e1 = Resolve(expr.AstOperand1, platform);
            var e2 = Resolve(expr.AstOperand2, platform);
            if (e1 == null || e2 == null)
                return null;

            // 시프트 연산의 필수 타입은 좌변 피연산자의 필수 타입
            if (expr.Str is "<<" or ">>")
                return e1;

            return e2.Value.Bits >= e1.Value.Bits ? e2 : e1;
        }

        // 함수 호출 결과(반환형), 배열 요소, 구조체 멤버, 포인터 역참조, 대입식: 해당 토큰의 값 타입
        bool isFunctionCall = expr.Str == "(" && expr.AstOperand1 != null && expr.AstOperand1.Type == "name";
        bool isDeref = expr.Str == "*" && expr.AstOperand2 == null;
        if (isFunctionCall || isDeref || expr.Str is "[" or "." || expr.IsAssignmentOp)
            return FromValueType(expr, platform, isPlainChar: false);

        // 열거형 상수 등 판정 불가 항목은 오탐 방지를 위해 null (misra.py와 동일)
        return null;
    }

    /// <summary>
    /// 토큰의 값 타입(valueType)을 그대로 필수 타입으로 변환 (함수 반환형 등 승격이 없는 위치에 사용)
    /// </summary>
    public static EssentialType? FromValueType(DumpToken tok, DumpPlatform platform, bool isPlainChar)
    {
        if (tok.ValuePointer > 0 || string.IsNullOrEmpty(tok.ValueType))
            return null;

        if (tok.ValueType == "bool")
            return new EssentialType(EssentialCategory.Boolean, 1, "bool");

        if (DumpPlatform.IsFloatType(tok.ValueType, 0))
            return new EssentialType(EssentialCategory.Floating, platform.GetBitWidth(tok.ValueType, 0), tok.ValueType);

        if (!DumpPlatform.IsIntegerType(tok.ValueType, 0))
            return null;

        int bits = platform.GetBitWidth(tok.ValueType, 0);
        if (bits <= 0)
            return null;

        if (tok.ValueType == "char" && (isPlainChar || string.IsNullOrEmpty(tok.ValueSign)))
            return new EssentialType(EssentialCategory.Character, bits, "char");

        bool isUnsigned = tok.ValueSign == "unsigned";
        return new EssentialType(
            isUnsigned ? EssentialCategory.Unsigned : EssentialCategory.Signed,
            bits,
            $"{(isUnsigned ? "unsigned" : "signed")} {tok.ValueType}");
    }

    /// <summary>
    /// 정수 상수 값을 표현할 수 있는 가장 작은 정수 타입 (misra.py get_essential_type_from_value)
    /// </summary>
    public static EssentialType? FromValue(long value, bool isSigned, DumpPlatform platform)
    {
        if (!isSigned && value < 0)
            return null;

        foreach (var typeName in IntegerRankOrder)
        {
            int bits = platform.GetBitWidth(typeName, 0);
            if (bits <= 0)
                continue;
            if (FitsInBits(value, bits, !isSigned))
            {
                return new EssentialType(
                    isSigned ? EssentialCategory.Signed : EssentialCategory.Unsigned,
                    bits,
                    $"{(isSigned ? "signed" : "unsigned")} {typeName}");
            }
        }

        return null;
    }

    public static bool FitsInBits(long value, int bits, bool isUnsigned)
    {
        if (bits >= 64)
            return !isUnsigned || value >= 0;

        if (isUnsigned)
            return value >= 0 && (ulong)value <= ((1UL << bits) - 1UL);

        long min = -(1L << (bits - 1));
        long max = (1L << (bits - 1)) - 1L;
        return value >= min && value <= max;
    }

    /// <summary>
    /// 정수 리터럴로만 이루어진 상수 수식의 값을 계산 (매크로 상수 확장 결과 포함). 계산 불가하면 false.
    /// </summary>
    public static bool TryEvalConstInt(DumpToken? tok, out long value)
    {
        value = 0;
        if (tok == null || tok.ValuePointer > 0)
            return false;

        if (tok.IsIntLiteral)
            return TryParseIntLiteral(tok.Str, out value);

        if (tok.IsCast || tok.Variable != null)
            return false;

        try
        {
            checked
            {
                if (tok.AstOperand1 != null && tok.AstOperand2 == null)
                {
                    if (!TryEvalConstInt(tok.AstOperand1, out long v1))
                        return false;
                    switch (tok.Str)
                    {
                        case "-": value = -v1; return true;
                        case "+": value = v1; return true;
                        default: return false;
                    }
                }

                if (tok.AstOperand1 == null || tok.AstOperand2 == null)
                    return false;

                if (!TryEvalConstInt(tok.AstOperand1, out long a) || !TryEvalConstInt(tok.AstOperand2, out long b))
                    return false;

                switch (tok.Str)
                {
                    case "+": value = a + b; return true;
                    case "-": value = a - b; return true;
                    case "*": value = a * b; return true;
                    case "/": if (b == 0) return false; value = a / b; return true;
                    case "%": if (b == 0) return false; value = a % b; return true;
                    case "&": value = a & b; return true;
                    case "|": value = a | b; return true;
                    case "^": value = a ^ b; return true;
                    case "<<":
                        if (b < 0 || b > 62 || a < 0 || a > (long.MaxValue >> (int)b)) return false;
                        value = a << (int)b; return true;
                    case ">>":
                        if (b < 0 || b > 63) return false;
                        value = a >> (int)b; return true;
                    default:
                        return false;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TryParseIntLiteral(string raw, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string s = raw.Trim().TrimEnd('u', 'U', 'l', 'L');
        bool negative = false;
        if (s.StartsWith('-'))
        {
            negative = true;
            s = s.Substring(1);
        }

        bool ok;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ok = ulong.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex) && hex <= long.MaxValue;
            value = ok ? (long)hex : 0;
        }
        else if (s.Length > 1 && s[0] == '0' && s.All(c => c is >= '0' and <= '7'))
        {
            try
            {
                value = Convert.ToInt64(s, 8);
                ok = true;
            }
            catch
            {
                ok = false;
            }
        }
        else
        {
            ok = long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        if (ok && negative)
            value = -value;
        return ok;
    }

    private static bool IsIntegerValueType(DumpToken tok) => DumpPlatform.IsIntegerType(tok.ValueType, tok.ValuePointer);

    private static bool IsCharLiteral(string s)
    {
        return s.Length >= 3 && s[0] == '\'' && s[^1] == '\'' &&
               (s.Length == 3 || (s.Length == 4 && s[1] == '\\'));
    }

    /// <summary>
    /// 함수 매개변수의 필수 타입. 이름 있는 매개변수는 이름 토큰의 값 타입을 쓰고,
    /// 이름 없는 프로토타입 매개변수(예: f(int16_t))는 단일 타입 토큰으로 판정한다.
    /// ('long'/'long long'은 dump 타입 토큰만으로 구분할 수 없어 오탐 방지를 위해 판정하지 않음)
    /// </summary>
    public static EssentialType? ResolveParameter(DumpVariable param, DumpPlatform platform)
    {
        if (param.NameToken != null)
            return FromValueType(param.NameToken, platform, IsPlainCharType(param.TypeStartToken));

        var typeTok = param.TypeStartToken;
        if (typeTok == null || param.TypeStartTokenId != param.TypeEndTokenId)
            return null;
        if (typeTok.Next == null || typeTok.Next.Str is not ("," or ")"))
            return null; // 포인터('*') 등 추가 선언자가 있는 경우

        switch (typeTok.Str)
        {
            case "bool" or "_Bool":
                return new EssentialType(EssentialCategory.Boolean, 1, "bool");
            case "float" or "double":
                return new EssentialType(EssentialCategory.Floating, platform.GetBitWidth(typeTok.Str, 0), typeTok.Str);
            case "char" when !typeTok.IsSigned && !typeTok.IsUnsigned:
                return new EssentialType(EssentialCategory.Character, platform.CharBit, "char");
            case "char" or "short" or "int":
                bool isUnsigned = typeTok.IsUnsigned;
                return new EssentialType(
                    isUnsigned ? EssentialCategory.Unsigned : EssentialCategory.Signed,
                    platform.GetBitWidth(typeTok.Str, 0),
                    $"{(isUnsigned ? "unsigned" : "signed")} {typeTok.Str}");
            default:
                return null;
        }
    }

    /// <summary>
    /// 타입 선언 토큰열에서 signed/unsigned가 명시되지 않은 일반 'char'인지 확인 (misra.py와 동일한 판정)
    /// </summary>
    private static bool IsPlainCharType(DumpToken? typeTok)
    {
        for (var tok = typeTok; tok != null && tok.Type == "name"; tok = tok.Next)
        {
            if (tok.Str == "char")
                return !tok.IsSigned && !tok.IsUnsigned;
        }
        return false;
    }
}
