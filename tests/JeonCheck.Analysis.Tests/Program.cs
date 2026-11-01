using JeonCheck.Models;
using JeonCheck.Services;

// 실행: dotnet run --project tests/JeonCheck.Analysis.Tests
// 실패하면 종료 코드 1을 반환한다. 최소 AST로 규칙의 양성/음성 사례를 검증한다.
int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"실패: {name}: {ex.Message}"); }
}

foreach (string context in new[] { "대입", "반환", "인자" })
foreach (string castType in new[] { "int", "char" })
{
    Test($"{context}: 캐스트 이후 변환 {castType}", () =>
    {
        var f = new Fixture();
        var destination = f.Value("dst", "char", "unsigned");
        var cast = f.Token("(");
        cast.IsCast = true; cast.ValueType = castType; cast.ValueSign = "unsigned";
        var typeToken = f.Token(castType); typeToken.IsUnsigned = true;
        cast.AstOperand1 = f.Value("src", "int", "unsigned");
        if (context == "대입") f.Binary("=", destination, cast).IsAssignmentOp = true;
        else if (context == "반환")
        {
            var ret = f.Token("return"); ret.ValueType = "char"; ret.ValueSign = "unsigned";
            ret.AstOperand1 = cast;
        }
        else
        {
            var name = f.Token("consume"); name.FunctionId = "consume";
            var call = f.Token("("); call.AstOperand1 = name; call.AstOperand2 = cast;
            f.Unit.FunctionById["consume"] = new() { Args = new() { new() { NameToken = destination } } };
        }
        f.Expect("10.3", castType == "int" ? 1 : 0);
        f.Expect("implicitNarrowing", castType == "int" ? 1 : 0);
    });
}

foreach (string op in new[] { "<", "+", "&", "|", "^", "+=", "-=", ":" })
foreach (string sign in new[] { "signed", "unsigned" })
    Test($"10.4: {op}, {sign} 상수", () =>
    {
        var f = new Fixture();
        var expr = f.Binary(op, f.Value("u", "int", "unsigned"), f.Integer("1", sign));
        expr.IsComparisonOp = op == "<";
        f.Expect("10.4", sign == "signed" ? 1 : 0);
    });

foreach (var (target, source, targetDepth, sourceDepth, expected) in new[]
{
    ("int", "void", 1, 1, 1), ("void", "int", 1, 1, 0),
    ("int", "int", 1, 1, 0), ("int", "void", 2, 1, 0)
})
    Test($"11.5: {source}* → {target}* ({targetDepth}/{sourceDepth})", () =>
    {
        var f = new Fixture();
        var lhs = f.Value("lhs", target); lhs.ValuePointer = targetDepth;
        var rhs = f.Value("rhs", source); rhs.ValuePointer = sourceDepth;
        f.Binary("=", lhs, rhs).IsAssignmentOp = true;
        f.Expect("11.5", expected);
    });

foreach (var (body, expected) in new[]
{
    ("case 1 : return 1 ; default : break ;", 0),
    ("case 1 : return func ( ) ; default : break ;", 0),
    ("case 1 : { work ( ) ; break ; } default : break ;", 0),
    ("case 1 : { { break ; } } default : break ;", 0),
    ("case 1 : case 2 : break ; default : break ;", 0),
    ("case 1 : work ( ) ; default : break ;", 1),
    ("case 1 : if ( flag ) { break ; } default : break ;", 1),
    ("case 1 : while ( flag ) { break ; } default : break ;", 1),
    ("case 1 : switch ( x ) { default : break ; } default : break ;", 1),
    ("case 1 : break ; default : work ( ) ;", 1),
    ("case 1 : break ; work ( ) ; default : break ;", 1),
    ("case 1 : if ( flag ) { break ; } break ; default : break ;", 0)
})
    Test($"16.3: {body}", () =>
    {
        var f = new Fixture(); var start = f.Token("{");
        foreach (var spelling in body.Split(' ')) f.Token(spelling);
        var end = f.Token("}");
        f.Unit.ScopeById["switch"] = new() { Type = "Switch", BodyStartId = start.Id, BodyEndId = end.Id };
        f.Expect("16.3", expected);
        f.Expect("16.4", 0);
    });

foreach (string op in new[] { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "++", "--" })
foreach (string access in new[] { "Argument", "Local" })
    Test($"17.8: {access} {op}", () =>
    {
        var f = new Fixture(); var value = f.Value("p"); value.Variable!.Access = access;
        var token = f.Binary(op, value, f.Integer("2"));
        token.IsAssignmentOp = op is not ("++" or "--");
        f.Expect("17.8", access == "Argument" ? 1 : 0);
    });

foreach (var (leftSign, rightKind, expected) in new[]
{
    ("unsigned", "signed-variable", 1), ("unsigned", "unsigned-variable", 0),
    ("unsigned", "signed-literal", 0), ("signed", "signed-literal", 1)
})
    Test($"10.1: {leftSign} << {rightKind}", () =>
    {
        var f = new Fixture(); var left = f.Value("left", "int", leftSign);
        var right = rightKind.EndsWith("literal") ? f.Integer("1") :
            f.Value("count", "int", rightKind.StartsWith("unsigned") ? "unsigned" : "signed");
        f.Binary("<<", left, right);
        f.Expect("10.1", expected);
    });

foreach (bool parenthesized in new[] { true, false })
foreach (bool comparisonOnLeft in new[] { true, false })
    Test($"12.1: 괄호={parenthesized}, 비교식 왼쪽={comparisonOnLeft}", () =>
    {
        var f = new Fixture(); DumpToken bitwise; DumpToken mask;
        if (!comparisonOnLeft) { mask = f.Token("mask"); bitwise = f.Token("&"); }
        else { bitwise = new(); mask = new(); }
        if (parenthesized) f.Token("(");
        var a = f.Token("a"); var comparison = f.Token("=="); var b = f.Token("b");
        comparison.IsComparisonOp = true; comparison.AstOperand1 = a; comparison.AstOperand2 = b;
        if (parenthesized) f.Token(")");
        if (comparisonOnLeft) { bitwise = f.Token("&"); mask = f.Token("mask"); }
        bitwise.AstOperand1 = comparisonOnLeft ? comparison : mask;
        bitwise.AstOperand2 = comparisonOnLeft ? mask : comparison;
        f.Expect("12.1", parenthesized ? 0 : 1);
    });

foreach (string name in new[] { "printf", "snprintf", "fread", "fwrite", "fseek", "fwprintf", "fgetwc", "swprintf_s", "malloc" })
    Test($"21.6: {name}", () =>
    {
        var f = new Fixture(); f.Call("main", name);
        f.Expect("21.6", name == "malloc" ? 0 : 1);
    });

Test("21.6: 이름이 같은 사용자 정의 함수", () =>
{
    var f = new Fixture(); f.Call("main", "remove");
    f.Unit.FunctionById["remove"] = new() { Token = f.Token("remove") };
    f.Expect("21.6", 0);
});

Test("21.6: 이름이 같은 함수 포인터", () =>
{
    var f = new Fixture(); var call = f.Call("main", "printf");
    call.Variable = new() { IsPointer = true }; call.FunctionId = "";
    f.Expect("21.6", 0);
});

Test("17.8: Cppcheck가 분리한 매개변수 초기화는 제외", () =>
{
    var f = new Fixture(); var arg = f.Value("arg"); arg.Variable!.Access = "Argument";
    var eq = f.Binary("=", arg, f.Integer("1"));
    eq.IsAssignmentOp = true; eq.IsSplittedVarDeclEq = true;
    f.Expect("17.8", 0);
});

foreach (var (edges, expected) in new[]
{
    ("a:a", 1), ("a:b,b:a", 2), ("a:b,b:c,c:a", 3),
    ("a:b,b:c", 0), ("a:b,b:c,c:b", 2), ("a:b,a:c,c:b", 0)
})
    Test($"17.2: {edges}", () =>
    {
        var f = new Fixture();
        foreach (var edge in edges.Split(',')) { var pair = edge.Split(':'); f.Call(pair[0], pair[1]); }
        f.Expect("17.2", expected);
    });

Test("17.2: 참조 파일을 경유하는 재귀와 보고 위치 필터", () =>
{
    var f = new Fixture(); f.Call("a", "b");
    var referenceCall = f.Call("b", "a"); referenceCall.File = Path.GetFullPath("reference.c");
    f.Expect("17.2", 1);
});

Test("17.2: 중첩 스코프의 호출", () =>
{
    var f = new Fixture(); var call = f.Call("a", "a");
    f.Unit.ScopeById["if"] = new() { Type = "If", NestedInId = "scope-a" };
    call.ScopeId = "if"; f.Expect("17.2", 1);
});

Test("규칙 비활성화 및 설정 간 중복 제거", () =>
{
    var f = new Fixture(); f.Call("a", "a");
    f.Document.Configurations.Add(f.Unit);
    f.Expect("17.2", 1);
    f.Expect("17.2", 0, enabled: false);
});

Test("dump XML 파싱 후 캐스트 대입 검사", () =>
{
    string path = Path.GetTempFileName();
    try
    {
        // Cppcheck XML 필드와 참조 ID를 사용해 파서부터 분석기까지 연결한다.
        File.WriteAllText(path, """
            <dumps><platform char_bit="8" int_bit="32"/><dump cfg="">
            <tokenlist>
            <token id="1" file="fixture.c" linenr="1" column="1" str="dst" type="name" variable="v1" valueType-type="char" valueType-sign="unsigned"/>
            <token id="2" file="fixture.c" linenr="1" column="5" str="=" isAssignmentOp="true" astOperand1="1" astOperand2="3"/>
            <token id="3" file="fixture.c" linenr="1" column="7" str="(" isCast="true" astOperand1="6" valueType-type="int" valueType-sign="unsigned"/>
            <token id="4" file="fixture.c" linenr="1" column="8" str="int" type="name" isUnsigned="true"/>
            <token id="5" file="fixture.c" linenr="1" column="11" str=")"/>
            <token id="6" file="fixture.c" linenr="1" column="12" str="src" type="name" variable="v2" valueType-type="int" valueType-sign="unsigned"/>
            </tokenlist><variables><var id="v1" nameToken="1" access="Local"/><var id="v2" nameToken="6" access="Local"/></variables>
            </dump></dumps>
            """);
        var doc = new DumpFileParser().Parse(path);
        var issues = new MisraAndTypeAnalyzer().Analyze(doc, new() { AnalysisIssue.NormalizePath("fixture.c") },
            new() { ["misra-c2012-10.3"] = new() });
        if (issues.Count != 1 || issues[0].Column != 5) throw new Exception("캐스트 대입 위치를 보고하지 않음");
    }
    finally { File.Delete(path); }
});

DescriptionTests.Run(Test);
RuleSummaryTests.Run(Test);
UnknownDiagnosticTests.Run(Test);

Console.WriteLine($"회귀 테스트: 성공 {passed}, 실패 {failed}");
return failed == 0 ? 0 : 1;

sealed class Fixture
{
    public DumpCfgUnit Unit { get; } = new();
    public DumpDocument Document { get; }
    private readonly string file = Path.GetFullPath("fixture.c");
    public Fixture() => Document = new() { Configurations = new() { Unit } };
    public DumpToken Token(string spelling)
    {
        var token = new DumpToken { Id = (Unit.Tokens.Count + 1).ToString(), Str = spelling,
            Type = "name", File = file, LineNr = 1, Column = Unit.Tokens.Count + 1 };
        if (Unit.Tokens.LastOrDefault() is { } previous) { previous.Next = token; token.Previous = previous; }
        Unit.Tokens.Add(token); Unit.TokenById[token.Id] = token;
        return token;
    }
    public DumpToken Value(string name, string type = "int", string sign = "signed")
    {
        var token = Token(name); token.ValueType = type; token.ValueSign = sign;
        token.Variable = new() { Access = "Local", NameToken = token }; return token;
    }
    public DumpToken Integer(string value, string sign = "signed")
    {
        var token = Token(value); token.ValueType = "int"; token.ValueSign = sign;
        token.IsIntLiteral = true; return token;
    }
    public DumpToken Binary(string op, DumpToken left, DumpToken right)
    {
        var token = Token(op); token.AstOperand1 = left; token.AstOperand2 = right; return token;
    }
    public DumpToken Call(string caller, string callee)
    {
        string scope = "scope-" + caller;
        Unit.ScopeById[scope] = new() { Id = scope, Type = "Function", FunctionId = caller };
        var name = Token(callee); name.ScopeId = scope; name.FunctionId = callee;
        var call = Token("("); call.AstOperand1 = name; Token(")"); return name;
    }
    public void Expect(string rule, int count, bool enabled = true)
    {
        string id = rule.Contains('.') ? "misra-c2012-" + rule : rule;
        var issues = new MisraAndTypeAnalyzer().Analyze(Document, new() { AnalysisIssue.NormalizePath(file) },
            new() { [id] = new() { Id = id, IsEnabled = enabled } });
        if (issues.Count != count) throw new Exception($"{id}: 기대 {count}, 실제 {issues.Count}");
    }
}
