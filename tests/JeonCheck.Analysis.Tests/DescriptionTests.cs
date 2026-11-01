using System.Reflection;
using System.Text.Json;
using JeonCheck.Models;
using JeonCheck.Services;

static class DescriptionTests
{
    public static void Run(Action<string, Action> test)
    {
        test("한글 설명 DB의 식별자와 필수 필드", () =>
        {
            var db = JsonSerializer.Deserialize<RuleDatabase>(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "rules_ko.json")))!;
            Require(db.Rules.Count > 0, "규칙이 비어 있음");
            Require(db.Rules.Select(r => r.Id).Distinct().Count() == db.Rules.Count, "규칙 ID 중복");
            foreach (var rule in db.Rules)
            {
                Require(!string.IsNullOrWhiteSpace(rule.Id), "규칙 ID 누락");
                foreach (string text in new[] { rule.Title, rule.Summary, rule.Description })
                    Require(text.Any(c => c is >= '가' and <= '힣'), $"{rule.Id}: 한글 설명 누락");
            }
        });

        test("빈 예시의 미등록 표시와 JSON 원본 보존", () =>
        {
            var rule = new RuleInfo { BadExample = " ", GoodExample = "" };
            Require(rule.BadExampleDisplay.Contains("아직 등록되지"), "위반 예시 미등록 표시 누락");
            Require(rule.GoodExampleDisplay.Contains("아직 등록되지"), "개선 예시 미등록 표시 누락");
            rule.GoodExample = "return 0;";
            Require(rule.GoodExampleDisplay == "return 0;", "등록된 예시 변경");
            Require(!JsonSerializer.Serialize(rule).Contains("GoodExampleDisplay"), "표시용 속성이 저장됨");
        });

        test("MISRA 의무와 프로젝트 정책의 분리", () =>
        {
            var misra = new RuleInfo { Id = "misra-c2012-9.1", Obligation = "강제(Mandatory)" };
            Require(misra.ObligationDisplay == "MISRA 의무: 강제(Mandatory)", "MISRA 의무 표시 오류");
            var custom = new RuleInfo { Id = "nullPointer", Obligation = "치명(Critical)" };
            Require(custom.ObligationDisplay == "프로젝트 정책: 우선 검토", "정책과 심각도 혼동");
            var issue = new AnalysisIssue { RuleId = custom.Id, Obligation = custom.Obligation };
            Require(issue.ObligationDisplay == custom.ObligationDisplay, "목록과 결함 상세 정책 불일치");
            Require(custom.Obligation == "치명(Critical)", "기존 정책/선택 기준 변경");
        });

        test("헤더 누락 노트에서 분석 완전성을 보장하지 않음", () =>
        {
            var result = ParseCppcheck("missingIncludeSystem", "information", "Header unavailable");
            Require(result.Defects.Count == 0 && result.NotesAndInfos.Count == 1, "헤더 누락 분류 오류");
            var note = result.NotesAndInfos[0];
            Require(note.IsInfoOrNote && note.Severity == "Info", "노트 심각도 오류");
            Require(note.Message.Contains("제한될 수") && !note.Message.Contains("정상 분석"), "과도한 분석 보장");
            Require(note.Message.Contains("Header unavailable"), "엔진 원문 유실");
        });

        test("미등록 Cppcheck 진단의 심각도와 한글 안내", () =>
        {
            const string raw = "Unknown operation issue";
            var result = ParseCppcheck("futureCheck", "warning", raw);
            Require(result.Defects.Count == 1, "미등록 진단 누락");
            var issue = result.Defects[0];
            Require(issue.Severity == "Warning" && issue.Obligation == "미분류", "근거 없는 등급 부여");
            Require(issue.Message.Contains("개별 한글 해설") && issue.Message.Contains(raw), "진단 안내 누락");
            Require(issue.Message.Split(raw).Length == 2, "원문 중복 표시");
            Require(issue.RuleDetails!.GoodExample == "", "미검증 개선 예시 제공");
        });

        foreach (string severity in new[] { "warning", "error" })
        foreach (string id in new[] { "clang-analyzer-future.Check", "cert-future-c" })
        {
            test($"미등록 Clang-Tidy 진단: {id}, {severity}", () =>
            {
                var issues = new List<AnalysisIssue>();
                var method = typeof(ClangTidyRunner).GetMethod("ParseClangTidyOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
                const string file = "C:/fixture.c";
                method.Invoke(null, new object[]
                {
                    $"{file}:1:2: {severity}: Future diagnostic [{id}]",
                    new HashSet<string> { AnalysisIssue.NormalizePath(file) },
                    new Dictionary<string, RuleInfo>(), new HashSet<string>(), issues
                });
                Require(issues.Count == 1, "미등록 진단 누락");
                var issue = issues[0];
                Require(issue.Severity == (severity == "error" ? "Error" : "Warning"), "엔진 심각도 미보존");
                Require(issue.Obligation == "미분류", "CERT 접두사만으로 의무 등급 추정");
                Require(issue.Message.Contains("개별 한글 해설") && issue.Message.Contains("Future diagnostic"), "한글 안내/원문 누락");
                Require(issue.RuleDetails!.GoodExampleDisplay.Contains("아직 등록되지"), "미등록 개선 예시 표시 오류");
            });
        }
    }

    internal static AnalysisRunResult ParseCppcheck(string id, string severity, string message, Dictionary<string, RuleInfo>? rules = null)
    {
        const string file = "fixture.c";
        var result = new AnalysisRunResult();
        var keysType = typeof(CppcheckRunner).GetNestedType("DedupKeys", BindingFlags.NonPublic)!;
        var keys = Activator.CreateInstance(keysType, nonPublic: true)!;
        var method = typeof(CppcheckRunner).GetMethod("ParseCppcheckXmlOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
        string xml = $"<?xml version=\"1.0\"?><results><errors><error id=\"{id}\" severity=\"{severity}\" msg=\"{message}\"><location file=\"{file}\" line=\"1\" column=\"2\"/></error></errors></results>";
        Require((bool)method.Invoke(null, new object[] { xml,
            new HashSet<string> { AnalysisIssue.NormalizePath(file) }, rules ?? new Dictionary<string, RuleInfo>(), result, keys })!,
            "XML 파싱 실패");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
