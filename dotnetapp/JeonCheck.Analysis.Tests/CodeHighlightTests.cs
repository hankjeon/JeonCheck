using JeonCheck.Services;

static class CodeHighlightTests
{
    public static void Run(Action<string, Action> test)
    {
        test("자료형·숫자·줄주석을 구분", () =>
        {
            bool state = false;
            var spans = CCodeHighlighter.TokenizeLine("int a = 1; // 주석", ref state);
            Require(!state, "블록 상태 오염");
            Require(spans.Count > 0 && spans[0].Text == "int" && spans[0].ForegroundHex == CCodeHighlighter.TypeHex, "자료형 색상 오류");
            Require(spans.Exists(s => s.Text == "1" && s.ForegroundHex == CCodeHighlighter.NumberHex), "숫자 색상 오류");
            Require(spans[^1].Text == "// 주석" && spans[^1].ForegroundHex == CCodeHighlighter.CommentHex, "줄주석 색상 오류");
            Require(spans.Exists(s => s.Text == "a" && s.ForegroundHex == null), "일반 식별자 변경");
        });

        test("주석은 이탤릭, 일반 토큰은 호버 없음이 기본값", () =>
        {
            bool state = false;
            var spans = CCodeHighlighter.TokenizeLine("int a = 1; // 주석", ref state);
            var comment = spans.Find(s => s.ForegroundHex == CCodeHighlighter.CommentHex);
            Require(comment != null && comment.IsItalic, "주석 이탤릭 누락");
            Require(spans.TrueForAll(s => s.HoverTip == null), "토크나이저가 호버를 직접 달면 안 됨");
            var ident = spans.Find(s => s.Text == "a");
            Require(ident != null && !ident.IsItalic, "일반 식별자에 이탤릭이 들어감");
        });

        test("전처리기는 한 줄 전체를 한 색으로", () =>
        {
            bool state = false;
            var spans = CCodeHighlighter.TokenizeLine("#include <stdio.h>", ref state);
            Require(spans.Count == 1 && spans[0].Text == "#include <stdio.h>"
                && spans[0].ForegroundHex == CCodeHighlighter.PreprocessorHex, "전처리기 색상 오류");
        });

        test("블록 주석은 줄을 넘어 이어짐", () =>
        {
            bool state = false;
            var first = CCodeHighlighter.TokenizeLine("/* 시작", ref state);
            Require(state && first.Count == 1 && first[0].ForegroundHex == CCodeHighlighter.CommentHex, "블록 주석 시작 오류");
            var second = CCodeHighlighter.TokenizeLine("계속 */ int x;", ref state);
            Require(!state, "블록 주석 종료 오류");
            Require(second.Count > 0 && second[0].Text == "계속 */"
                && second[0].ForegroundHex == CCodeHighlighter.CommentHex, "블록 주석 종료 색상 오류");
            Require(second.Exists(s => s.Text == "int" && s.ForegroundHex == CCodeHighlighter.TypeHex), "주석 뒤 코드 색상 오류");
        });

        test("이스케이프 포함 문자열을 하나로", () =>
        {
            bool state = false;
            var spans = CCodeHighlighter.TokenizeLine("char* s = \"a\\\"b\";", ref state);
            Require(spans.Exists(s => s.Text == "\"a\\\"b\"" && s.ForegroundHex == CCodeHighlighter.StringHex), "문자열 색상 오류");
        });

        test("키워드와 한글 식별자", () =>
        {
            bool state = false;
            var spans = CCodeHighlighter.TokenizeLine("if (변수 > 0) return;", ref state);
            Require(spans.Exists(s => s.Text == "if" && s.ForegroundHex == CCodeHighlighter.KeywordHex), "키워드 색상 오류");
            Require(spans.Exists(s => s.Text == "변수" && s.ForegroundHex == null), "한글 식별자 변경");
            Require(spans.Exists(s => s.Text == "return" && s.ForegroundHex == CCodeHighlighter.KeywordHex), "return 색상 오류");
        });

        test("Prettify: 줄 수와 줄 번호를 유지하며 들여쓰기 정렬", () =>
        {
            string[] raw =
            {
                "  #include <stdio.h>",
                "int main(void) {",
                "int a = 1;",
                "  if (a > 0) {",
                "\ta++;",
                "      if (a == 2)",
                "a += 10;",
                "} else {",
                "        a--;",
                "    }",
                "switch (a) {",
                "case 1:",
                "a = 10;",
                "break;",
                "default:",
                "a = 0;",
                "break;",
                "}",
                "return 0;",
                "}"
            };

            var pretty = CCodePrettifier.PrettifyLines(raw);
            Require(pretty.Length == raw.Length, "줄 수가 변경됨");
            Require(pretty[0] == "#include <stdio.h>", $"전처리기 정렬 오류: '{pretty[0]}'");
            Require(pretty[1] == "int main(void) {", $"함수 헤더 정렬 오류: '{pretty[1]}'");
            Require(pretty[2] == "    int a = 1;", $"블록 1단 들여쓰기 오류: '{pretty[2]}'");
            Require(pretty[4] == "        a++;", $"블록 2단 들여쓰기 오류: '{pretty[4]}'");
            Require(pretty[6] == "            a += 10;", $"단일 문장 if 들여쓰기 오류: '{pretty[6]}'");
            Require(pretty[7] == "    } else {", $"else 블록 정렬 오류: '{pretty[7]}'");
            Require(pretty[11] == "        case 1:", $"switch case 라벨 정렬 오류: '{pretty[11]}'");
            Require(pretty[12] == "            a = 10;", $"switch case 본문 정렬 오류: '{pretty[12]}'");
            Require(pretty[14] == "        default:", $"switch default 라벨 정렬 오류: '{pretty[14]}'");
            Require(pretty[19] == "}", $"닫는 중괄호 정렬 오류: '{pretty[19]}'");
        });

        test("MainViewModel Prettify 토글 시 줄 번호 유지 및 원본 파일 미변경", () =>
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"jeoncheck_prettify_{Guid.NewGuid():N}.c");
            string originalContent = "void f(void) {\nint x = 1;\n  if (x) {\nx++;\n}\n}\n";
            File.WriteAllText(tempFile, originalContent);
            try
            {
                var vm = new JeonCheck.ViewModels.MainViewModel();
                vm.LoadFileIntoCodeViewer(tempFile);
                Require(vm.CurrentFileLines.Count == 6, "초기 줄 수 불일치");
                Require(vm.CurrentFileLines[1].CodeText == "int x = 1;", "초기 원본 줄 텍스트 불일치");

                vm.IsPrettifyEnabled = true;
                Require(vm.CurrentFileLines.Count == 6, "Prettify 후 줄 수 불일치");
                Require(vm.CurrentFileLines[1].LineNumber == 2 && vm.CurrentFileLines[1].CodeText == "    int x = 1;", "Prettify 2번째 줄 정렬 불일치");
                Require(vm.CurrentFileLines[3].LineNumber == 4 && vm.CurrentFileLines[3].CodeText == "        x++;", "Prettify 4번째 줄 정렬 불일치");
                Require(File.ReadAllText(tempFile) == originalContent, "원본 파일이 변경됨");

                vm.IsPrettifyEnabled = false;
                Require(vm.CurrentFileLines[1].CodeText == "int x = 1;", "Prettify 해제 후 원본 복원 불일치");
            }
            finally
            {
                File.Delete(tempFile);
            }
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
