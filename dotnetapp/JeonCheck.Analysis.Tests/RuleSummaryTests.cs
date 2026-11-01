using System.Reflection;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;

static class RuleSummaryTests
{
    public static void Run(Action<string, Action> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "jeoncheck-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "source.c");
            File.WriteAllText(file, "  int a;\n  int b;\n\n");

            AnalysisIssue Issue(string id, int line = 1, int column = 1, string category = "메모리 및 포인터") => new()
            {
                RuleId = id, Title = "한글 설명 " + id, FilePath = file,
                Line = line, Column = column, Category = category, Message = id + " 진단"
            };

            test("규칙별 진단 건수와 정렬: 같은 줄의 서로 다른 열도 별도 집계", () =>
            {
                var sameLine = new[] { Issue("b", column: 8), Issue("a"), Issue("b", column: 2), Issue("c") };
                var groups = RuleSummaryBuilder.Build(sameLine);
                Require(groups.Select(g => g.RuleId).SequenceEqual(new[] { "b", "a", "c" }), "건수/규칙 ID 정렬 오류");
                Require(groups[0].Count == 2 && groups.Sum(g => g.Count) == 4, "진단 개수를 줄 수로 집계함");
                Require(groups[0].Header.Contains("한글 설명 b") && groups[0].Header.Contains("2건"), "요약 제목 누락");
                Require(groups[0].Occurrences.Select(o => o.Issue.Column).SequenceEqual(new[] { 2, 8 }), "열 위치 정렬 오류");
            });

            test("규칙 ID 대소문자 통합과 여러 파일의 위치 보존", () =>
            {
                var a = Issue("nullPointer", line: 2);
                var b = Issue("NULLPOINTER"); b.FilePath = Path.Combine(folder, "other", "source.c");
                var groups = RuleSummaryBuilder.Build(new[] { a, b });
                Require(groups.Count == 1 && groups[0].Count == 2, "동일 규칙을 두 그룹으로 표시함");
                Require(groups[0].Occurrences.Select(o => o.Location).Distinct().Count() == 2, "같은 파일명 위치를 구분하지 못함");
                var occurrence = groups[0].Occurrences.Single(o => ReferenceEquals(o.Issue, a));
                Require(occurrence.CodeText == "int b;" && occurrence.Location.EndsWith(":2:1"), "코드/위치 불일치");
            });

            test("누락된 파일과 유효하지 않은 줄 정보", () =>
            {
                var missing = Issue("missing"); missing.FilePath += ".removed";
                var groups = RuleSummaryBuilder.Build(new[] { missing, Issue("zero", line: 0), Issue("past", line: 999), Issue("blank", line: 3) });
                Require(groups.Single(g => g.RuleId == "missing").Occurrences[0].CodeText.Contains("읽을 수 없습니다"), "파일 누락 안내 없음");
                Require(groups.Single(g => g.RuleId == "zero").Occurrences[0].CodeText.Contains("줄 정보"), "줄 정보 누락 안내 없음");
                Require(groups.Single(g => g.RuleId == "past").Occurrences[0].CodeText.Contains("현재 파일에 없습니다"), "삭제된 줄 안내 없음");
                Require(groups.Single(g => g.RuleId == "blank").Occurrences[0].CodeText == "(빈 줄)", "빈 줄 표시 오류");
            });

            test("코드 미리보기 갱신 시 파일 변경 반영", () =>
            {
                string changed = Path.Combine(folder, "changed.c"); File.WriteAllText(changed, "old_value;");
                var issue = Issue("a"); issue.FilePath = changed;
                Require(RuleSummaryBuilder.Build(new[] { issue })[0].Occurrences[0].CodeText == "old_value;", "최초 코드 읽기 실패");
                File.WriteAllText(changed, "new_value;");
                Require(RuleSummaryBuilder.Build(new[] { issue })[0].Occurrences[0].CodeText == "new_value;", "오래된 코드 표시");
            });

            test("요약과 결함 목록에 동일한 검색·분류 조건 적용", () =>
            {
                var vm = ViewModel(Issue("a"), Issue("a", line: 2), Issue("b", category: "산술 및 연산"));
                Require(vm.RuleIssueGroups.Count == 2 && vm.RuleIssueGroups[0].Count == 2, "초기 요약 오류");
                vm.SelectedDefectCategoryFilter = "산술 및 연산";
                Require(vm.FilteredDefects.Count == 1 && vm.RuleIssueGroups.Single().RuleId == "b", "분류 필터 불일치");
                vm.SelectedDefectCategoryFilter = "전체"; vm.DefectSearchText = "a 진단";
                Require(vm.FilteredDefects.Count == 2 && vm.RuleIssueGroups.Single().Count == 2, "검색 필터 불일치");
                vm.DefectSearchText = "없는 검색어";
                Require(vm.RuleIssueGroups.Count == 0 && vm.RuleSummaryStatusText.Contains("0건"), "빈 검색 결과 미반영");
            });

            test("심각도 표시 필터와 코드 줄 상태", () =>
            {
                var error = Issue("error", line: 1); error.Severity = "Error";
                var advisory = Issue("advisory", line: 2); advisory.Obligation = "권고(Advisory)";
                var sameLineAdvisory = Issue("sameLineAdvisory", line: 1); sameLineAdvisory.Severity = "Style";
                var warning = Issue("warning", line: 3);
                var vm = ViewModel(error, advisory, sameLineAdvisory, warning);
                vm.AddTargetPath(file);
                vm.RefreshFilteredDefects();
                vm.LoadFileIntoCodeViewer(file);
                Require(advisory.ReviewSeverityDisplay == "권고" && warning.ReviewSeverityDisplay == "경고", "권고/경고 표시 기준 불일치");
                Require(vm.FilteredDefects.Count == 4 && vm.RuleIssueGroups.Count == 4, "기본 표시 범위 오류");
                Require(vm.TargetRoots.Single().DefectCount == 4, "기본 파일 트리 건수 오류");

                vm.SelectedIssue = advisory;
                vm.ShowOnlyErrors();
                Require(vm.ShowErrors && !vm.ShowWarnings && !vm.ShowAdvisories, "오류만 버튼 상태 오류");
                Require(vm.FilteredDefects.SequenceEqual(new[] { error }) && vm.RuleIssueGroups.Single().RuleId == "error", "오류만 필터 오류");
                Require(vm.SelectedIssue == null, "숨긴 결함이 상세 선택에 남음");
                Require(vm.CurrentFileLines[0].Status == CodeLineStatus.Error && vm.CurrentFileLines[1].Status == CodeLineStatus.Neutral, "코드 줄 표시 필터 오류");
                Require(vm.CurrentFileLines[0].Issues.SequenceEqual(new[] { error }), "같은 줄의 숨긴 권고가 코드에 남음");
                Require(vm.TargetRoots.Single().DefectCount == 1, "파일 트리 건수 필터 오류");

                vm.ShowWarnings = true;
                Require(vm.FilteredDefects.SequenceEqual(new[] { error, warning }), "경고 개별 토글 오류");
                vm.ShowAllSeverities();
                Require(vm.FilteredDefects.Count == 4 && vm.CurrentFileLines[1].Status == CodeLineStatus.Advisory, "모두 표시 복원 오류");
                vm.ShowErrors = false; vm.ShowWarnings = false; vm.ShowAdvisories = false;
                Require(vm.FilteredDefects.Count == 0 && vm.RuleIssueGroups.Count == 0, "전체 숨김 오류");
            });

            test("예외 처리·복원 시 요약 건수와 항목 갱신", () =>
            {
                var first = Issue("a", column: 2);
                var vm = ViewModel(first, Issue("a", column: 8), Issue("a", line: 2), Issue("b"));
                vm.SuppressSingleIssue(first);
                // 기존 예외 정책은 동일 파일·줄·규칙의 모든 열을 제외한다.
                Require(vm.RuleIssueGroups.Single(g => g.RuleId == "a").Count == 1, "예외 항목이 요약에 남음");
                Require(vm.RuleIssueGroups.Sum(g => g.Count) == vm.FilteredDefects.Count, "결함 목록과 요약 건수 불일치");
                vm.RestoreAllSuppressedIssues();
                Require(vm.RuleIssueGroups.Single(g => g.RuleId == "a").Count == 3, "복원 건수 미반영");
            });

            test("검사 OFF 규칙 제외와 그룹 사라짐", () =>
            {
                var vm = ViewModel(Issue("a"), Issue("b"));
                vm.AllRules.Single(r => r.Id == "a").IsEnabled = false; vm.RefreshFilteredDefects();
                Require(vm.RuleIssueGroups.Count == 1 && vm.RuleIssueGroups[0].RuleId == "b", "OFF 규칙이 요약에 남음");
            });

            test("규칙별 요약 기본 접힘과 검색 갱신 시 펼침 상태 유지", () =>
            {
                var vm = ViewModel(Issue("a"), Issue("b"));
                Require(vm.RuleIssueGroups.All(g => !g.IsExpanded), "새 그룹이 기본으로 펼쳐짐");
                vm.RuleIssueGroups[0].IsExpanded = true;
                string rule = vm.RuleIssueGroups[0].RuleId; vm.RefreshFilteredDefects();
                Require(vm.RuleIssueGroups.Single(g => g.RuleId == rule).IsExpanded, "펼침 상태 초기화");
                Require(!vm.RuleIssueGroups.Single(g => g.RuleId != rule).IsExpanded, "다른 그룹의 접힘 상태 변경");
            });

            test("같은 줄의 두 번째 진단을 선택해도 상세 대상 보존", () =>
            {
                var second = Issue("b", column: 8); var vm = ViewModel(Issue("a"), second);
                var occurrence = vm.RuleIssueGroups.Single(g => g.RuleId == "b").Occurrences[0];
                vm.SelectedIssue = occurrence.Issue; vm.LoadFileIntoCodeViewer(occurrence.Issue.FilePath);
                Require(ReferenceEquals(vm.SelectedIssue, second), "다른 진단으로 선택이 바뀜");
                Require(vm.CurrentFileLines[0].Issues.Contains(second), "선택한 진단이 코드 줄에서 유실됨");
            });

            test("새 프로젝트에서 요약 초기화 및 건수 변경 알림", () =>
            {
                var vm = ViewModel(Issue("a")); var notified = new HashSet<string>();
                vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
                vm.ClearWorkspace();
                Require(vm.RuleIssueGroups.Count == 0 && vm.FilteredDefects.Count == 0, "이전 프로젝트 결과가 남음");
                Require(notified.Contains(nameof(vm.RuleSummaryTabHeader)) && notified.Contains(nameof(vm.RuleSummaryStatusText)), "요약 헤더 알림 누락");
            });
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static MainViewModel ViewModel(params AnalysisIssue[] issues)
    {
        var vm = new MainViewModel(); vm.ClearWorkspace(); vm.AllRules.Clear();
        foreach (string id in issues.Select(i => i.RuleId).Distinct()) vm.AllRules.Add(new() { Id = id });
        typeof(MainViewModel).GetField("_allRawDefects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, issues.ToList());
        vm.RefreshFilteredDefects(); return vm;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
