using System.IO;
using JeonCheck.Models;

namespace JeonCheck.Services;

/// <summary>
/// 자체 타입 변환 검사 (MISRA 규칙이 아닌 전검사 고유 진단).
/// - implicitNarrowing: 대입·반환·인자 전달 시 비트 폭이 줄어드는 암묵적 변환
/// - implicitSignConversion: 대입·반환·인자 전달 시 부호 있는/없는 정수 간 암묵적 변환
/// - explicitNarrowingCast: 더 작은 수치형으로의 명시적 캐스트
/// - pointerSizeMismatch: 포인터를 더 작은 정수형으로 캐스트
/// MISRA C 2012 규칙은 Misra.MisraChecker(원본 misra.py 이식)가 검사한다.
/// </summary>
public class TypeConversionAnalyzer
{
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
            foreach (var tok in unit.Tokens)
            {
                if (!IsInTargetFiles(tok.File, targetFilesNormalized))
                    continue;

                // 대입문 (=)
                if (tok.Str == "=" && tok.IsAssignmentOp && tok.AstOperand1 != null && tok.AstOperand2 != null)
                    AnalyzeAssignment(tok, platform, rulesById, issues, emittedKeys);

                // return 문
                if (tok.Str == "return" && tok.AstOperand1 != null)
                    AnalyzeReturnStatement(tok, platform, rulesById, issues, emittedKeys);

                // 명시적 캐스트
                if (tok.IsCast && tok.AstOperand1 != null)
                    AnalyzeExplicitCast(tok, platform, rulesById, issues, emittedKeys);

                // 함수 호출 인자 전달
                if (tok.Str == "(" && !tok.IsCast && tok.AstOperand1 != null && tok.AstOperand1.Type == "name")
                    AnalyzeFunctionCall(tok, unit, platform, rulesById, issues, emittedKeys);
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

        // 필수 타입(Essential Type) 기준 비교: 정수 승격 결과(int)가 아닌 원래 타입으로 판정
        var lhsType = EssentialTypeResolver.Resolve(lhs, platform);
        var rhsType = EssentialTypeResolver.Resolve(rhs, platform);
        if (lhsType == null || rhsType == null)
            return;

        CheckImplicitConversion(
            lhsType.Value, rhsType.Value, rhs, rulesById, issues, emittedKeys,
            eqTok.File, eqTok.LineNr, eqTok.Column,
            narrowIntMsg: (l, r) => $"암묵적 축소 대입: 필수 타입 '{r.Display}' 수식('{rhs.Str}')이 명시적 캐스트 없이 더 작은 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다.",
            narrowFloatMsg: (l, r) => $"암묵적 실수 축소 대입: '{r.Display}' 값('{rhs.Str}')이 명시적 캐스트 없이 더 좁은 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다.",
            floatToIntMsg: (l, r) => $"부동소수점-정수 암묵적 변환: 실수형 '{r.Display}' 수식이 명시적 캐스트 없이 정수형 '{l.Display}' 변수('{lhs.Str}')에 대입되었습니다 (소수점 이하 절삭).",
            signMsg: (l, r) => $"암묵적 부호 변환: 필수 타입 '{r.Display}' 수식('{rhs.Str}')이 부호가 다른 '{l.Display}' 변수('{lhs.Str}')에 명시적 캐스트 없이 대입되었습니다.");
    }

    private static void AnalyzeReturnStatement(
        DumpToken retTok,
        DumpPlatform platform,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys)
    {
        var expr = retTok.AstOperand1!;
        // return 토큰의 값 타입은 함수의 선언된 반환형
        var retType = EssentialTypeResolver.FromValueType(retTok, platform, isPlainChar: false);
        var exprType = EssentialTypeResolver.Resolve(expr, platform);
        if (retType == null || exprType == null)
            return;

        CheckImplicitConversion(
            retType.Value, exprType.Value, expr, rulesById, issues, emittedKeys,
            retTok.File, retTok.LineNr, retTok.Column,
            narrowIntMsg: (l, r) => $"함수 반환 시 암묵적 축소 변환: 필수 타입 '{r.Display}' 수식이 더 작은 함수 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            narrowFloatMsg: (l, r) => $"함수 반환 시 암묵적 실수 축소 변환: '{r.Display}' 수식이 더 좁은 함수 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            floatToIntMsg: (l, r) => $"함수 반환 시 부동소수점-정수 암묵적 변환: 실수형 '{r.Display}' 수식이 정수형 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.",
            signMsg: (l, r) => $"함수 반환 시 암묵적 부호 변환: 필수 타입 '{r.Display}' 수식이 부호가 다른 반환형 '{l.Display}'으로 캐스트 없이 반환되었습니다.");
    }

    /// <summary>
    /// 대입/반환/인자 전달 공통 암묵적 변환 검사.
    /// - implicitNarrowing: 비트 폭이 줄어드는 축소 변환(정수 축소, double→float, 실수→정수)으로 인한 데이터 손실
    /// - implicitSignConversion: 부호 있는/없는 정수 간 변환
    /// </summary>
    private static void CheckImplicitConversion(
        EssentialType target,
        EssentialType source,
        DumpToken sourceExpr,
        Dictionary<string, RuleInfo> rulesById,
        List<AnalysisIssue> issues,
        HashSet<string> emittedKeys,
        string file,
        int line,
        int column,
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
        //    음수가 아닌 부호 있는 정수 상수 수식을 부호 없는 타입에 대입하는 것은 허용
        //    (값이 범위를 넘는 경우는 위 1)의 축소 변환으로 이미 보고되므로 부호 변환으로 중복 보고하지 않음)
        if (target.IsSignedOrUnsigned && source.IsSignedOrUnsigned && target.Category != source.Category)
        {
            bool allowedConstant = target.Category == EssentialCategory.Unsigned && IsNonNegativeConstant(sourceExpr);
            if (!allowedConstant)
                AddIssue(issues, emittedKeys, rulesById, "implicitSignConversion", file, line, column, signMsg(target, source));
        }
    }

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
        bool targetIsPtr = castTok.ValuePointer > 0;
        bool srcIsPtr = src.ValuePointer > 0;

        // 1) 포인터 <-> 정수 변환: 포인터를 더 작은 정수형으로 변환하면 주소를 표현하지 못할 수 있다
        if (targetIsPtr != srcIsPtr)
        {
            if (srcIsPtr && !targetIsPtr && targetBits > 0 && targetBits < platform.PointerBit)
            {
                AddIssue(issues, emittedKeys, rulesById, "pointerSizeMismatch", castTok.File, castTok.LineNr, castTok.Column,
                    $"포인터 주소 표현 범위 위험: {platform.PointerBit}-bit 포인터를 더 작은 {targetBits}-bit 정수형으로 변환하고 있습니다. 주소를 대상 정수형으로 표현할 수 없으면 정의되지 않은 동작이 발생할 수 있습니다.");
            }
            return;
        }

        // 2) 포인터 <-> 포인터 변환은 자체 검사 대상이 아니다
        if (targetIsPtr && srcIsPtr)
            return;

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

        // 함수 인자 전달 시 암묵적 축소/부호 변환 (인자 전달은 매개변수로의 대입과 같다)
        if (string.IsNullOrEmpty(funcTok.FunctionId) ||
            !unit.FunctionById.TryGetValue(funcTok.FunctionId, out var func) ||
            openParenTok.AstOperand2 == null)
            return;

        var callArgs = FlattenCallArguments(openParenTok.AstOperand2);
        int count = Math.Min(callArgs.Count, func.Args.Count); // 가변 인자(...) 부분은 검사하지 않음
        for (int i = 0; i < count; i++)
        {
            var argExpr = callArgs[i];
            var param = func.Args[i];
            if (param == null)
                continue;

            int argNo = i + 1;
            var paramType = EssentialTypeResolver.ResolveParameter(param, platform);
            var argType = EssentialTypeResolver.Resolve(argExpr, platform);
            if (paramType == null || argType == null)
                continue;

            CheckImplicitConversion(
                paramType.Value, argType.Value, argExpr, rulesById, issues, emittedKeys,
                argExpr.File, argExpr.LineNr, argExpr.Column,
                narrowIntMsg: (l, r) => $"함수 인자 전달 시 암묵적 축소 변환: '{funcName}()'의 {argNo}번째 인자(필수 타입 '{r.Display}')가 명시적 캐스트 없이 더 작은 매개변수 타입 '{l.Display}'으로 전달되었습니다.",
                narrowFloatMsg: (l, r) => $"함수 인자 전달 시 암묵적 실수 축소 변환: '{funcName}()'의 {argNo}번째 인자('{r.Display}')가 더 좁은 매개변수 타입 '{l.Display}'으로 전달되었습니다.",
                floatToIntMsg: (l, r) => $"함수 인자 전달 시 부동소수점-정수 암묵적 변환: '{funcName}()'의 {argNo}번째 인자(실수형 '{r.Display}')가 정수형 매개변수 '{l.Display}'으로 전달되었습니다 (소수점 이하 절삭).",
                signMsg: (l, r) => $"함수 인자 전달 시 암묵적 부호 변환: '{funcName}()'의 {argNo}번째 인자(필수 타입 '{r.Display}')가 부호가 다른 매개변수 타입 '{l.Display}'으로 명시적 캐스트 없이 전달되었습니다.");
        }
    }

    /// <summary>함수 호출 '(' 토큰의 astOperand2(인자 AST)를 ',' 기준으로 펼쳐 인자 순서대로 반환</summary>
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

    /// <summary>정수 상수 수식(리터럴 및 매크로 상수 확장 결과)의 값이 대상 비트 폭 범위 안에 들어가는지 확인</summary>
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
