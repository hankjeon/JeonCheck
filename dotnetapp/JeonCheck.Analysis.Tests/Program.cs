using JeonCheck.Models;
using JeonCheck.Services;

// 실행: dotnet run --project tests/JeonCheck.Analysis.Tests
// 실패하면 종료 코드 1을 반환한다. 최소 AST로 자체 타입 검사의 양성/음성 사례를 검증한다.
// MISRA 검사기(원본 misra.py 이식) 검증은 MisraCheckerTests.cs에 있다.
// 차분 검증 모드: MisraParity/compare_misra.py가 호출한다. 폴더의 모든 .dump를 MisraChecker로 검사해 JSON으로 저장한다.
if (args.Length == 3 && args[0] == "--misra-json")
{
    var output = new Dictionary<string, List<object?[]>>();
    foreach (var dump in Directory.GetFiles(args[1], "*.dump", SearchOption.AllDirectories))
    {
        var r = JeonCheck.Services.Misra.MisraChecker.CheckDump(dump);
        output[Path.GetRelativePath(args[1], dump)] = r.Violations.Select(v => new object?[] { v.RuleId, v.File, v.Line, v.Column }).ToList();
    }
    File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(output));
    return 0;
}

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
        f.Expect("implicitNarrowing", castType == "int" ? 1 : 0);
    });
}

foreach (var (target, value, expected) in new[] { ("unsigned", "1", 0), ("unsigned", "-1", 1), ("signed", "1", 0) })
    Test($"부호 변환: {target} ← 상수 {value}", () =>
    {
        // 음수가 아닌 정수 상수를 부호 없는 변수에 대입하는 것은 허용한다.
        var f = new Fixture();
        var lhs = f.Value("u", "int", target);
        var rhs = f.Integer(value);
        if (value.StartsWith('-')) { rhs.IsIntLiteral = false; rhs.Str = "-"; rhs.AstOperand1 = f.Integer("1"); }
        f.Binary("=", lhs, rhs).IsAssignmentOp = true;
        f.Expect("implicitSignConversion", expected);
    });

Test("sizeof 값은 Cppcheck 확정 값으로 상수 판정", () =>
{
    // c = sizeof(int): 값이 대상 타입에 들어가면 축소 변환이 아니다
    var f = new Fixture();
    var lhs = f.Value("c", "char");
    var paren = f.Token("("); paren.ValueType = "long"; paren.ValueSign = "unsigned"; paren.KnownIntValue = 4;
    paren.AstOperand1 = f.Token("sizeof"); paren.AstOperand2 = f.Token("int");
    f.Binary("=", lhs, paren).IsAssignmentOp = true;
    f.Expect("implicitNarrowing", 0);
    paren.KnownIntValue = 300;
    f.Expect("implicitNarrowing", 1);
    paren.KnownIntValue = null; // 값을 모르면 상수로 보지 않는다
    f.Expect("implicitNarrowing", 1);
});

Test("규칙 비활성화 시 자체 검사 미보고", () =>
{
    var f = new Fixture();
    f.Binary("=", f.Value("c", "char"), f.Value("i", "int")).IsAssignmentOp = true;
    f.Expect("implicitNarrowing", 1);
    f.Expect("implicitNarrowing", 0, enabled: false);
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
        var issues = new TypeConversionAnalyzer().Analyze(doc, new() { AnalysisIssue.NormalizePath("fixture.c") },
            new() { ["implicitNarrowing"] = new() });
        if (issues.Count != 1 || issues[0].Column != 5) throw new Exception("캐스트 대입 위치를 보고하지 않음");
    }
    finally { File.Delete(path); }
});

DescriptionTests.Run(Test);
SourceFileEncodingTests.Run(Test);
CodeHighlightTests.Run(Test);
AnalysisSnapshotTests.Run(Test);
RuleSummaryTests.Run(Test);
UnknownDiagnosticTests.Run(Test);
MisraCheckerTests.Run(Test);

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
    public void Expect(string rule, int count, bool enabled = true)
    {
        string id = rule;
        var issues = new TypeConversionAnalyzer().Analyze(Document, new() { AnalysisIssue.NormalizePath(file) },
            new() { [id] = new() { Id = id, IsEnabled = enabled } });
        if (issues.Count != count) throw new Exception($"{id}: 기대 {count}, 실제 {issues.Count}");
    }
}
