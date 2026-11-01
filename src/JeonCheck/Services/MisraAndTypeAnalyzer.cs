using System.IO;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class MisraAndTypeAnalyzer
{
    private static readonly HashSet<string> DynamicMemFunctions = new(StringComparer.Ordinal)
    {
        "malloc", "calloc", "realloc", "free"
    };

    private static readonly HashSet<string> StdioFunctions = new(StringComparer.Ordinal)
    {
        "printf", "scanf", "sprintf", "snprintf", "sscanf",
        "vprintf", "vfprintf", "vsprintf", "vsnprintf", "vscanf", "vfscanf", "vsscanf",
        "fopen", "freopen", "fclose", "fflush", "setbuf", "setvbuf",
        "fprintf", "fscanf",
        "fgetc", "fgets", "fputc", "fputs", "getc", "getchar", "gets", "putc", "putchar", "puts", "ungetc",
        "fread", "fwrite", "fgetpos", "fseek", "fsetpos", "ftell", "rewind",
        "clearerr", "feof", "ferror", "perror",
        "remove", "rename", "tmpfile", "tmpnam"
    };

    public List<AnalysisIssue> Analyze(
        DumpDocument dumpDoc,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById)
    {
        var issues = new List<AnalysisIssue>();
        var emittedKeys = new HashSet<string>();
        var platform = dumpDoc.Platform;

        foreach (var unit in dumpDoc.Configurations)
        {
            // MISRA 13.5용 함수별 지속 부작용 판정 캐시 (설정 단위)
            var sideEffectCache = new Dictionary<string, string?>();

            // 1. 전처리기 지시문 검사 (MISRA 20.5 #undef)
            foreach (var dir in unit.Directives)
            {
                if (!IsInTargetFiles(dir.File, targetFilesNormalized))
                    continue;

                if (dir.Str.StartsWith("#undef", StringComparison.Ordinal))
                {
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-20.5", dir.File, dir.LineNr, 1,
                        $"전처리기 지시문 '{dir.Str}'이 사용되었습니다. 매크로 재정의/해제(#undef)는 지양해야 합니다.");
                }
            }

            // 2. Switch 스코프 기반 검사 (MISRA 16.3 break 누락, MISRA 16.4 default 누락)
            AnalyzeSwitchScopes(unit, targetFilesNormalized, rulesById, issues, emittedKeys);

            // 2-1. 함수 선언/정의 및 함수 포인터의 매개변수 프로토타입 형식 검사 (MISRA 8.2)
            AnalyzeFunctionPrototypes(unit, targetFilesNormalized, rulesById, issues, emittedKeys);

            // 2-2. 함수 직접/간접 재귀 호출 검사 (MISRA 17.2, misra.py misra_17_2)
            AnalyzeRecursiveCalls(unit, targetFilesNormalized, rulesById, issues, emittedKeys);

            // 3. 토큰 및 AST 순회 검사
            foreach (var tok in unit.Tokens)
            {
                if (!IsInTargetFiles(tok.File, targetFilesNormalized))
                    continue;

                // 3.1 MISRA 15.6: 제어문(if/else/for/while) 중괄호 { } 누락 (Cppcheck isInsertedBrace, 단 'else if'는 예외)
                if (tok.Str == "{" && tok.IsInsertedBrace)
                {
                    if (tok.Previous?.Str == "else" && tok.Next?.Str == "if")
                        continue;

                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-15.6", tok.File, tok.LineNr, tok.Column,
                        "제어문(if/else/for/while)의 본문이 중괄호 '{ }'로 감싸져 있지 않습니다.");
                    continue;
                }

                // 3.2 MISRA 15.1: goto 문 사용
                if (tok.Str == "goto" && tok.Type == "name")
                {
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-15.1", tok.File, tok.LineNr, tok.Column,
                        "'goto' 문이 사용되었습니다. 구조화된 제어 흐름을 사용하십시오.");
                }

                // 3.3 대입문 (=) 검사: 암묵적 축소 변환, 부호 변환, 포인터-정수 변환, MISRA 10.3 / 11.5 / 13.4
                if (tok.Str == "=" && tok.IsAssignmentOp && tok.AstOperand1 != null && tok.AstOperand2 != null)
                {
                    AnalyzeAssignment(tok, platform, rulesById, issues, emittedKeys);
                }

                // 3.4 return 문 검사: 반환 시 암묵적 축소 변환 및 부호 변환
                if (tok.Str == "return" && tok.AstOperand1 != null)
                {
                    AnalyzeReturnStatement(tok, platform, rulesById, issues, emittedKeys);
                }

                // 3.5 명시적 캐스트 검사: explicitNarrowingCast, pointerSizeMismatch, MISRA 11.3 / 11.4 / 11.5 / 11.8
                if (tok.IsCast && tok.AstOperand1 != null)
                {
                    AnalyzeExplicitCast(tok, platform, rulesById, issues, emittedKeys);
                }

                // 3.6 함수 호출 검사: MISRA 11.8(포인터 인자 const 제거), 17.7(반환값 무시), 21.3(동적할당), 21.6(표준입출력)
                if (tok.Str == "(" && !tok.IsCast && tok.AstOperand1 != null && tok.AstOperand1.Type == "name")
                {
                    AnalyzeFunctionCall(tok, unit, platform, rulesById, issues, emittedKeys);
                }

                // 3.7 비트 연산 및 시프트 연산 검사: MISRA 10.1, 12.2
                if (tok.Str is "<<" or ">>" or "&" or "|" or "^" or "~")
                {
                    AnalyzeBitwiseAndShift(tok, platform, rulesById, issues, emittedKeys);
                }

                // 3.7-1 연산자 우선순위 괄호 명시 검사: MISRA 12.1 (misra.py misra_12_1 & misra_12_1_sizeof)
                AnalyzeOperatorPrecedence(tok, rulesById, issues, emittedKeys);

                // 3.8 이항 산술/비트/비교/삼항 연산자의 필수 타입 범주 불일치 검사: MISRA 10.4 (misra.py misra_10_4)
                if (tok.AstOperand1 != null && tok.AstOperand2 != null &&
                    (tok.IsComparisonOp || tok.Str is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "+=" or "-=" or ":"))
                {
                    AnalyzeBinarySignMismatch(tok, platform, rulesById, issues, emittedKeys);
                }

                // 3.9 논리 연산자 우변 부작용 검사: MISRA 13.5
                if (tok.Str is "&&" or "||" && tok.AstOperand2 != null)
                {
                    string? effect = FindPersistentSideEffect(tok.AstOperand2, unit, sideEffectCache, 0);
                    if (effect != null)
                    {
                        AddIssue(issues, emittedKeys, rulesById, "misra-c2012-13.5", tok.File, tok.LineNr, tok.Column,
                            $"논리 연산자 '{tok.Str}'의 우변 피연산자에 지속적인 부작용({effect})이 있어, 단락 평가로 실행되지 않을 수 있습니다.");
                    }
                }

                // 3.10 sizeof 내부 부작용 검사: MISRA 13.6 (misra.py misra_13_6)
                if (tok.Str == "sizeof")
                {
                    AnalyzeSizeofSideEffect(tok, rulesById, issues, emittedKeys);
                }

                // 3.11 제어문 조건식(if, while) Boolean 검사(MISRA 14.4) 및 for/while 루프 실수 카운터 검사(MISRA 14.1)
                if (tok.Str == "for" && tok.Next?.Str == "(")
                {
                    AnalyzeForLoop(tok, rulesById, issues, emittedKeys);
                }
                else if (tok.Str == "while" && tok.Next?.Str == "(")
                {
                    AnalyzeWhileLoopFloatCounter(tok, rulesById, issues, emittedKeys);
                }

                if (tok.Str is "if" or "while" && tok.Next?.Str == "(" && tok.Next.AstOperand2 != null)
                {
                    var cond = tok.Next.AstOperand2;
                    if (!IsBoolExpression(cond, platform))
                    {
                        AddIssue(issues, emittedKeys, rulesById, "misra-c2012-14.4", tok.File, tok.LineNr, tok.Column,
                            $"'{tok.Str}' 문의 조건식('{cond.Str}')이 명시적인 Boolean 수식이 아닙니다.");
                    }
                }

                // 3.12 매개변수 직접 변경 검사: MISRA 17.8 (misra.py misra_17_8: 모든 대입 연산자 및 ++/--)
                if ((tok.IsAssignmentOp || tok.Str is "++" or "--") &&
                    !tok.IsSplittedVarDeclEq &&
                    tok.AstOperand1?.Variable?.Access == "Argument")
                {
                    string actionDesc = tok.Str == "="
                        ? "새로운 값을 직접 대입하고"
                        : $"'{tok.Str}' 연산으로 값을 직접 변경하고";
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-17.8", tok.File, tok.LineNr, tok.Column,
                        $"함수 매개변수 '{tok.AstOperand1.Str}'에 {actionDesc} 있습니다.");
                }
            }
        }

        return issues;
    }

    private static void AnalyzeAssignment(
        DumpToken eqTok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var lhs = eqTok.AstOperand1!;
        var rhs = eqTok.AstOperand2!;

        // MISRA 13.4 (misra.py misra_13_4 3046~3057행):
        // 배열 지정 초기화('{ [0] = 1 }') 및 ';', ',', '{' 바로 아래의 대입문은 허용하고,
        // 조건식이나 다른 수식 내부에서 대입 연산자('=')의 결과값을 사용하는 경우만 검출
        if (!eqTok.IsSplittedVarDeclEq && eqTok.AstParent != null)
        {
            bool isDesignatedArrayInit = lhs.Str == "[" && lhs.Previous?.Str is "{" or ",";
            if (!isDesignatedArrayInit && eqTok.AstParent.Str is not (";" or "," or "{"))
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-13.4", eqTok.File, eqTok.LineNr, eqTok.Column,
                    "대입 연산자('=')의 결과값을 다른 수식이나 조건식 내부에서 직접 사용하고 있습니다.");
            }
        }

        // MISRA 11.5 (misra.py misra_11_5 2777~2783행):
        // 명시적 캐스트 없이 'void*' 포인터를 객체 포인터 변수에 암묵적으로 대입하는 경우 검출 (NULL/0 상수 제외)
        if (!rhs.IsCast &&
            lhs.ValuePointer > 0 &&
            !string.IsNullOrEmpty(lhs.ValueType) && lhs.ValueType != "void" &&
            rhs.ValuePointer == lhs.ValuePointer &&
            rhs.ValueType == "void" &&
            !IsNullPointerConstant(rhs))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.5", eqTok.File, eqTok.LineNr, eqTok.Column,
                $"'void*' 포인터 수식('{rhs.Str}')이 객체 포인터('{lhs.FormatTypeDisplay(platform)}') 변수('{lhs.Str}')에 변환되었습니다.");
        }

        // 우변에 명시적 캐스트가 있는 경우는 암묵적 변환 검사에서 제외 (명시적 캐스트는 AnalyzeExplicitCast에서 처리)
        if (rhs.IsCast)
            return;

        // MISRA 필수 타입(Essential Type) 기준 비교: 정수 승격 결과(int)가 아닌 원래 타입으로 판정
        var lhsType = EssentialTypeResolver.Resolve(lhs, platform);
        var rhsType = EssentialTypeResolver.Resolve(rhs, platform);
        if (lhsType == null || rhsType == null)
            return;

        CheckImplicitConversion(
            lhsType.Value, rhsType.Value, rhs, platform, rulesById, issues, emittedKeys,
            eqTok.File, eqTok.LineNr, eqTok.Column,
            misraContext: $"변수 '{lhs.Str}'에 대입되었습니다",
            narrowIntMsg: (l, r) => $"암묵적 축소 대입: 필수 타입 '{r.Display}' 수식('{rhs.Str}')이 명시적 캐스트 없이 더 작은 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다.",
            narrowFloatMsg: (l, r) => $"암묵적 실수 축소 대입: '{r.Display}' 값('{rhs.Str}')이 명시적 캐스트 없이 더 좁은 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다.",
            floatToIntMsg: (l, r) => $"부동소수점-정수 암묵적 변환: 실수형 '{r.Display}' 수식이 명시적 캐스트 없이 정수형 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다 (소수점 이하 절삭).",
            signMsg: (l, r) => $"암묵적 부호 변환: 필수 타입 '{r.Display}' 수식('{rhs.Str}')이 부호가 다른 '{l.Display}' 변수('{lhs.Str}')에 명시적 캐스트 없이 대입되었습니다.");
    }

    private static bool IsNullPointerConstant(DumpToken tok)
    {
        if (tok.MacroName == "NULL" || tok.Str is "NULL" or "0")
            return true;
        return EssentialTypeResolver.TryEvalConstInt(tok, out long v) && v == 0;
    }

    private static void AnalyzeReturnStatement(
        DumpToken retTok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var expr = retTok.AstOperand1!;
        if (expr.IsCast)
            return;

        // return 토큰의 값 타입은 함수의 선언된 반환형
        var retType = EssentialTypeResolver.FromValueType(retTok, platform, isPlainChar: false);
        var exprType = EssentialTypeResolver.Resolve(expr, platform);
        if (retType == null || exprType == null)
            return;

        CheckImplicitConversion(
            retType.Value, exprType.Value, expr, platform, rulesById, issues, emittedKeys,
            retTok.File, retTok.LineNr, retTok.Column,
            misraContext: "함수 반환형으로 반환되었습니다",
            narrowIntMsg: (l, r) => $"함수 반환 시 암묵적 축소 변환: 필수 타입 '{r.Display}' 수식이 더 작은 함수 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            narrowFloatMsg: (l, r) => $"함수 반환 시 암묵적 실수 축소 변환: '{r.Display}' 수식이 더 좁은 함수 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            floatToIntMsg: (l, r) => $"함수 반환 시 부동소수점-정수 암묵적 변환: 실수형 '{r.Display}' 수식이 정수형 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            signMsg: (l, r) => $"함수 반환 시 암묵적 부호 변환: 필수 타입 '{r.Display}' 수식이 부호가 다른 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.");
    }

    /// <summary>
    /// 대입/반환/인자 전달 공통 암묵적 변환 검사. 규칙별 역할:
    /// - implicitNarrowing: 비트 폭이 줄어드는 축소 변환(정수 축소, double→float, 실수→정수)으로 인한 데이터 손실
    /// - implicitSignConversion: 부호 있는/없는 정수 간 변환
    /// - misra-c2012-10.3: MISRA 정의 전체 (더 좁은 필수 타입 또는 다른 필수 타입 범주로의 대입)
    /// </summary>
    private static void CheckImplicitConversion(
        EssentialType target,
        EssentialType source,
        DumpToken sourceExpr,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys,
        string file,
        int line,
        int column,
        string misraContext,
        Func<EssentialType, EssentialType, string> narrowIntMsg,
        Func<EssentialType, EssentialType, string> narrowFloatMsg,
        Func<EssentialType, EssentialType, string> floatToIntMsg,
        Func<EssentialType, EssentialType, string> signMsg)
    {
        // 1) 정수 -> 더 작은 정수 암묵적 축소 변환 (값이 대상 범위에 들어가는 정수 상수 수식은 제외)
        if (target.IsIntegerLike && source.IsIntegerLike && source.Bits > target.Bits)
        {
            if (!IsConstantFittingBits(sourceExpr, target.Bits, target.Category == EssentialCategory.Unsigned))
                AddIssue(issues, emittedKeys, rulesById, "implicitNarrowing", file, line, column, narrowIntMsg(target, source));
        }
        // 2) 실수(double) -> 더 작은 실수(float) 암묵적 축소 변환
        else if (target.Category == EssentialCategory.Floating && source.Category == EssentialCategory.Floating && source.Bits > target.Bits)
        {
            AddIssue(issues, emittedKeys, rulesById, "implicitNarrowing", file, line, column, narrowFloatMsg(target, source));
        }
        // 3) 실수(float/double) -> 정수형 암묵적 변환
        else if (target.IsIntegerLike && source.Category == EssentialCategory.Floating)
        {
            AddIssue(issues, emittedKeys, rulesById, "implicitNarrowing", file, line, column, floatToIntMsg(target, source));
        }

        // 4) 부호 있는 정수 <-> 부호 없는 정수 암묵적 부호 변환
        //    MISRA 10.3 예외: 음수가 아닌 부호 있는 정수 상수 수식을 부호 없는 타입에 대입하는 것은 허용
        //    (값이 범위를 넘는 경우는 위 1)의 축소 변환으로 이미 보고되므로 부호 변환으로 중복 보고하지 않음)
        if (target.IsSignedOrUnsigned && source.IsSignedOrUnsigned && target.Category != source.Category)
        {
            bool allowedConstant = target.Category == EssentialCategory.Unsigned && IsNonNegativeConstant(sourceExpr);
            if (!allowedConstant)
                AddIssue(issues, emittedKeys, rulesById, "implicitSignConversion", file, line, column, signMsg(target, source));
        }

        // 5) MISRA 10.3: 더 좁은 필수 타입 또는 다른 필수 타입 범주로의 대입
        bool categoryDiffers = target.Category != source.Category;
        bool narrower = !categoryDiffers && source.Bits > target.Bits;
        if (categoryDiffers || narrower)
        {
            // 예외 1: 음수가 아닌 부호 있는 정수 상수 수식은 값을 표현할 수 있으면 부호 없는 타입에 대입 가능
            bool nonNegativeConstantToUnsigned =
                target.Category == EssentialCategory.Unsigned && source.Category == EssentialCategory.Signed &&
                IsConstantFittingBits(sourceExpr, target.Bits, targetUnsigned: true);
            // 예외 2: <stdbool.h>의 true/false 매크로(0/1로 전개)는 불리언 값으로 취급 (misra.py와 동일)
            bool boolLiteralToBool =
                target.Category == EssentialCategory.Boolean &&
                (sourceExpr.MacroName is "true" or "false" || sourceExpr.Str is "true" or "false");

            if (!nonNegativeConstantToUnsigned && !boolLiteralToBool)
            {
                string reason = categoryDiffers
                    ? $"다른 필수 타입 범주({CategoryName(source.Category)} → {CategoryName(target.Category)})의"
                    : "더 좁은";
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-10.3", file, line, column,
                    $"MISRA 10.3 위반: 필수 타입 '{source.Display}' 값('{sourceExpr.Str}')이 명시적 캐스트 없이 {reason} '{target.Display}' 타입으로 {misraContext}.");
            }
        }
    }

    private static string CategoryName(EssentialCategory category) => category switch
    {
        EssentialCategory.Boolean => "불리언",
        EssentialCategory.Character => "문자",
        EssentialCategory.Signed => "부호 있는 정수",
        EssentialCategory.Unsigned => "부호 없는 정수",
        EssentialCategory.Floating => "부동소수점",
        _ => category.ToString()
    };

    private static void AnalyzeExplicitCast(
        DumpToken castTok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        if (castTok.ValueType == "void" && castTok.ValuePointer == 0)
            return;

        var src = castTok.AstOperand1!;
        int targetBits = platform.GetBitWidth(castTok.ValueType, castTok.ValuePointer);
        int srcBits = platform.GetBitWidth(src.ValueType, src.ValuePointer);

        // 1) 포인터 <-> 정수 변환 검사 (MISRA 11.4 & pointerSizeMismatch)
        bool targetIsPtr = castTok.ValuePointer > 0;
        bool srcIsPtr = src.ValuePointer > 0;

        if (targetIsPtr != srcIsPtr)
        {
            if (srcIsPtr && !targetIsPtr && (DumpPlatform.IsIntegerType(castTok.ValueType, 0) || castTok.ValueType == "enum") && src.ValueType != "void")
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.4", castTok.File, castTok.LineNr, castTok.Column,
                    $"포인터와 정수 타입 간의 형변환('{src.FormatTypeDisplay(platform)}' -> '{castTok.FormatTypeDisplay(platform)}')이 사용되었습니다.");
            }
            else if (!srcIsPtr && targetIsPtr && (DumpPlatform.IsIntegerType(src.ValueType, 0) || src.ValueType == "enum") && castTok.ValueType != "void" && !IsNullPointerConstant(src))
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.4", castTok.File, castTok.LineNr, castTok.Column,
                    $"포인터와 정수 타입 간의 형변환('{src.FormatTypeDisplay(platform)}' -> '{castTok.FormatTypeDisplay(platform)}')이 사용되었습니다.");
            }

            if (srcIsPtr && !targetIsPtr && targetBits > 0 && targetBits < platform.PointerBit)
            {
                AddIssue(issues, emittedKeys, rulesById, "pointerSizeMismatch", castTok.File, castTok.LineNr, castTok.Column,
                    $"포인터 주소 절삭 위험: {platform.PointerBit}-bit 포인터를 더 작은 {targetBits}-bit 정수형으로 변환하면 상위 주소 비트가 손실됩니다.");
            }
            return;
        }

        // 2) 포인터 <-> 포인터 변환 검사 (MISRA 11.3, 11.5, 11.8)
        if (targetIsPtr && srcIsPtr)
        {
            // MISRA 11.8 (misra.py misra_11_8 2840~2843행):
            // 포인터 변수 자체의 const(bit 1)가 아니라 가리키는 대상(pointee, bit 0: constness % 2)의 const/volatile 제거만 검출
            bool srcPointeeConst = (src.ValueConstness % 2) != 0;
            bool targetPointeeConst = (castTok.ValueConstness % 2) != 0;
            bool srcPointeeVolatile = (src.ValueVolatileness % 2) != 0;
            bool targetPointeeVolatile = (castTok.ValueVolatileness % 2) != 0;

            if ((srcPointeeConst && !targetPointeeConst) || (srcPointeeVolatile && !targetPointeeVolatile))
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.8", castTok.File, castTok.LineNr, castTok.Column,
                    "포인터 캐스트를 통해 가리키는 대상의 'const' 또는 'volatile' 한정자를 제거했습니다.");
            }

            if (src.ValueType == "void" && castTok.ValueType != "void")
            {
                // misra.py misra_11_5(2785~2787행): malloc/calloc/realloc 반환값 캐스트는 21.3과 중복 보고하지 않음
                if (src.AstOperand1 != null && DynamicMemFunctions.Contains(src.AstOperand1.Str))
                    return;

                if (!IsNullPointerConstant(src))
                {
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.5", castTok.File, castTok.LineNr, castTok.Column,
                        $"'void*' 포인터에서 객체 포인터('{castTok.FormatTypeDisplay(platform)}')로 변환했습니다.");
                }
            }
            else if (src.ValueType != "void" && castTok.ValueType != "void")
            {
                // MISRA 11.3 공식 예외 (misra.py misra_11_3 2728~2734행):
                // 객체 포인터를 문자형 포인터(char*, signed char*, unsigned char*, uint8_t*)로 변환하는 것은 허용 (단, 포인터 차수가 같은 경우)
                if (castTok.ValuePointer == src.ValuePointer && castTok.ValueType == "char")
                    return;

                bool diffPointerDepth = castTok.ValuePointer != src.ValuePointer;
                bool diffRecordScope = castTok.ValueType == "record" && src.ValueType == "record" &&
                                       !string.IsNullOrEmpty(castTok.ValueTypeScopeId) && !string.IsNullOrEmpty(src.ValueTypeScopeId) &&
                                       castTok.ValueTypeScopeId != src.ValueTypeScopeId;
                bool diffType = castTok.ValueType != src.ValueType ||
                                (!string.IsNullOrEmpty(castTok.OriginalTypeName) && !string.IsNullOrEmpty(src.OriginalTypeName) &&
                                 castTok.OriginalTypeName != src.OriginalTypeName);

                if (diffPointerDepth || diffRecordScope || diffType)
                {
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.3", castTok.File, castTok.LineNr, castTok.Column,
                        $"서로 다른 객체 타입 포인터 간의 강제 형변환('{src.FormatTypeDisplay(platform)}' -> '{castTok.FormatTypeDisplay(platform)}')이 수행되었습니다.");
                }
            }
            return;
        }

        // 3) 일반 수치형 명시적 축소 캐스트 (explicitNarrowingCast): 원본은 필수 타입 비트 폭으로 비교
        var srcEssential = EssentialTypeResolver.Resolve(src, platform);
        int srcEssentialBits = srcEssential?.Bits ?? srcBits;
        if (targetBits > 0 && srcEssentialBits > targetBits && !IsConstantFittingBits(src, targetBits, castTok.ValueSign == "unsigned"))
        {
            AddIssue(issues, emittedKeys, rulesById, "explicitNarrowingCast", castTok.File, castTok.LineNr, castTok.Column,
                $"명시적 축소 캐스트: '{(srcEssential?.Display ?? src.FormatTypeDisplay(platform))}'에서 더 작은 '{castTok.FormatTypeDisplay(platform)}' 타입으로 형변환되었습니다.");
        }
    }

    private static void AnalyzeFunctionCall(
        DumpToken openParenTok,
        DumpCfgUnit unit,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var funcTok = openParenTok.AstOperand1!;
        string funcName = funcTok.Str;

        if (funcName is "if" or "for" or "while" or "switch" or "sizeof" or "return")
            return;

        // 1) MISRA 21.3: 동적 메모리 할당 함수 금지
        if (DynamicMemFunctions.Contains(funcName))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-21.3", funcTok.File, funcTok.LineNr, funcTok.Column,
                $"표준 동적 메모리 할당/해제 함수 '{funcName}()'가 사용되었습니다. 정적 버퍼를 사용하십시오.");
        }

        // 2) MISRA 21.6 (misra.py misra_21_6 3989~3996행): 표준 입출력 함수 사용 금지 (사용자 정의 동명 함수는 제외)
        if (StdioFunctions.Contains(funcName))
        {
            bool isUserDefinedFunction = !string.IsNullOrEmpty(funcTok.FunctionId) &&
                                         unit.FunctionById.TryGetValue(funcTok.FunctionId, out var userFunc) &&
                                         userFunc.Token != null;
            if (!isUserDefinedFunction)
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-21.6", funcTok.File, funcTok.LineNr, funcTok.Column,
                    $"표준 입출력 라이브러리 함수 '{funcName}()'가 사용되었습니다.");
            }
        }

        // 3) 함수 인자 전달 검사: MISRA 11.8(포인터 인자 const 제거) 및 MISRA 10.3(암묵적 축소/부호 변환)
        if (!string.IsNullOrEmpty(funcTok.FunctionId) &&
            unit.FunctionById.TryGetValue(funcTok.FunctionId, out var func) &&
            openParenTok.AstOperand2 != null)
        {
            var callArgs = FlattenCallArguments(openParenTok.AstOperand2);
            int count = Math.Min(callArgs.Count, func.Args.Count); // 가변 인자(...) 부분은 검사하지 않음
            for (int i = 0; i < count; i++)
            {
                var argExpr = callArgs[i];
                var param = func.Args[i];
                if (param == null)
                    continue;

                int argNo = i + 1;

                // MISRA 11.8 (misra.py misra_11_8 2845~2862행): 함수 호출 시 const/volatile 포인터 인자를 비-const/volatile 매개변수로 전달
                if (param.IsPointer && argExpr.ValuePointer > 0)
                {
                    bool argPointeeConst = (argExpr.ValueConstness % 2) != 0;
                    bool paramPointeeConst = (param.Constness % 2) != 0;
                    bool argPointeeVolatile = (argExpr.ValueVolatileness % 2) != 0;
                    bool paramPointeeVolatile = (param.Volatileness % 2) != 0;

                    if ((argPointeeConst && !paramPointeeConst) || (argPointeeVolatile && !paramPointeeVolatile))
                    {
                        AddIssue(issues, emittedKeys, rulesById, "misra-c2012-11.8", argExpr.File, argExpr.LineNr, argExpr.Column,
                            $"함수 '{funcName}()'의 {argNo}번째 인자 전달 시 가리키는 대상의 'const' 또는 'volatile' 한정자가 제거되었습니다.");
                    }
                }

                if (argExpr.IsCast)
                    continue;

                var paramType = EssentialTypeResolver.ResolveParameter(param, platform);
                var argType = EssentialTypeResolver.Resolve(argExpr, platform);
                if (paramType == null || argType == null)
                    continue;

                CheckImplicitConversion(
                    paramType.Value, argType.Value, argExpr, platform, rulesById, issues, emittedKeys,
                    argExpr.File, argExpr.LineNr, argExpr.Column,
                    misraContext: $"'{funcName}()'의 {argNo}번째 매개변수로 전달되었습니다",
                    narrowIntMsg: (l, r) => $"함수 인자 전달 시 암묵적 축소 변환: '{funcName}()'의 {argNo}번째 인자(필수 타입 '{r.Display}')가 명시적 캐스트 없이 더 작은 매개변수 타입 '{l.Display}'으로 전달되었습니다.",
                    narrowFloatMsg: (l, r) => $"함수 인자 전달 시 암묵적 실수 축소 변환: '{funcName}()'의 {argNo}번째 인자('{r.Display}')가 더 좁은 매개변수 타입 '{l.Display}'으로 전달되었습니다.",
                    floatToIntMsg: (l, r) => $"함수 인자 전달 시 부동소수점-정수 암묵적 변환: '{funcName}()'의 {argNo}번째 인자(실수형 '{r.Display}')가 정수형 매개변수 '{l.Display}'으로 전달되었습니다 (소수점 이하 절삭).",
                    signMsg: (l, r) => $"함수 인자 전달 시 암묵적 부호 변환: '{funcName}()'의 {argNo}번째 인자(필수 타입 '{r.Display}')가 부호가 다른 매개변수 타입 '{l.Display}'으로 명시적 캐스트 없이 전달되었습니다.");
            }
        }

        // 4) MISRA 17.7: 반환값이 있는 함수의 결과값 무시
        bool returnsValue = (!string.IsNullOrEmpty(openParenTok.ValueType) && openParenTok.ValueType != "void") ||
                            openParenTok.ValuePointer > 0;
        if (returnsValue && openParenTok.AstParent == null)
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-17.7", funcTok.File, funcTok.LineNr, funcTok.Column,
                $"함수 '{funcName}()'의 반환값('{openParenTok.ValueType}')이 확인되지 않고 무시되었습니다.");
        }
    }

    /// <summary>
    /// MISRA 17.2 (misra.py misra_17_2 3459~3508행): 직접 재귀(f -> f) 및 간접 재귀(f -> g -> f) 호출 그래프 분석
    /// </summary>
    private static void AnalyzeRecursiveCalls(
        DumpCfgUnit unit,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var callsByFuncId = new Dictionary<string, List<(string CalleeId, DumpToken CallTok)>>();

        foreach (var scope in unit.ScopeById.Values.Where(s => s.Type == "Function" && !string.IsNullOrEmpty(s.FunctionId)))
        {
            if (!unit.TokenById.TryGetValue(scope.BodyStartId, out var bodyStart) ||
                !unit.TokenById.TryGetValue(scope.BodyEndId, out var bodyEnd))
                continue;

            var list = new List<(string CalleeId, DumpToken CallTok)>();
            for (var tok = bodyStart.Next; tok != null && tok != bodyEnd; tok = tok.Next)
            {
                if (tok.Str == "(" && !tok.IsCast && tok.AstOperand1 != null && !string.IsNullOrEmpty(tok.AstOperand1.FunctionId))
                {
                    list.Add((tok.AstOperand1.FunctionId, tok.AstOperand1));
                }
            }
            callsByFuncId[scope.FunctionId] = list;
        }

        bool IsRecursiveReachable(string targetFuncId, string currentCalleeId, HashSet<string> visited)
        {
            if (currentCalleeId == targetFuncId)
                return true;
            if (!callsByFuncId.TryGetValue(currentCalleeId, out var nextCalls))
                return false;

            foreach (var (nextCalleeId, _) in nextCalls)
            {
                if (nextCalleeId == targetFuncId)
                    return true;
                if (visited.Add(nextCalleeId) && IsRecursiveReachable(targetFuncId, nextCalleeId, visited))
                    return true;
            }
            return false;
        }

        foreach (var (callerId, calls) in callsByFuncId)
        {
            foreach (var (calleeId, callTok) in calls)
            {
                if (!IsInTargetFiles(callTok.File, targetFilesNormalized))
                    continue;

                var visited = new HashSet<string>();
                if (IsRecursiveReachable(callerId, calleeId, visited))
                {
                    string msg = callerId == calleeId
                        ? $"함수 '{callTok.Str}()'가 자기 자신을 직접 재귀 호출(Recursion)하고 있습니다."
                        : $"함수 '{callTok.Str}()' 호출을 통해 간접 재귀 호출(Indirect Recursion) 경로가 형성됩니다.";
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-17.2", callTok.File, callTok.LineNr, callTok.Column, msg);
                }
            }
        }
    }

    /// <summary>
    /// 함수 호출 '(' 토큰의 astOperand2(인자 AST)를 ',' 기준으로 펼쳐 인자 순서대로 반환
    /// </summary>
    private static List<DumpToken> FlattenCallArguments(DumpToken argsRoot)
    {
        var result = new List<DumpToken>();
        void Walk(DumpToken? t)
        {
            if (t == null)
                return;
            if (t.Str == ",")
            {
                Walk(t.AstOperand1);
                Walk(t.AstOperand2);
            }
            else
            {
                result.Add(t);
            }
        }
        Walk(argsRoot);
        return result;
    }

    /// <summary>
    /// MISRA 8.2: 함수 타입은 이름 있는 매개변수를 가진 프로토타입 형식이어야 함 (misra.py misra_8_2 변환)
    /// - 매개변수가 없는 함수/함수 포인터는 '(void)'로 명시
    /// - 선언/정의의 모든 매개변수에 이름 명시
    /// - K&R(구식) 매개변수 선언 목록 금지
    /// </summary>
    private static void AnalyzeFunctionPrototypes(
        DumpCfgUnit unit,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        void Report(DumpToken tok, string message)
        {
            if (IsInTargetFiles(tok.File, targetFilesNormalized))
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-8.2", tok.File, tok.LineNr, tok.Column, message);
        }

        void CheckZeroArguments(DumpFunction func, DumpToken startCall)
        {
            if (!startCall.IsRemovedVoidParameter && func.Args.Count == 0)
            {
                var at = func.TokenDef?.Next ?? func.TokenDef ?? startCall;
                Report(at, $"매개변수가 없는 함수 '{func.Name}'의 괄호가 비어 있습니다. 인자가 없음을 나타내려면 '(void)'로 명시하십시오.");
            }
        }

        void CheckArgumentNames(DumpFunction func, DumpToken endCall)
        {
            foreach (var arg in func.Args)
            {
                var typeStart = arg?.TypeStartToken;
                if (arg == null || typeStart == null)
                    continue;

                if (arg.NameToken == null && typeStart.Str != "...")
                    Report(typeStart, $"함수 '{func.Name}'의 매개변수('{typeStart.Str}' 타입)에 이름이 없습니다. 모든 매개변수에 타입과 이름을 함께 작성하십시오.");

                if (typeStart.LineNr > endCall.LineNr)
                    Report(typeStart, $"함수 '{func.Name}'에 구식(K&R) 매개변수 선언 목록이 사용되었습니다. 괄호 안에 타입과 이름을 함께 작성하는 프로토타입 형식을 사용하십시오.");
            }
        }

        foreach (var func in unit.FunctionById.Values)
        {
            // 함수 정의(본문) 검사
            var tokenImpl = func.Token;
            if (tokenImpl != null)
            {
                var startCall = tokenImpl.Next;
                var endCall = startCall?.Link;
                if (startCall?.Str != "(" || endCall?.Str != ")")
                    continue;
                CheckZeroArguments(func, startCall);
                CheckArgumentNames(func, endCall);
            }

            // 함수 선언(프로토타입) 검사
            var tokenDef = func.TokenDef;
            if (tokenDef != null)
            {
                var startCall = tokenDef.Next;
                var endCall = startCall?.Link;
                if (startCall?.Str != "(" || endCall?.Str != ")")
                    continue;
                CheckZeroArguments(func, startCall);

                if (tokenImpl != null)
                {
                    // 정의가 있으면 선언의 이름 있는 매개변수 개수가 정의와 같은지 확인
                    int namedInDefinition = func.Args.Count(a => a?.TypeStartToken != null && a.NameToken != null);
                    int namedInDeclaration = 0;
                    for (var t = startCall; t != null && t != endCall; t = t.Next)
                    {
                        if (!string.IsNullOrEmpty(t.VarId) && t.VarId != "0")
                            namedInDeclaration++;
                    }
                    if (namedInDefinition != namedInDeclaration)
                        Report(tokenDef.Next ?? tokenDef, $"함수 '{func.Name}'의 선언에 이름이 없는 매개변수가 있습니다. 선언(프로토타입)의 모든 매개변수에도 이름을 작성하십시오.");
                }
                else
                {
                    // 정의가 없는 선언만 있는 경우 선언 자체로 매개변수 이름 검사
                    CheckArgumentNames(func, endCall);
                }
            }
        }

        // 함수 포인터 변수 선언에서 빈 괄호 'returnType (*ptr)()' 검사 (misra.py 2234~2250행)
        foreach (var variable in unit.VariableById.Values)
        {
            if (!variable.IsPointer || variable.NameToken == null)
                continue;

            var t0 = variable.NameToken.Next;
            var t1 = t0?.Next;
            var t2 = t1?.Next;
            if (t0?.Str == ")" && t1?.Str == "(" && t2?.Str == ")" && !t1.IsRemovedVoidParameter)
            {
                Report(variable.NameToken, $"함수 포인터 '{variable.NameToken.Str}'의 매개변수 괄호가 비어 있습니다. 인자가 없음을 나타내려면 '(void)'로 명시하십시오.");
            }
        }
    }

    private static void AnalyzeBitwiseAndShift(
        DumpToken tok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var op1 = tok.AstOperand1;
        var op2 = tok.AstOperand2;

        // 단항 주소 연산자(&var)는 비트 연산이 아님
        if (tok.Str == "&" && op2 == null)
            return;

        // MISRA 10.1 (misra.py misra_10_1 2413~2435행):
        // 비트 연산('~', '&', '|', '^') 및 시프트 연산('<<', '>>')의 피연산자는 부호 없는(Unsigned) 필수 타입이어야 함
        // (단, 시프트 우변의 양수 정수 상수 리터럴 등은 허용)
        var op1Type = EssentialTypeResolver.Resolve(op1, platform);
        var op2Type = EssentialTypeResolver.Resolve(op2, platform);

        if (op1 != null && op1Type != null && op1Type.Value.Category != EssentialCategory.Unsigned && !IsNonNegativeConstant(op1))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-10.1", tok.File, tok.LineNr, tok.Column,
                $"필수 타입이 부호 없는 정수가 아닌 피연산자('{op1.Str}', {op1Type.Value.Display})에 비트/시프트 연산자('{tok.Str}')를 사용했습니다. 부호 없는(unsigned) 타입을 사용하십시오.");
        }
        else if (op2 != null && op2Type != null && op2Type.Value.Category != EssentialCategory.Unsigned && !IsNonNegativeConstant(op2))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-10.1", tok.File, tok.LineNr, tok.Column,
                $"필수 타입이 부호 없는 정수가 아닌 우변 피연산자('{op2.Str}', {op2Type.Value.Display})에 비트/시프트 연산자('{tok.Str}')를 사용했습니다.");
        }

        // MISRA 12.2: 시프트 비트 폭 범위 확인 (좌변 필수 타입의 비트 폭 기준)
        if (tok.Str is "<<" or ">>" && op1 != null && op2 != null)
        {
            int lhsBits = op1Type?.IsIntegerLike == true ? op1Type.Value.Bits : platform.GetBitWidth(op1.ValueType, 0);
            if (lhsBits > 0 && EssentialTypeResolver.TryEvalConstInt(op2, out long shiftAmount))
            {
                if (shiftAmount < 0 || shiftAmount >= lhsBits)
                {
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-12.2", tok.File, tok.LineNr, tok.Column,
                        $"시프트 연산('{tok.Str}')의 우변 값({shiftAmount})이 좌변 필수 타입의 비트 폭({lhsBits}-bit) 이상이거나 음수입니다.");
                }
            }
        }
    }

    /// <summary>
    /// MISRA 12.1 (misra.py misra_12_1 2878~2910행 & getPrecedence 1093~1166행):
    /// 서로 다른 연산자 우선순위(2~12단계)가 괄호 없이 결합된 경우 및 괄호 없는 sizeof 피연산자 뒤 산술 연산 검출
    /// </summary>
    private static void AnalyzeOperatorPrecedence(
        DumpToken tok,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        // misra_12_1_sizeof: sizeof x + y 형태
        if (tok.Str == "sizeof" && tok.Next != null && tok.Next.Type == "name" &&
            tok.AstParent != null && tok.AstParent.Str is "+" or "-" or "*" or "/" or "%")
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-12.1", tok.File, tok.LineNr, tok.Column,
                "'sizeof' 피연산자에 괄호가 없어 뒤따르는 산술 연산자와의 결합 순서가 모호합니다. 'sizeof(x)' 형태로 괄호를 명시하십시오.");
            return;
        }

        int p = GetOperatorPrecedence(tok);
        if (p < 2 || p > 12)
            return;

        int p1 = GetOperatorPrecedence(tok.AstOperand1);
        if (p < p1 && p1 <= 12 && HasNoParenthesesBetween(tok.AstOperand1!, tok))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-12.1", tok.File, tok.LineNr, tok.Column,
                $"연산자 '{tok.Str}'와 좌변의 '{tok.AstOperand1!.Str}' 연산자가 괄호 없이 함께 사용되었습니다. 괄호로 연산 우선순위를 명시하십시오.");
            return;
        }

        int p2 = GetOperatorPrecedence(tok.AstOperand2);
        if (p < p2 && p2 <= 12 && HasNoParenthesesBetween(tok, tok.AstOperand2!))
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-12.1", tok.File, tok.LineNr, tok.Column,
                $"연산자 '{tok.Str}'와 우변의 '{tok.AstOperand2!.Str}' 연산자가 괄호 없이 함께 사용되었습니다. 괄호로 연산 우선순위를 명시하십시오.");
        }
    }

    private static int GetOperatorPrecedence(DumpToken? expr)
    {
        if (expr == null || expr.AstOperand1 == null || expr.AstOperand2 == null)
            return 16;

        if (expr.Str is "*" or "/" or "%") return 12;
        if (expr.Str is "+" or "-") return 11;
        if (expr.Str is "<<" or ">>") return 10;
        if (expr.Str is "<" or ">" or "<=" or ">=") return 9;
        if (expr.Str is "==" or "!=") return 8;
        if (expr.Str == "&") return 7;
        if (expr.Str == "^") return 6;
        if (expr.Str == "|") return 5;
        if (expr.Str == "&&") return 4;
        if (expr.Str == "||") return 3;
        if (expr.Str is "?" or ":") return 2;
        if (expr.IsAssignmentOp) return 1;
        if (expr.Str == ",") return 0;
        return -1;
    }

    /// <summary>
    /// misra.py numberOfParentheses(tok1, tok2): tok1에서 tok2 사이에 '(' 또는 ')' 토큰이 하나도 없으면 true 반환
    /// </summary>
    private static bool HasNoParenthesesBetween(DumpToken tok1, DumpToken tok2)
    {
        var cur = tok1;
        while (cur != null && cur != tok2)
        {
            if (cur.Str is "(" or ")")
                return false;
            cur = cur.Next;
        }
        return cur == tok2;
    }

    private static bool IsMisra104TargetOp(DumpToken tok)
    {
        return tok.IsComparisonOp || tok.Str is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "+=" or "-=" or ":";
    }

    private static void AnalyzeBinarySignMismatch(
        DumpToken tok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var rawOp1 = tok.AstOperand1!;
        var rawOp2 = tok.AstOperand2!;

        // misra.py misra_10_4(2521~2529행): 연쇄 이항 연산에서 인접한 피연산자끼리 비교
        var op1 = IsMisra104TargetOp(rawOp1) && rawOp1.AstOperand2 != null ? rawOp1.AstOperand2 : rawOp1;
        var op2 = IsMisra104TargetOp(rawOp2) && rawOp2.AstOperand1 != null ? rawOp2.AstOperand1 : rawOp2;

        // misra.py getEssentialCategorylist와 동일: 증감 연산 및 포인터 피연산자는 제외
        if (op1.Str is "++" or "--" || op2.Str is "++" or "--")
            return;
        if (op1.ValuePointer > 0 || op2.ValuePointer > 0)
            return;

        var t1 = EssentialTypeResolver.Resolve(op1, platform);
        var t2 = EssentialTypeResolver.Resolve(op2, platform);
        if (t1 == null || t2 == null)
            return;

        // misra.py 2530~2537행 예외: 문자형(char)과 정수형(signed/unsigned)의 '+', '+=', '-', '-=' 연산은 허용
        if (tok.Str is "+" or "+=")
        {
            if ((t1.Value.Category == EssentialCategory.Character && t2.Value.IsSignedOrUnsigned) ||
                (t2.Value.Category == EssentialCategory.Character && t1.Value.IsSignedOrUnsigned))
                return;
        }
        else if (tok.Str is "-" or "-=")
        {
            if (t1.Value.Category == EssentialCategory.Character && t2.Value.IsSignedOrUnsigned)
                return;
        }

        if (t1.Value.Category == t2.Value.Category)
            return;

        // 음수가 아닌 정수 상수 수식과 unsigned 피연산자 간의 연산은 제외
        if ((t1.Value.Category == EssentialCategory.Unsigned && IsNonNegativeConstant(op2)) ||
            (t2.Value.Category == EssentialCategory.Unsigned && IsNonNegativeConstant(op1)))
            return;

        AddIssue(issues, emittedKeys, rulesById, "misra-c2012-10.4", tok.File, tok.LineNr, tok.Column,
            $"연산자 '{tok.Str}'의 좌변('{op1.Str}': {t1.Value.Display})과 우변('{op2.Str}': {t2.Value.Display})의 필수 타입 범주({CategoryName(t1.Value.Category)} vs {CategoryName(t2.Value.Category)})가 서로 다릅니다.");
    }

    /// <summary>
    /// MISRA 13.6 (misra.py misra_13_6): sizeof 피연산자 내부의 부작용 검사
    /// </summary>
    private static void AnalyzeSizeofSideEffect(
        DumpToken sizeofTok,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        bool hasSideEffect = ContainsSideEffect(sizeofTok.AstOperand1) ||
                             ContainsSideEffect(sizeofTok.Next?.AstOperand1) ||
                             ContainsSideEffect(sizeofTok.Next?.AstOperand2);

        if (!hasSideEffect && sizeofTok.Next?.Str == "(" && sizeofTok.Next.Link != null)
        {
            var endParen = sizeofTok.Next.Link;
            for (var cur = sizeofTok.Next.Next; cur != null && cur != endParen; cur = cur.Next)
            {
                if (cur.IsAssignmentOp || cur.Str is "++" or "--" ||
                    (cur.Str == "(" && !cur.IsCast && cur.AstOperand1?.Type == "name"))
                {
                    hasSideEffect = true;
                    break;
                }
            }
        }

        if (hasSideEffect)
        {
            AddIssue(issues, emittedKeys, rulesById, "misra-c2012-13.6", sizeofTok.File, sizeofTok.LineNr, sizeofTok.Column,
                "'sizeof' 연산자의 피연산자에 실행되지 않는 부작용(증감·대입 연산 또는 함수 호출)이 포함되어 있습니다.");
        }
    }

    /// <summary>
    /// MISRA 14.1 (misra.py misra_14_1 3069~3077행 & getForLoopExpressions/findCounterTokens):
    /// for 문의 종료 조건식(2번째 절)에서 루프 카운터 토큰을 추출하여 부동소수점 타입인지 검사
    /// </summary>
    private static void AnalyzeForLoop(
        DumpToken forTok,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var openParen = forTok.Next;
        if (openParen == null || openParen.Str != "(")
            return;

        // Cppcheck AST에서 for '('의 astOperand2는 첫 번째 ';', 그 astOperand2는 두 번째 ';'
        var semi1 = openParen.AstOperand2;
        if (semi1?.Str == ";" && semi1.AstOperand2?.Str == ";")
        {
            var condExpr = semi1.AstOperand2.AstOperand1;
            var stepExpr = semi1.AstOperand2.AstOperand2;
            foreach (var counter in FindCounterTokens(condExpr))
            {
                if (counter.ValuePointer == 0 && DumpPlatform.IsFloatType(counter.ValueType, 0))
                {
                    // 조건식의 float 변수가 실제로 루프 증감식(3번째 절)에서도 변경되거나 카운터로 쓰이는지 확인 (misra.py findCounterTokens와 동일)
                    AddIssue(issues, emittedKeys, rulesById, "misra-c2012-14.1", counter.File, counter.LineNr, counter.Column,
                        $"for 반복문의 루프 제어 변수 '{counter.Str}'에 부동소수점 타입('{counter.ValueType}')이 사용되었습니다.");
                    return;
                }
            }

            // 증감식(3번째 절)에서 직접 변경되는 float 루프 변수 확인
            if (stepExpr != null && FindFloatModifiedToken(stepExpr) is DumpToken floatStepTok)
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-14.1", floatStepTok.File, floatStepTok.LineNr, floatStepTok.Column,
                    $"for 반복문의 루프 증감 변수 '{floatStepTok.Str}'에 부동소수점 타입('{floatStepTok.ValueType}')이 사용되었습니다.");
            }
        }
    }

    private static List<DumpToken> FindCounterTokens(DumpToken? cond)
    {
        var result = new List<DumpToken>();
        void Walk(DumpToken? t)
        {
            if (t == null)
                return;
            if (t.Str is "&&" or "||")
            {
                Walk(t.AstOperand1);
                Walk(t.AstOperand2);
                return;
            }
            if ((t.IsArithmeticalOp || t.IsComparisonOp) && t.AstOperand1 != null && t.AstOperand2 != null)
            {
                if (t.AstOperand1.Type == "name")
                    result.Add(t.AstOperand1);
                if (t.AstOperand2.Type == "name")
                    result.Add(t.AstOperand2);
                Walk(t.AstOperand1);
                Walk(t.AstOperand2);
            }
        }
        Walk(cond);
        return result;
    }

    private static DumpToken? FindFloatModifiedToken(DumpToken? expr)
    {
        if (expr == null)
            return null;
        if ((expr.IsAssignmentOp || expr.Str is "++" or "--") &&
            expr.AstOperand1 != null &&
            expr.AstOperand1.ValuePointer == 0 &&
            DumpPlatform.IsFloatType(expr.AstOperand1.ValueType, 0))
        {
            return expr.AstOperand1;
        }
        return FindFloatModifiedToken(expr.AstOperand1) ?? FindFloatModifiedToken(expr.AstOperand2);
    }

    /// <summary>
    /// MISRA 14.1 (misra.py isFloatCounterInWhileLoop 998~1030행): while 반복문의 부동소수점 루프 카운터 검사
    /// </summary>
    private static void AnalyzeWhileLoopFloatCounter(
        DumpToken whileTok,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var lpar = whileTok.Next;
        var rpar = lpar?.Link;
        if (lpar?.Str != "(" || rpar == null)
            return;

        var counterTokens = FindCounterTokens(lpar.AstOperand2);
        var counterVarIds = counterTokens
            .Where(t => !string.IsNullOrEmpty(t.VarId) && t.VarId != "0" && t.ValuePointer == 0 && DumpPlatform.IsFloatType(t.ValueType, 0))
            .Select(t => t.VarId)
            .ToHashSet(StringComparer.Ordinal);

        if (counterVarIds.Count == 0)
            return;

        DumpToken? bodyStart = null;
        if (rpar.Next?.Str == "{")
            bodyStart = rpar.Next;
        else if (whileTok.Previous?.Str == "}" && whileTok.Previous.Link?.Previous?.Str == "do")
            bodyStart = whileTok.Previous.Link;

        if (bodyStart?.Link == null)
            return;

        for (var tok = bodyStart.Next; tok != null && tok != bodyStart.Link; tok = tok.Next)
        {
            if (string.IsNullOrEmpty(tok.VarId) || !counterVarIds.Contains(tok.VarId))
                continue;
            if (tok.AstParent == null || tok.ValuePointer > 0 || !DumpPlatform.IsFloatType(tok.ValueType, 0))
                continue;

            var parent = tok.AstParent;
            if (parent.Str is "++" or "--" ||
                (parent.IsAssignmentOp && parent.Str != "=" && parent.AstOperand1 == tok) ||
                (parent.Str == "=" && parent.AstOperand1?.Str == tok.Str && parent.AstOperand1 != tok))
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-14.1", whileTok.File, whileTok.LineNr, whileTok.Column,
                    $"while 반복문의 루프 카운터 변수 '{tok.Str}'에 부동소수점 타입('{tok.ValueType}')이 사용되었습니다.");
                return;
            }
        }
    }

    private static bool IsBoolExpression(DumpToken cond, DumpPlatform platform)
    {
        if (cond.ValueType == "bool" || cond.IsComparisonOp)
            return true;
        if (cond.Str is "!" or "&&" or "||" or "0" or "1" or "true" or "false")
            return true;
        if (cond.MacroName is "true" or "false")
            return true;
        return EssentialTypeResolver.Resolve(cond, platform)?.Category == EssentialCategory.Boolean;
    }

    private static void AnalyzeSwitchScopes(
        DumpCfgUnit unit,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        foreach (var scope in unit.ScopeById.Values.Where(s => s.Type == "Switch"))
        {
            if (!unit.TokenById.TryGetValue(scope.BodyStartId, out var startTok) ||
                !unit.TokenById.TryGetValue(scope.BodyEndId, out var endTok))
                continue;

            if (!IsInTargetFiles(startTok.File, targetFilesNormalized))
                continue;

            bool hasDefault = false;
            DumpToken? currentCaseTok = null;
            bool caseHasStatements = false;
            bool caseTerminated = false;
            bool inTerminatingStmt = false;

            for (var tok = startTok.Next; tok != null && tok != endTok; tok = tok.Next)
            {
                // 중괄호 블록('{ ... }') 처리:
                // - case VKSTR: { ... } 처럼 ':', ';', '{', '}' 바로 뒤에 오는 단순 복합 블록(Unconditional Block)은
                //   내부의 break/return/goto를 그대로 검사한다.
                // - if / else / for / while / switch 등 제어문에 속한 '{ ... }' 블록은 내부의 break(루프/중첩 switch 탈출)나
                //   조건부 return이 현재 case의 무조건 종료로 오판되지 않도록 블록 끝('}')으로 건너뛴다.
                if (tok.Str == "{")
                {
                    string? prevStr = tok.Previous?.Str;
                    bool isUnconditionalBlock = prevStr is ":" or ";" or "{" or "}";
                    if (!isUnconditionalBlock && tok.Link != null)
                    {
                        caseHasStatements = true;
                        // if (...) { return ...; } else { return ...; } 구조에서 양쪽 분기 모두 종료문으로 끝나는 경우만 예외 인정
                        if (prevStr == "else" &&
                            BlockEndsWithTerminator(tok, tok.Link) &&
                            tok.Previous?.Previous?.Str == "}" &&
                            tok.Previous.Previous.Link != null &&
                            BlockEndsWithTerminator(tok.Previous.Previous.Link, tok.Previous.Previous))
                        {
                            caseTerminated = true;
                        }
                        else
                        {
                            caseTerminated = false;
                        }
                        inTerminatingStmt = false;
                        tok = tok.Link;
                    }
                    continue;
                }

                if (tok.Str == "}")
                {
                    // 단순 복합 블록의 닫는 '}'는 직전 구문의 종료 상태(caseTerminated)를 그대로 유지
                    continue;
                }

                if (tok.Str == "case" || tok.Str == "default")
                {
                    if (currentCaseTok != null && caseHasStatements && !caseTerminated)
                    {
                        AddIssue(issues, emittedKeys, rulesById, "misra-c2012-16.3", currentCaseTok.File, currentCaseTok.LineNr, currentCaseTok.Column,
                            $"switch 문의 '{currentCaseTok.Str}' 절이 'break;' 또는 'return;'으로 끝나지 않아 다음 절로 Fall-through 됩니다.");
                    }

                    if (tok.Str == "default")
                        hasDefault = true;

                    currentCaseTok = tok;
                    caseHasStatements = false;
                    caseTerminated = false;
                    inTerminatingStmt = false;

                    // 'case 값 :' 또는 'default :'의 ':' 토큰까지 건너뛰기
                    while (tok.Next != null && tok.Next != endTok && tok.Str != ":")
                        tok = tok.Next;
                    continue;
                }

                if (currentCaseTok != null)
                {
                    if (tok.Str == ";" && tok.Column == 0)
                        continue;

                    if (tok.Str is "break" or "return" or "goto" or "continue")
                    {
                        caseHasStatements = true;
                        inTerminatingStmt = true;
                    }
                    else if (tok.Str == ";")
                    {
                        // misra.py misra_16_3(STATE_BREAK -> ';'에서 STATE_OK 전환)과 동일:
                        // return 1; 또는 return func(a, b); 처럼 반환식이 있는 경우에도 ';'에서 종료 확정
                        if (inTerminatingStmt)
                        {
                            caseTerminated = true;
                            inTerminatingStmt = false;
                        }
                        else
                        {
                            caseTerminated = false;
                        }
                    }
                    else
                    {
                        caseHasStatements = true;
                        if (!inTerminatingStmt)
                        {
                            caseTerminated = false;
                        }
                    }
                }
            }

            if (currentCaseTok != null && caseHasStatements && !caseTerminated)
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-16.3", currentCaseTok.File, currentCaseTok.LineNr, currentCaseTok.Column,
                    $"switch 문의 마지막 '{currentCaseTok.Str}' 절 끝에 'break;'가 누락되었습니다.");
            }

            if (!hasDefault)
            {
                AddIssue(issues, emittedKeys, rulesById, "misra-c2012-16.4", startTok.File, startTok.LineNr, startTok.Column,
                    "switch 문에 예외 값 방어를 위한 'default:' 절이 누락되었습니다.");
            }
        }
    }

    private static bool BlockEndsWithTerminator(DumpToken openBrace, DumpToken closeBrace)
    {
        bool terminated = false;
        bool inTerm = false;
        for (var t = openBrace.Next; t != null && t != closeBrace; t = t.Next)
        {
            if (t.Str == "{" && t.Link != null)
            {
                t = t.Link;
                terminated = false;
                inTerm = false;
                continue;
            }
            if (t.Str is "break" or "return" or "goto" or "continue")
            {
                inTerm = true;
            }
            else if (t.Str == ";")
            {
                terminated = inTerm;
                inTerm = false;
            }
            else if (!inTerm && t.Str != "}")
            {
                terminated = false;
            }
        }
        return terminated;
    }

    /// <summary>
    /// MISRA 13.5용 지속적 부작용(persistent side effect) 탐지. 부작용의 설명을 반환하고, 없으면 null.
    /// - 대입·증감 연산, volatile 객체 접근
    /// - 함수 호출: 같은 소스(번역 단위)에 본문이 있으면 본문을 분석하여 전역/정적 변수, 포인터가 가리키는 대상,
    ///   volatile 객체를 변경하거나 부작용이 있는 함수를 호출하는 경우에만 부작용으로 판정.
    ///   본문을 알 수 없는 외부 함수와 함수 포인터 호출은 부작용이 있을 수 있는 것으로 판정.
    /// </summary>
    private static string? FindPersistentSideEffect(DumpToken? root, DumpCfgUnit unit, Dictionary<string, string?> cache, int depth)
    {
        if (root == null)
            return null;

        if (root.IsAssignmentOp || root.Str is "++" or "--")
            return $"'{root.Str}' 연산으로 값 변경";

        if (root.Variable?.IsVolatile == true)
            return $"volatile 객체 '{root.Str}' 접근";

        if (root.Str == "(" && !root.IsCast && root.AstOperand1?.Type == "name")
        {
            string? callEffect = FunctionSideEffect(root.AstOperand1, unit, cache, depth);
            if (callEffect != null)
                return callEffect;
            return FindPersistentSideEffect(root.AstOperand2, unit, cache, depth); // 인자 수식
        }

        return FindPersistentSideEffect(root.AstOperand1, unit, cache, depth) ??
               FindPersistentSideEffect(root.AstOperand2, unit, cache, depth);
    }

    private static string? FunctionSideEffect(DumpToken funcNameTok, DumpCfgUnit unit, Dictionary<string, string?> cache, int depth)
    {
        string name = funcNameTok.Str;

        if (funcNameTok.Variable != null)
            return $"함수 포인터 '{name}' 호출 (호출 대상을 알 수 없음)";

        if (string.IsNullOrEmpty(funcNameTok.FunctionId) ||
            !unit.FunctionById.TryGetValue(funcNameTok.FunctionId, out var func) ||
            func.Token == null)
            return $"본문을 알 수 없는 함수 '{name}()' 호출";

        if (cache.TryGetValue(func.Id, out var cached))
            return cached;

        if (depth > 8)
            return $"함수 '{name}()' 호출 (호출 깊이가 깊어 분석 생략)";

        // 재귀 호출 대비: 분석 중인 함수는 부작용이 있는 것으로 가정
        cache[func.Id] = $"재귀 호출되는 함수 '{name}()' 호출";
        string? bodyEffect = AnalyzeFunctionBodySideEffect(func, unit, cache, depth + 1);
        string? resultEffect = bodyEffect == null ? null : $"함수 '{name}()' 호출: {bodyEffect}";
        cache[func.Id] = resultEffect;
        return resultEffect;
    }

    private static string? AnalyzeFunctionBodySideEffect(DumpFunction func, DumpCfgUnit unit, Dictionary<string, string?> cache, int depth)
    {
        var scope = unit.ScopeById.Values.FirstOrDefault(sc => sc.Type == "Function" && sc.FunctionId == func.Id);
        if (scope == null ||
            !unit.TokenById.TryGetValue(scope.BodyStartId, out var bodyStart) ||
            !unit.TokenById.TryGetValue(scope.BodyEndId, out var bodyEnd))
            return "본문 범위를 확인할 수 없음";

        for (var t = bodyStart.Next; t != null && t != bodyEnd; t = t.Next)
        {
            if (t.Variable?.IsVolatile == true)
                return $"volatile 객체 '{t.Str}' 접근";

            if (t.IsAssignmentOp || t.Str is "++" or "--")
            {
                var target = t.AstOperand1;
                if (!IsFunctionLocalObject(target))
                {
                    string targetText = target?.Str == "*" && target.AstOperand2 == null && target.AstOperand1 != null
                        ? "*" + target.AstOperand1.Str
                        : target?.Str ?? t.Str;
                    return $"'{targetText}' 변경 (전역/정적 변수 또는 포인터가 가리키는 대상)";
                }
            }

            if (t.Str == "(" && !t.IsCast && t.AstOperand1?.Type == "name")
            {
                string? nested = FunctionSideEffect(t.AstOperand1, unit, cache, depth);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 대입 대상이 함수 안에서만 유효한 객체(정적이 아닌 지역 변수, 값으로 전달된 매개변수, 그 멤버/지역 배열 요소)인지 판정
    /// </summary>
    private static bool IsFunctionLocalObject(DumpToken? target)
    {
        var t = target;
        while (t != null)
        {
            if (t.Str == ".")
            {
                // p->member (포인터 기반 멤버 접근)는 함수 밖 객체를 변경
                if (t.AstOperand1 == null || t.AstOperand1.ValuePointer > 0)
                    return false;
                t = t.AstOperand1;
                continue;
            }

            if (t.Str == "[")
            {
                var baseTok = t.AstOperand1;
                if (baseTok?.Variable == null || !baseTok.Variable.IsArray || baseTok.Variable.IsPointer)
                    return false;
                t = baseTok;
                continue;
            }

            if (t.Variable != null)
            {
                var v = t.Variable;
                if (v.IsStatic)
                    return false;
                return v.Access is "Local" or "Argument";
            }

            return false; // 역참조(*p) 등
        }

        return false;
    }

    private static bool ContainsSideEffect(DumpToken? root)
    {
        if (root == null)
            return false;

        if (root.IsAssignmentOp || root.Str is "++" or "--")
            return true;

        if (root.Str == "(" && !root.IsCast && root.AstOperand1?.Type == "name")
            return true;

        return ContainsSideEffect(root.AstOperand1) || ContainsSideEffect(root.AstOperand2);
    }

    /// <summary>
    /// 정수 상수 수식(리터럴 및 매크로 상수 확장 결과)의 값이 대상 비트 폭 범위 안에 들어가는지 확인
    /// </summary>
    private static bool IsConstantFittingBits(DumpToken tok, int targetBits, bool targetUnsigned)
    {
        return EssentialTypeResolver.TryEvalConstInt(tok, out long val) &&
               EssentialTypeResolver.FitsInBits(val, targetBits, targetUnsigned);
    }

    private static bool IsNonNegativeConstant(DumpToken tok)
    {
        return EssentialTypeResolver.TryEvalConstInt(tok, out long val) && val >= 0;
    }

    private static bool IsInTargetFiles(string filePath, HashSet<string> targetFilesNormalized)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        return targetFilesNormalized.Contains(AnalysisIssue.NormalizePath(filePath));
    }

    private static void AddIssue(
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys,
        Dictionary<string, RuleInfo> rulesById,
        string ruleId,
        string filePath,
        int line,
        int column,
        string message)
    {
        if (!rulesById.TryGetValue(ruleId, out var rule) || !rule.IsEnabled)
            return;

        // 같은 위치(줄+열)의 중복만 제거 (#ifdef 설정별 반복, 여러 .c에 포함된 헤더의 반복 보고)
        string dedupKey = AnalysisIssue.MakePositionKey(ruleId, filePath, line, column);
        if (!emittedKeys.Add(dedupKey))
            return;

        issues.Add(new AnalysisIssue
        {
            RuleId = ruleId,
            Category = rule.Category,
            Severity = rule.Severity,
            Obligation = rule.Obligation,
            Title = rule.Title,
            Message = message,
            FilePath = Path.GetFullPath(filePath),
            Line = line,
            Column = column,
            IsInfoOrNote = false,
            RuleDetails = rule
        });
    }
}
