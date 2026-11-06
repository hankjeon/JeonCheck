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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
