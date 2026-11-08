namespace JeonCheck.Services;

// C 소스 한 줄 토크나이저 (주석·문자열·키워드·자료형·숫자·전처리기). 외부 의존성 없음.
// 줄 단위로 동작하며 블록 주석(/* */)은 inBlockComment로 줄을 넘어 이어간다.
public sealed class CodeSegment
{
    public CodeSegment(string text, string? foregroundHex, bool isItalic = false, string? hoverTip = null)
    {
        Text = text;
        ForegroundHex = foregroundHex;
        IsItalic = isItalic;
        HoverTip = hoverTip;
    }

    public string Text { get; }
    public string? ForegroundHex { get; } // null이면 줄 상태 색상을 그대로 상속
    public bool IsItalic { get; }
    public string? HoverTip { get; } // null이면 호버 팝업 없음
}

public static class CCodeHighlighter
{
    public const string KeywordHex = "#569CD6";
    public const string TypeHex = "#4EC9B0";
    public const string NumberHex = "#B5CEA8";
    public const string StringHex = "#CE9178";
    public const string CommentHex = "#6A9955";
    public const string PreprocessorHex = "#C586C0";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "const", "continue", "default", "do", "else",
        "enum", "extern", "for", "goto", "if", "inline", "register", "restrict",
        "return", "sizeof", "static", "struct", "switch", "typedef", "union",
        "volatile", "while", "_Alignas", "_Alignof", "_Atomic", "_Generic",
        "_Noreturn", "_Static_assert", "_Thread_local"
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "void", "char", "short", "int", "long", "float", "double",
        "signed", "unsigned", "_Bool", "_Complex", "_Imaginary", "bool"
    };

    public static List<CodeSegment> TokenizeLine(string line, ref bool inBlockComment)
    {
        var spans = new List<CodeSegment>();
        int n = line.Length;
        if (n == 0)
            return spans;

        // 전처리기 지시문: 앞 공백 뒤 # 로 시작하면 한 줄 전체를 한 색으로 표시
        int s = 0;
        while (s < n && (line[s] == ' ' || line[s] == '\t'))
            s++;
        if (!inBlockComment && s < n && line[s] == '#')
        {
            UpdateBlockState(line, ref inBlockComment);
            spans.Add(new CodeSegment(line, PreprocessorHex));
            return spans;
        }

        int i = 0;
        int plainStart = 0;
        while (i < n)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    spans.Add(new CodeSegment(line.Substring(i), CommentHex, isItalic: true));
                    return spans;
                }
                spans.Add(new CodeSegment(line.Substring(i, end + 2 - i), CommentHex, isItalic: true));
                i = end + 2;
                inBlockComment = false;
                plainStart = i;
                continue;
            }

            char c = line[i];
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                AddPlain(spans, line, plainStart, i);
                spans.Add(new CodeSegment(line.Substring(i), CommentHex, isItalic: true));
                return spans;
            }
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                AddPlain(spans, line, plainStart, i);
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    spans.Add(new CodeSegment(line.Substring(i), CommentHex, isItalic: true));
                    inBlockComment = true;
                    return spans;
                }
                spans.Add(new CodeSegment(line.Substring(i, end + 2 - i), CommentHex, isItalic: true));
                i = end + 2;
                plainStart = i;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                AddPlain(spans, line, plainStart, i);
                int end = ScanLiteral(line, i);
                spans.Add(new CodeSegment(line.Substring(i, end - i), StringHex));
                i = end;
                plainStart = i;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1])))
            {
                AddPlain(spans, line, plainStart, i);
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_' || line[j] == '\''))
                    j++;
                spans.Add(new CodeSegment(line.Substring(i, j - i), NumberHex));
                i = j;
                plainStart = i;
                continue;
            }
            if (IsIdentStart(c))
            {
                AddPlain(spans, line, plainStart, i);
                int j = i + 1;
                while (j < n && IsIdentPart(line[j]))
                    j++;
                string word = line.Substring(i, j - i);
                string? hex = Types.Contains(word) ? TypeHex : Keywords.Contains(word) ? KeywordHex : null;
                spans.Add(new CodeSegment(word, hex));
                i = j;
                plainStart = i;
                continue;
            }
            i++;
        }
        AddPlain(spans, line, plainStart, n);
        return spans;
    }

    private static void AddPlain(List<CodeSegment> spans, string line, int start, int end)
    {
        if (end > start)
            spans.Add(new CodeSegment(line.Substring(start, end - start), null));
    }

    // "..." / '...' 리터럴 끝 위치. 이스케이프(\")를 건너뛰고 닫히지 않으면 줄 끝까지.
    private static int ScanLiteral(string line, int i)
    {
        char quote = line[i];
        int n = line.Length;
        i++;
        while (i < n)
        {
            if (line[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (line[i] == quote)
                return i + 1;
            i++;
        }
        return i;
    }

    // 전처리기 줄 안의 /* */ 로 블록 주석 상태만 갱신. // 뒤는 주석이라 무시.
    private static void UpdateBlockState(string line, ref bool inBlockComment)
    {
        int i = 0;
        int n = line.Length;
        while (i < n)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0)
                    return;
                i = end + 2;
                inBlockComment = false;
            }
            else
            {
                int lineComment = line.IndexOf("//", i, StringComparison.Ordinal);
                int open = line.IndexOf("/*", i, StringComparison.Ordinal);
                if (open < 0 || (lineComment >= 0 && lineComment < open))
                    return;
                int end = line.IndexOf("*/", open + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlockComment = true;
                    return;
                }
                i = end + 2;
            }
        }
    }

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
