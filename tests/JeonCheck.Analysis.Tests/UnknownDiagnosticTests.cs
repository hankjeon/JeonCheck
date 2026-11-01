using System.Reflection;
using System.Text.Json;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;

static class UnknownDiagnosticTests
{
    public static void Run(Action<string, Action> test)
    {
        test("미등록 Cppcheck: XML부터 결함 목록·요약·상세 연결", () =>
        {
            var issue = DescriptionTests.ParseCppcheck("futureCppCheck", "error", "Future raw diagnostic").Defects.Single();
            var vm = new MainViewModel(); vm.ClearWorkspace();
            SetResults(vm, issue);
            Require(vm.FilteredDefects.Single() == issue, "미등록 항목이 UI 필터에서 유실됨");
            Require(vm.RuleIssueGroups.Single().Occurrences.Single().Issue == issue, "요약에서 진단 유실");
            vm.SelectedIssue = vm.RuleIssueGroups[0].Occurrences[0].Issue;
            Require(vm.SelectedIssue.Message.Contains("Future raw diagnostic"), "선택한 진단의 원문 누락");
            Require(vm.SelectedIssue.RuleDetails!.Description.Contains("아직 등록되지"), "미등록 한글 안내 누락");
            Require(vm.AllRules.Single(r => r.Id == issue.RuleId).IsEnabled, "미등록 규칙 기본 OFF");
        });

        test("미등록 진단 재분석: 원문·심각도 갱신, 첫 진단 문맥 재사용 방지", () =>
        {
            var first = ParseClang("clang-analyzer-future.Check", "warning", "old_variable diagnostic").Single();
            var vm = new MainViewModel(); vm.ClearWorkspace(); SetResults(vm, first);
            var rules = vm.AllRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            var next = ParseClang(first.RuleId, "error", "new_variable diagnostic", rules).Single();
            SetResults(vm, next);
            Require(vm.FilteredDefects.Single().Severity == "Error", "이전 warning 심각도를 재사용함");
            Require(next.Message.Contains("new_variable") && !next.Message.Contains("old_variable"), "이전 원문 재사용");
            Require(!next.RuleDetails!.Description.Contains("old_variable"), "공통 해설에 첫 원문이 고정됨");
            Require(vm.AllRules.Count(r => r.Id == next.RuleId) == 1, "재분석 때 규칙 중복 생성");
        });

        test("미등록 Cppcheck 재분석 시 엔진의 현재 심각도 유지", () =>
        {
            var issue = DescriptionTests.ParseCppcheck("futureCppCheck", "warning", "old message").Defects.Single();
            var vm = new MainViewModel(); vm.ClearWorkspace(); SetResults(vm, issue);
            var next = DescriptionTests.ParseCppcheck(issue.RuleId, "error", "new message",
                vm.AllRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase)).Defects.Single();
            Require(next.Severity == "Error" && next.Message.Contains("new message"), "현재 엔진 진단 미보존");
        });

        test("미등록 진단의 검색·분류·예외 처리와 복원", () =>
        {
            var issue = DescriptionTests.ParseCppcheck("futureCppCheck", "warning", "needle operation").Defects.Single();
            var vm = new MainViewModel(); vm.ClearWorkspace(); SetResults(vm, issue);
            Require(vm.DefectCategoryFilters.Contains(issue.Category), "발견된 분류가 필터 목록에 없음");
            vm.SelectedDefectCategoryFilter = issue.Category; vm.DefectSearchText = "needle";
            Require(vm.FilteredDefects.Count == 1, "미등록 진단의 검색/분류 불일치");
            vm.SuppressSingleIssue(issue);
            Require(vm.RuleIssueGroups.Count == 0 && vm.FilteredDefects.Count == 0, "예외가 미등록 진단에 적용되지 않음");
            vm.RestoreAllSuppressedIssues();
            Require(vm.RuleIssueGroups.Single().Count == 1, "예외 복원 후 미등록 진단 유실");
        });

        foreach (bool enabled in new[] { false, true })
        {
            test($"미등록 규칙 ON/OFF 프로젝트 저장·재로드·재분석: {enabled}", () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "jeoncheck-unknown-" + Guid.NewGuid().ToString("N") + ".jcproj");
                try
                {
                    var issue = DescriptionTests.ParseCppcheck("futureCppCheck", "warning", "original").Defects.Single();
                    var vm = new MainViewModel(); vm.ClearWorkspace(); SetResults(vm, issue);
                    if (!enabled) vm.DisableRuleEntirely(issue.RuleId);
                    vm.SaveProjectToFile(path);
                    var model = JsonSerializer.Deserialize<ProjectFileModel>(File.ReadAllText(path))!;
                    Require((enabled ? model.EnabledRuleIds : model.DisabledRuleIds!).Contains(issue.RuleId), "미등록 선택 저장 누락");

                    var restored = new MainViewModel(); restored.LoadProjectFromFile(path);
                    var rule = restored.AllRules.Single(r => r.Id == issue.RuleId);
                    Require(rule.IsEnabled == enabled && rule.IsUnregistered, "미등록 ON/OFF 복원 누락");
                    var results = DescriptionTests.ParseCppcheck(issue.RuleId, "error", "after load",
                        restored.AllRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase));
                    Require(results.Defects.Count == (enabled ? 1 : 0), "재분석 때 미등록 선택 미적용");
                    SetResults(restored, results.Defects.ToArray());
                    Require(restored.FilteredDefects.Count == (enabled ? 1 : 0), "프로젝트 로드 후 UI 필터 불일치");
                    if (enabled) Require(restored.FilteredDefects[0].Severity == "Error", "복원된 기본 심각도로 엔진 오류를 덮음");
                }
                finally { File.Delete(path); }
            });
        }

        test("등록된 OFF 규칙과 미등록 ON 규칙 구분", () =>
        {
            var unknown = DescriptionTests.ParseCppcheck("futureCppCheck", "warning", "raw").Defects.Single();
            var vm = new MainViewModel(); vm.ClearWorkspace();
            var registered = vm.AllRules.Single(r => r.Id == "nullPointer"); registered.IsEnabled = false;
            var known = new AnalysisIssue { RuleId = registered.Id, RuleDetails = registered, FilePath = unknown.FilePath, Line = 2 };
            SetResults(vm, known, unknown);
            Require(vm.FilteredDefects.Single() == unknown && !registered.IsEnabled, "기존 OFF 선택이 해제됨");
            vm.AllRules.Single(r => r.Id == unknown.RuleId).IsEnabled = false; vm.RefreshFilteredDefects();
            Require(vm.FilteredDefects.Count == 0, "발견된 규칙의 OFF 선택 무시");
        });

        test("미등록 Clang 별칭 때문에 기존 OFF 선택을 우회하지 않음", () =>
        {
            var rules = new Dictionary<string, RuleInfo> { ["cert-err33-c"] = new() { Id = "cert-err33-c", IsEnabled = false } };
            Require(ParseClang("future-check,cert-err33-c", "warning", "raw", rules).Count == 0, "등록 별칭 OFF 우회");
        });

        test("통합 축소 변환 ID가 DB에 없어도 진단 표시", () =>
        {
            var issue = ParseClang("bugprone-narrowing-conversions", "warning", "precision loss").Single();
            Require(issue.RuleId == "implicitNarrowing" && issue.RuleDetails!.IsUnregistered, "통합 ID의 미등록 경로 누락");
            var vm = new MainViewModel(); vm.ClearWorkspace(); vm.AllRules.Clear(); SetResults(vm, issue);
            Require(vm.FilteredDefects.Single().Message.Contains("precision loss"), "통합 진단 원문 유실");
            var rule = vm.AllRules.Single(); rule.IsEnabled = false;
            Require(ParseClang("bugprone-narrowing-conversions", "warning", "raw", new() { [rule.Id] = rule }).Count == 0,
                "통합 규칙 OFF 무시");
        });

        test("추가 번역 규칙을 실제 파서와 UI에서 한글 설명으로 표시", () =>
        {
            var vm = new MainViewModel(); vm.ClearWorkspace();
            var rules = vm.AllRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            foreach (string id in new[] { "wrongfeofUsage", "uninitStructMember", "funcArgOrderDifferent" })
            {
                var issue = DescriptionTests.ParseCppcheck(id, "warning", "Original engine text", rules).Defects.Single(d => d.RuleId == id);
                SetResults(vm, issue);
                Require(!issue.RuleDetails!.IsUnregistered && !issue.RuleDetails.Description.Contains("아직 등록되지"), "등록한 한글 설명 미사용");
                Require(issue.Message.Contains("Original engine text") && vm.RuleIssueGroups[0].Title == rules[id].Title, "원문/한글 제목 유실");
            }
        });

        test("미등록 표시용 상태는 규칙 JSON에 저장하지 않음", () =>
            Require(!JsonSerializer.Serialize(RuleInfo.CreateUnregistered("future")).Contains("IsUnregistered"), "런타임 상태가 DB 형식에 포함됨"));
    }

    private static void SetResults(MainViewModel vm, params AnalysisIssue[] issues)
    {
        typeof(MainViewModel).GetField("_allRawDefects", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, issues.ToList());
        vm.RefreshFilteredDefects();
    }

    private static List<AnalysisIssue> ParseClang(string ids, string severity, string message, Dictionary<string, RuleInfo>? rules = null)
    {
        var issues = new List<AnalysisIssue>();
        const string file = "C:/fixture.c";
        typeof(ClangTidyRunner).GetMethod("ParseClangTidyOutput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            new object[] { $"{file}:1:2: {severity}: {message} [{ids}]", new HashSet<string> { AnalysisIssue.NormalizePath(file) },
                rules ?? new Dictionary<string, RuleInfo>(), new HashSet<string>(), issues });
        return issues;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
