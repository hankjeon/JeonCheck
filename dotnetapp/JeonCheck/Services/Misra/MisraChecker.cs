namespace JeonCheck.Services.Misra;

/// <summary>misra.py --cli가 보고하는 위반 한 건. RuleId는 "misra-c2012-X.Y" 또는 "misra-config".</summary>
public sealed record MisraViolation(string RuleId, string? File, int Line, int Column);

/// <summary>dump 파일 하나의 검사 결과. 원본처럼 도중에 예외가 나면 그 전까지 보고된 위반과 오류를 함께 돌려준다.</summary>
public sealed record MisraResult(List<MisraViolation> Violations, Exception? Error);

/// <summary>
/// Cppcheck 2.22 addons/misra.py(MisraChecker)와 misra_9.py를 C#으로 옮긴 검사기.
/// 규칙별 함수 이름·실행 순서·보고 위치는 원본과 같게 유지한다. (파일 간 CTU 규칙은 원본 --cli 단일 dump 실행처럼 보고하지 않는다)
/// </summary>
public sealed partial class MisraChecker
{
    private readonly List<MisraViolation> _violations = new();
    private readonly HashSet<string> _existingViolations = new(StringComparer.Ordinal);
    private readonly string _stdversion = "c89";
    private bool _isCpp;

    // 원본 모듈 전역 typeBits (dump의 platform 값으로 설정)
    private int _bitsChar, _bitsShort, _bitsInt, _bitsLong, _bitsLongLong, _bitsPointer;

    /// <summary>dump 파일 하나를 검사한다. (misra.py --cli &lt;dump&gt; 와 같은 결과)</summary>
    public static MisraResult CheckDump(string dumpFile)
    {
        var checker = new MisraChecker();
        try
        {
            checker.ParseDump(dumpFile);
            return new MisraResult(checker._violations, null);
        }
        catch (Exception ex)
        {
            return new MisraResult(checker._violations, ex);
        }
    }

    private void ReportError(DumpLocation? location, int num1, int num2)
    {
        if (location == null) throw new NullReferenceException($"reportError location is None (rule {num1}.{num2})");
        int ruleNum = num1 * 100 + num2;
        string key = $"{Py.Str(location.File)}-{location.Linenr}-{location.Column}-{ruleNum}";
        if (_existingViolations.Add(key))
            _violations.Add(new MisraViolation($"misra-c2012-{num1}.{num2}", location.File, location.Linenr, location.Column));
    }

    private void ReportConfigError(DumpLocation location) =>
        _violations.Add(new MisraViolation("misra-config", location.File, location.Linenr, location.Column));

    // misra.py executeCheck: C++ 파일이면 C++에도 해당하는 규칙만 실행
    private static readonly HashSet<int> MisraCpp = new() { 202, 203, 402, 701, 702, 1203, 1204, 1305, 1702, 1901 };

    private void ExecuteCheck(int ruleNum, Action check)
    {
        if (!_isCpp || MisraCpp.Contains(ruleNum))
            check();
    }

    private void ParseDump(string dumpfile)
    {
        var data = CppcheckData.Parse(dumpfile);
        var platform = data.Platform ?? throw new InvalidOperationException("dump에 platform 정보가 없습니다.");
        _bitsChar = platform.CharBit;
        _bitsShort = platform.ShortBit;
        _bitsInt = platform.IntBit;
        _bitsLong = platform.LongBit;
        _bitsLongLong = platform.LongLongBit;
        _bitsPointer = platform.PointerBit;
        _isCpp = data.Language == "cpp";

        int cfgNumber = 0;
        foreach (var cfg in data.IterConfigurations())
        {
            var raw = data.RawTokens;
            ExecuteCheck(102, () => Misra_1_2(cfg));
            ExecuteCheck(104, () => Misra_1_4(cfg));
            ExecuteCheck(202, () => Misra_2_2(cfg));
            // 2.3, 2.4, 2.5, 5.6~5.9, 8.5~8.7: CTU 요약만 남기므로 단일 dump 실행에서는 보고가 없다.
            ExecuteCheck(204, () => Misra_2_4(cfg));
            ExecuteCheck(207, () => Misra_2_7(cfg));
            if (cfgNumber == 0)
            {
                ExecuteCheck(301, () => Misra_3_1(raw));
                ExecuteCheck(401, () => Misra_4_1(raw));
                ExecuteCheck(402, () => Misra_4_2(raw));
            }
            ExecuteCheck(501, () => Misra_5_1(cfg));
            ExecuteCheck(502, () => Misra_5_2(cfg));
            ExecuteCheck(504, () => Misra_5_4(cfg));
            ExecuteCheck(505, () => Misra_5_5(cfg));
            ExecuteCheck(507, () => Misra_2_4(cfg)); // 원본 misra_5_7도 같은 요약 함수(1회만 실행)를 호출한다
            ExecuteCheck(601, () => Misra_6_1(cfg));
            ExecuteCheck(602, () => Misra_6_2(cfg));
            if (cfgNumber == 0) ExecuteCheck(701, () => Misra_7_1(raw));
            ExecuteCheck(702, () => Misra_7_2(cfg));
            if (cfgNumber == 0) ExecuteCheck(703, () => Misra_7_3(raw));
            ExecuteCheck(704, () => Misra_7_4(cfg));
            ExecuteCheck(801, () => Misra_8_1(cfg));
            if (cfgNumber == 0) ExecuteCheck(802, () => Misra_8_2(cfg, raw));
            ExecuteCheck(804, () => Misra_8_4(cfg));
            ExecuteCheck(808, () => Misra_8_8(cfg));
            ExecuteCheck(809, () => Misra_8_9(cfg));
            ExecuteCheck(810, () => Misra_8_10(cfg));
            ExecuteCheck(811, () => Misra_8_11(cfg));
            ExecuteCheck(812, () => Misra_8_12(cfg));
            if (cfgNumber == 0) ExecuteCheck(814, () => Misra_8_14(raw));
            ExecuteCheck(902, () => Misra_9_x(cfg, 902));
            ExecuteCheck(903, () => Misra_9_x(cfg, 903));
            ExecuteCheck(904, () => Misra_9_x(cfg, 904));
            if (cfgNumber == 0) ExecuteCheck(905, () => Misra_9_x(cfg, 905, raw));
            ExecuteCheck(1001, () => Misra_10_1(cfg));
            ExecuteCheck(1002, () => Misra_10_2(cfg));
            ExecuteCheck(1003, () => Misra_10_3(cfg));
            ExecuteCheck(1004, () => Misra_10_4(cfg));
            ExecuteCheck(1005, () => Misra_10_5(cfg));
            ExecuteCheck(1006, () => Misra_10_6(cfg));
            ExecuteCheck(1007, () => Misra_10_7(cfg));
            ExecuteCheck(1008, () => Misra_10_8(cfg));
            ExecuteCheck(1101, () => Misra_11_1(cfg));
            ExecuteCheck(1102, () => Misra_11_2(cfg));
            ExecuteCheck(1103, () => Misra_11_3(cfg));
            ExecuteCheck(1104, () => Misra_11_4(cfg));
            ExecuteCheck(1105, () => Misra_11_5(cfg));
            ExecuteCheck(1106, () => Misra_11_6(cfg));
            ExecuteCheck(1107, () => Misra_11_7(cfg));
            ExecuteCheck(1108, () => Misra_11_8(cfg));
            ExecuteCheck(1109, () => Misra_11_9(cfg));
            if (cfgNumber == 0) ExecuteCheck(1201, () => Misra_12_1_sizeof(raw));
            ExecuteCheck(1201, () => Misra_12_1(cfg));
            ExecuteCheck(1202, () => Misra_12_2(cfg));
            ExecuteCheck(1203, () => Misra_12_3(cfg));
            ExecuteCheck(1204, () => Misra_12_4(cfg));
            ExecuteCheck(1301, () => Misra_13_1(cfg));
            ExecuteCheck(1303, () => Misra_13_3(cfg));
            ExecuteCheck(1304, () => Misra_13_4(cfg));
            ExecuteCheck(1305, () => Misra_13_5(cfg));
            ExecuteCheck(1306, () => Misra_13_6(cfg));
            ExecuteCheck(1401, () => Misra_14_1(cfg));
            ExecuteCheck(1402, () => Misra_14_2(cfg));
            ExecuteCheck(1404, () => Misra_14_4(cfg));
            ExecuteCheck(1501, () => Misra_15_1(cfg));
            ExecuteCheck(1502, () => Misra_15_2(cfg));
            ExecuteCheck(1503, () => Misra_15_3(cfg));
            ExecuteCheck(1504, () => Misra_15_4(cfg));
            ExecuteCheck(1505, () => Misra_15_5(cfg));
            if (cfgNumber == 0) ExecuteCheck(1506, () => Misra_15_6(raw));
            ExecuteCheck(1507, () => Misra_15_7(cfg));
            ExecuteCheck(1601, () => Misra_16_1(cfg));
            ExecuteCheck(1602, () => Misra_16_2(cfg));
            if (cfgNumber == 0) ExecuteCheck(1603, () => Misra_16_3(raw));
            ExecuteCheck(1604, () => Misra_16_4(cfg));
            ExecuteCheck(1605, () => Misra_16_5(cfg));
            ExecuteCheck(1606, () => Misra_16_6(cfg));
            ExecuteCheck(1607, () => Misra_16_7(cfg));
            ExecuteCheck(1701, () => Misra_17_1(cfg));
            ExecuteCheck(1702, () => Misra_17_2(cfg));
            ExecuteCheck(1703, () => Misra_17_3(cfg));
            MisraConfig(cfg);
            if (cfgNumber == 0) ExecuteCheck(1706, () => Misra_17_6(raw));
            ExecuteCheck(1707, () => Misra_17_7(cfg));
            ExecuteCheck(1708, () => Misra_17_8(cfg));
            ExecuteCheck(1804, () => Misra_18_4(cfg));
            ExecuteCheck(1805, () => Misra_18_5(cfg));
            ExecuteCheck(1807, () => Misra_18_7(cfg));
            ExecuteCheck(1808, () => Misra_18_8(cfg));
            ExecuteCheck(1902, () => Misra_19_2(cfg));
            ExecuteCheck(2001, () => Misra_20_1(cfg));
            ExecuteCheck(2002, () => Misra_20_2(cfg));
            ExecuteCheck(2003, () => Misra_20_3(cfg));
            ExecuteCheck(2004, () => Misra_20_4(cfg));
            ExecuteCheck(2005, () => Misra_20_5(cfg));
            ExecuteCheck(2007, () => Misra_20_7(cfg));
            ExecuteCheck(2008, () => Misra_20_8(cfg));
            ExecuteCheck(2009, () => Misra_20_9(cfg));
            ExecuteCheck(2010, () => Misra_20_10(cfg));
            ExecuteCheck(2011, () => Misra_20_11(cfg));
            ExecuteCheck(2012, () => Misra_20_12(cfg));
            ExecuteCheck(2013, () => Misra_20_13(cfg));
            ExecuteCheck(2014, () => Misra_20_14(cfg));
            ExecuteCheck(2101, () => Misra_21_1(cfg));
            ExecuteCheck(2102, () => Misra_21_2(cfg));
            ExecuteCheck(2103, () => Misra_21_3(cfg));
            ExecuteCheck(2104, () => Misra_21_4(cfg));
            ExecuteCheck(2105, () => Misra_21_5(cfg));
            ExecuteCheck(2106, () => Misra_21_6(cfg));
            ExecuteCheck(2107, () => Misra_21_7(cfg));
            ExecuteCheck(2108, () => Misra_21_8(cfg));
            ExecuteCheck(2109, () => Misra_21_9(cfg));
            ExecuteCheck(2110, () => Misra_21_10(cfg));
            ExecuteCheck(2111, () => Misra_21_11(cfg));
            ExecuteCheck(2112, () => Misra_21_12(cfg));
            ExecuteCheck(2114, () => Misra_21_14(cfg));
            ExecuteCheck(2115, () => Misra_21_15(cfg));
            ExecuteCheck(2116, () => Misra_21_16(cfg));
            ExecuteCheck(2119, () => Misra_21_19(cfg));
            ExecuteCheck(2120, () => Misra_21_20(cfg));
            ExecuteCheck(2121, () => Misra_21_21(cfg));
            ExecuteCheck(2205, () => Misra_22_5(cfg));
            ExecuteCheck(2207, () => Misra_22_7(cfg));
            ExecuteCheck(2208, () => Misra_22_8(cfg));
            ExecuteCheck(2209, () => Misra_22_9(cfg));
            ExecuteCheck(2210, () => Misra_22_10(cfg));
            cfgNumber++;
        }
    }

    // ------------------------------------------------------------------ 필수 타입 (typeBits 사용)

    private string? GetEssentialTypeFromValue(long? value, bool isSigned)
    {
        if (value == null) return null;
        long v = value.Value;
        foreach (var t in new[] { "char", "short", "int", "long", "long long" })
        {
            int bits = BitsOfEssentialType(t);
            if (bits >= 64) continue;
            Int128 rangeMin, rangeMax;
            if (isSigned)
            {
                rangeMin = -((Int128)1 << (bits - 1));
                rangeMax = ((Int128)1 << (bits - 1)) - 1;
            }
            else
            {
                rangeMin = 0;
                rangeMax = ((Int128)1 << bits) - 1;
            }
            string sign = isSigned ? "signed" : "unsigned";
            if (isSigned && v < 0 && v >= rangeMin) return $"{sign} {t}";
            if (v >= 0 && v <= rangeMax) return $"{sign} {t}";
        }
        return null;
    }

    private string? GetEssentialType(Token? expr)
    {
        if (expr == null) return null;

        // Appendix D, D.6 문자 상수
        if (expr.Str[0] == '\'' && expr.Str[^1] == '\'')
        {
            if (expr.Str.Length == 3 || (expr.Str.Length == 4 && expr.Str[1] == '\\')) return "char";
            return $"{Py.Str(expr.ValueType!.Sign)} {Py.Str(expr.ValueType.Type)}";
        }

        if (expr.Variable != null || MisraHelpers.IsCast(expr))
        {
            var typeToken = expr.Variable != null ? expr.Variable.TypeStartToken : expr.Next;
            while (typeToken != null && typeToken.IsName)
            {
                if (typeToken.Str == "char" && !typeToken.IsSigned && !typeToken.IsUnsigned) return "char";
                typeToken = typeToken.Next;
            }
            if (expr.ValueType != null)
            {
                if (expr.ValueType.Type == "bool") return "bool";
                if (expr.ValueType.IsFloat()) return expr.ValueType.Type;
                if (expr.ValueType.IsIntegral())
                {
                    if (expr.ValueType.Sign == null && expr.ValueType.Type == "char") return "char";
                    return $"{Py.Str(expr.ValueType.Sign)} {Py.Str(expr.ValueType.Type)}";
                }
            }
        }
        else if (expr.IsNumber)
        {
            var vt = expr.ValueType!;
            if (vt.Type == "bool") return "bool";
            if (vt.IsFloat()) return vt.Type;
            if (vt.IsIntegral())
            {
                if (vt.Type != "int") return $"{Py.Str(vt.Sign)} {Py.Str(vt.Type)}";
                return GetEssentialTypeFromValue(expr.GetKnownIntValue(), vt.Sign == "signed");
            }
        }
        else if (expr.Str is "<" or "<=" or ">=" or ">" or "==" or "!=" or "&&" or "||" or "!")
        {
            return "bool";
        }
        else if (expr.AstOperand1 != null && expr.AstOperand2 != null &&
                 expr.Str is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or ">>" or "<<" or "?" or ":")
        {
            if (expr.AstOperand1.ValueType != null && expr.AstOperand1.ValueType.Pointer > 0) return null;
            if (expr.AstOperand2.ValueType != null && expr.AstOperand2.ValueType.Pointer > 0) return null;
            var e1 = GetEssentialType(expr.AstOperand1);
            var e2 = GetEssentialType(expr.AstOperand2);
            if (e1 == null || e2 == null) return null;
            if (MisraHelpers.IsConstantIntegerExpression(expr))
            {
                var sign1 = Py.FirstWord(e1);
                var sign2 = Py.FirstWord(e2);
                if (sign1 == sign2 && sign1 is "signed" or "unsigned")
                {
                    var e = GetEssentialTypeFromValue(expr.GetKnownIntValue(), sign1 == "signed");
                    if (!string.IsNullOrEmpty(e)) return e;
                }
            }
            if (BitsOfEssentialType(e2) >= BitsOfEssentialType(e1)) return e2;
            return e1;
        }
        else if (expr.Str == "~")
        {
            return GetEssentialType(expr.AstOperand1);
        }
        return null;
    }

    private int BitsOfEssentialType(string? ty)
    {
        if (ty == null) return 0;
        string lastType = Py.LastWord(ty);
        if (lastType == "Boolean") return 1;
        if (lastType == "char") return _bitsChar;
        if (lastType == "short") return _bitsShort;
        if (lastType == "int") return _bitsInt;
        if (ty.EndsWith("long long", StringComparison.Ordinal)) return _bitsLongLong;
        if (lastType == "long") return _bitsLong;
        foreach (var sty in MisraTables.STDINT_TYPES)
            if (ty == sty) return int.Parse(new string(sty.Where(char.IsDigit).ToArray()));
        return 0;
    }
}
