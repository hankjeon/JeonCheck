using JeonCheck.Services.Misra;

// Cppcheck 2.22 misra.py를 옮긴 MisraChecker의 dump 해석·보고 규칙 검증.
// 원본과의 전체 결과 일치는 Cppcheck로 만든 dump 코퍼스에서 차분 검증한다 (README 참고).
static class MisraCheckerTests
{
    // 모든 토큰이 함수 스코프에 속한 최소 dump. Cppcheck 2.22처럼 rawtokens를 dump 구성 뒤에 둔다.
    private static string Dump(string tokens, string extra = "", string raw = "", int configurations = 1, string std = "c99") =>
        $"""
        <?xml version="1.0"?>
        <dumps language="c">
          <platform name="unix64" char_bit="8" short_bit="16" int_bit="32" long_bit="64" long_long_bit="64" pointer_bit="64"/>
        {string.Concat(Enumerable.Repeat($"""
          <dump cfg="">
            <standards><c version="{std}"/></standards>
            {extra}
            <tokenlist>
              <token id="b" file="t.c" linenr="1" column="10" str="&#123;" scope="s1" link="e"/>
              {tokens}
              <token id="e" file="t.c" linenr="9" column="1" str="&#125;" scope="s1" link="b"/>
            </tokenlist>
            <scopes><scope id="s0" type="Global"/><scope id="s1" type="Function" className="f" bodyStart="b" bodyEnd="e" nestedIn="s0"/></scopes>
          </dump>
        """, configurations))}
          <rawtokens>
            <file index="0" name="t.c"/>
            {raw}
          </rawtokens>
        </dumps>
        """;

    private static MisraResult Check(string xml)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, xml);
            return MisraChecker.CheckDump(path);
        }
        finally { File.Delete(path); }
    }

    private static void Expect(MisraResult r, params string[] expected)
    {
        if (r.Error != null) throw new Exception($"예외: {r.Error.Message} {r.Error.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        var actual = r.Violations.Select(v => $"{v.RuleId.Replace("misra-c2012-", "")}@{v.Line}:{v.Column}").OrderBy(x => x).ToArray();
        var want = expected.OrderBy(x => x).ToArray();
        if (!actual.SequenceEqual(want))
            throw new Exception($"기대 [{string.Join(", ", want)}], 실제 [{string.Join(", ", actual)}]");
    }

    public static void Run(Action<string, Action> test)
    {
        test("MISRA: dump 구성 뒤의 rawtokens 해석 (7.1, 7.3, 8.14)", () => Expect(Check(Dump("", raw: """
            <tok fileIndex="0" linenr="2" column="5" str="012"/>
            <tok fileIndex="0" linenr="3" column="5" str="10l"/>
            <tok fileIndex="0" linenr="4" column="5" str="restrict"/>
            """)), "7.1@2:5", "7.3@3:5", "8.14@4:5"));

        test("MISRA: 지시문 규칙 (20.5)", () => Expect(Check(Dump("", extra: """
            <directivelist><directive file="t.c" linenr="5" column="1" str="#undef X"/></directivelist>
            """)), "20.5@5:1"));

        test("MISRA: 구성이 여러 개여도 같은 위치는 한 번만 보고 (15.1)", () => Expect(Check(Dump("""
            <token id="1" file="t.c" linenr="3" column="5" str="goto" scope="s1" type="name"/>
            """, configurations: 2)), "15.1@3:5"));

        test("MISRA: 매개변수 수정 (17.8)", () => Expect(Check(Dump("""
            <token id="1" file="t.c" linenr="3" column="5" str="p" scope="s1" type="name" variable="v1" varId="1"/>
            <token id="2" file="t.c" linenr="3" column="7" str="=" scope="s1" type="op" isAssignmentOp="True" astOperand1="1" astOperand2="3"/>
            <token id="3" file="t.c" linenr="3" column="9" str="0" scope="s1" type="number" isInt="True" astParent="2" valueType-type="int" valueType-sign="signed"/>
            """, extra: """
            <variables><var id="v1" nameToken="1" access="Argument" scope="s1"/></variables>
            """)), "17.8@3:7"));

        test("MISRA: 원본이 중단되는 dump는 그 전까지의 결과와 예외를 함께 반환", () =>
        {
            // 15.5는 return 토큰의 scope.type을 읽는다. 스코프가 없으면 원본처럼 그 지점에서 중단된다.
            var r = Check(Dump("""
                <token id="1" file="t.c" linenr="3" column="5" str="goto" scope="s1" type="name"/>
                <token id="2" file="t.c" linenr="4" column="5" str="return" type="name"/>
                """));
            if (r.Error == null) throw new Exception("예외가 전달되지 않음");
            if (!r.Violations.Any(v => v.RuleId == "misra-c2012-15.1")) throw new Exception("중단 전 결과가 사라짐");
        });

        test("MISRA: 원본 문자열 연결 버그까지 같은 errno 함수 목록 (22.8)", () =>
        {
            if (MisraHelpers.IsErrnoSettingFunction("fputwc") || !MisraHelpers.IsErrnoSettingFunction("fputwcstrtoimax"))
                throw new Exception("원본 is_errno_setting_function 목록과 다름");
            if (!MisraHelpers.IsErrnoSettingFunction("strtol")) throw new Exception("strtol 누락");
        });

        test("MISRA: Python 'in <문자열>'은 부분 문자열 검사", () =>
        {
            if (!Py.In("Struct", "Struct") || !Py.In("", "Struct") || Py.In("Union", "Struct"))
                throw new Exception("부분 문자열 검사 결과가 원본과 다름");
            if (Py.Str(null) + " int" != "None int") throw new Exception("None 서식이 원본과 다름");
        });
    }
}
