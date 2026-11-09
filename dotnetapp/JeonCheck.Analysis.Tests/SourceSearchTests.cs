using System.Text;
using System.Text.RegularExpressions;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;

static class SourceSearchTests
{
    public static void Run(Action<string, Action> test)
    {
        test("선택: 식별자·한글·숫자·밑줄과 구두점 경계", () =>
        {
            const string text = "int 한글_var2 = getValue(x);";
            Require(CodeTextSelection.WordAt(text, 7) == new CodeTextRange(4, 7), "식별자 경계 오류");
            Require(CodeTextSelection.WordAt(text, 17) == new CodeTextRange(14, 8), "함수명 경계 오류");
            Require(CodeTextSelection.WordAt(text, 22) == new CodeTextRange(22, 1), "괄호는 한 문자 선택");
            Require(CodeTextSelection.WordAt("", 0).Length == 0, "빈 줄 오류");
        });
        test("선택: 더블 클릭 드래그 양방향 확장과 되돌리기", () =>
        {
            const string text = "one two three";
            var anchor = CodeTextSelection.WordAt(text, 5);
            Require(CodeTextSelection.ExtendWord(text, anchor, 10) == new CodeTextRange(4, 9), "오른쪽 단어 확장");
            Require(CodeTextSelection.ExtendWord(text, anchor, 0) == new CodeTextRange(0, 7), "왼쪽 단어 확장");
            Require(CodeTextSelection.ExtendWord(text, anchor, 5) == anchor, "원래 단어로 축소");
            Require(CodeTextSelection.ExtendWord(text, anchor, 100).Start == 4, "줄 끝 제한");
        });
        test("검색: 일반 문자열·대소문자·단어 전체·정규식 조합", () =>
        {
            const string text = "foo Foo foo2 _foo foo_bar 한글foo foo 한글 a+b aab";
            int Count(string query, bool regex = false, bool matchCase = false, bool whole = false) =>
                new SourceSearchOptions(query, regex, matchCase, whole).CreateRegex().Matches(text).Count;
            Require(Count("foo") == 7, "부분 일치");
            Require(Count("foo", matchCase: true) == 6, "대소문자");
            Require(Count("foo", whole: true) == 3, "식별자 경계");
            Require(Count("foo|한글", regex: true, whole: true) == 4, "정규식 전체 일치 조합");
            Require(Count("a+b") == 1, "일반 문자열 메타문자 이스케이프");
            Require(Count("a+b", regex: true) == 1, "정규식 검색");
            foreach (string bad in new[] { "", "[", "a\nb" })
            {
                bool rejected = false;
                try { new SourceSearchOptions(bad, true, false, false).CreateRegex(); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "잘못된 검색 조건 허용");
            }
        });
        test("검색: 범위·제거 항목 제외·중복 파일 제거", () => WithFiles((dir, a, b) =>
        {
            var target = SourceTreeNode.CreateFromPath(dir, true);
            target.Children.Remove(target.Children.Single(n => n.FullPath == b));
            var reference = SourceTreeNode.CreateFromPath(b, false);
            var targets = new[] { target, SourceTreeNode.CreateFromPath(a, true) };
            Require(SourceSearch.CollectPaths(targets, new[] { reference }, 0, a).Length == 2, "전체/중복");
            Require(SourceSearch.CollectPaths(targets, new[] { reference }, 1, a).SequenceEqual(new[] { a }), "대상/제거");
            Require(SourceSearch.CollectPaths(targets, new[] { reference }, 2, a).SequenceEqual(new[] { b }), "참조");
            Require(SourceSearch.CollectPaths(targets, new[] { reference }, 3, a).SequenceEqual(new[] { a }), "현재 파일");
            Require(SourceSearch.CollectPaths(targets, new[] { reference }, 3, "").Length == 0, "현재 파일 없음");
        }));
        test("검색: 인코딩·줄/열·같은 줄 여러 일치·읽기 실패", () => WithFiles((dir, a, b) =>
        {
            var regex = new SourceSearchOptions("변수", false, true, false).CreateRegex();
            var report = SourceSearch.Search(new[] { a, b, Path.Combine(dir, "missing.c") }, regex, default);
            Require(report.Files.Count == 2 && report.Warnings.Count == 1, "파일 그룹/오류 안내");
            var hits = report.Files[0].Hits;
            Require(hits.Count == 2 && hits[0].Line == 2 && hits[0].Column == 5 && hits[1].Column == 8, "실제 줄/열");
            Require(report.Files[1].Hits[0].EncodingName == "CP949", "CP949 판별");
            Require(report.Files[1].Hits[0].LineText == "변수", "한글 디코딩");
            var empty = SourceSearch.Search(new[] { a }, new SourceSearchOptions("^", true, false, false).CreateRegex(), default);
            Require(empty.Files.Count == 0, "빈 일치 제외");
        }));
        test("검색: 취소와 정규식 시간 제한", () => WithFiles((_, a, _) =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool canceled = false;
            try { SourceSearch.Search(new[] { a }, new SourceSearchOptions("foo", false, false, false).CreateRegex(), cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled, "취소 무시");
            File.WriteAllText(a, new string('a', 10000) + "!");
            bool timeout = false;
            try { SourceSearch.Search(new[] { a }, new SourceSearchOptions("(a+)+$", true, false, false).CreateRegex(), default); }
            catch (RegexMatchTimeoutException) { timeout = true; }
            Require(timeout, "정규식 무한 대기 방지");
        }));
        test("검색 VM: 우클릭은 검색어만 입력·버튼 실행·결과 강조·변경 감지", () => WithFiles((_, a, b) =>
        {
            var vm = new MainViewModel();
            vm.ClearWorkspace();
            vm.AddTargetPath(a);
            vm.AddNonTargetPath(b);
            vm.SourceSearchRegex = true;
            vm.PrepareSourceSearch("변수");
            Require(!vm.SourceSearchRegex && vm.SourceSearchFiles.Count == 0 && !vm.IsSourceSearching, "우클릭 자동 실행 금지");
            vm.SearchSourceAsync().GetAwaiter().GetResult();
            Require(vm.SourceSearchFiles.Count == 2 && !vm.IsSourceSearching, "버튼 검색");
            var hit = vm.SourceSearchFiles[0].Hits[0];
            vm.SourceSearchText = "다른 검색어";
            vm.IsPrettifyEnabled = true;
            Require(vm.OpenSourceSearchHit(hit), "결과 열기");
            var line = vm.CurrentFileLines[hit.Line - 1];
            Require(line.SearchRanges.Count == 2 && line.CodeText.Substring(line.SearchRanges[0].Start, 2) == "변수", "실행 조건/Prettify 강조");
            Require(vm.OpenSourceSearchHit(vm.SourceSearchFiles[0].Hits[1]), "같은 줄 두 번째 결과 열기");
            Require(vm.CurrentFileLines[1].SearchRanges[0].Start > vm.CurrentFileLines[1].SearchRanges[1].Start, "선택한 일치 위치 우선");
            File.WriteAllText(hit.FilePath, "변경됨");
            Require(!vm.OpenSourceSearchHit(hit) && vm.SourceSearchStatus.Contains("변경"), "오래된 위치 차단");
            vm.ClearWorkspace();
            Require(vm.SourceSearchFiles.Count == 0, "작업 공간 초기화");
        }));
        test("검색 강조: Prettify 탭·들여쓰기·정규식 원본 범위 변환", () =>
        {
            const string raw = "\t  foo\tbar foo  ";
            string display = CCodePrettifier.PrettifyLines(new[] { raw })[0];
            var range = CCodePrettifier.MapRangeToDisplay(raw, display, new(raw.IndexOf("bar"), 3));
            Require(display.Substring(range.Start, range.Length) == "bar", "탭 열 변환");
            var match = new SourceSearchOptions(@"^\s+foo", true, true, false).CreateRegex().Match(raw);
            range = CCodePrettifier.MapRangeToDisplay(raw, display, new(match.Index, match.Length));
            Require(display.Substring(range.Start, range.Length) == "foo", "원본 들여쓰기 정규식 강조");
            range = CCodePrettifier.MapRangeToDisplay(raw, display, new(raw.LastIndexOf("foo"), 3));
            Require(range.Start == display.LastIndexOf("foo"), "같은 줄 두 번째 단어 위치");
        });
        test("검색 VM: 오류·취소·이전 프로젝트 검색 완료 유입 방지", () => WithFiles((_, a, _) =>
        {
            var vm = new MainViewModel();
            vm.ClearWorkspace();
            vm.AddTargetPath(a);
            vm.SourceSearchText = "[";
            vm.SourceSearchRegex = true;
            vm.SearchSourceAsync().GetAwaiter().GetResult();
            Require(vm.SourceSearchStatus.Contains("오류") && !vm.IsSourceSearching, "잘못된 정규식 안내");
            File.WriteAllText(a, new string('a', 10000) + "!");
            vm.SourceSearchText = "(a+)+$";
            var task = vm.SearchSourceAsync();
            vm.CancelSourceSearch();
            task.GetAwaiter().GetResult();
            Require(!vm.IsSourceSearching && vm.SourceSearchFiles.Count == 0, "VM 취소");
            task = vm.SearchSourceAsync();
            vm.ClearWorkspace();
            task.GetAwaiter().GetResult();
            Require(!vm.IsSourceSearching && vm.SourceSearchFiles.Count == 0 && vm.SourceSearchStatus.Contains("버튼"), "이전 프로젝트 결과 유입");
        }));
    }

    private static void WithFiles(Action<string, string, string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jc_search_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.c"), b = Path.Combine(dir, "b.h");
            File.WriteAllText(a, "// 첫 줄\r\n    변수 변수;\r\n", new UTF8Encoding(true));
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            File.WriteAllText(b, "변수\n", Encoding.GetEncoding(949));
            action(dir, a, b);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
