using System.Text;

namespace JeonCheck.Services;

/// <summary>
/// 원본 파일과 줄 번호(LineNumber)를 1:1로 유지하면서 C 코드의 앞쪽 들여쓰기와 탭 간격만 정렬하는 뷰어 전용 프리티파이어.
/// </summary>
public static class CCodePrettifier
{
    // 검색 결과의 원본 열을 화면용 들여쓰기·탭 확장 위치로 변환한다.
    public static CodeTextRange MapRangeToDisplay(string raw, string display, CodeTextRange range, int tabSize = 4)
    {
        if (raw == display) return range;
        int leading = raw.Length - raw.TrimStart(' ', '\t', '\r').Length;
        int end = raw.TrimEnd(' ', '\t', '\r').Length;
        int ExpandedColumn(int position)
        {
            int column = 0;
            for (int i = leading; i < Math.Min(position, end); i++)
                column += raw[i] == '\t' ? tabSize - column % tabSize : 1;
            return column;
        }
        int indent = Math.Max(0, display.Length - ExpandedColumn(end));
        int Map(int position) => Math.Clamp(position < leading ? 0 : indent + ExpandedColumn(position), 0, display.Length);
        int start = Map(range.Start);
        return new(start, Math.Max(0, Map(range.Start + range.Length) - start));
    }

    public static string[] PrettifyLines(IReadOnlyList<string> rawLines, int indentSize = 4)
    {
        if (rawLines == null || rawLines.Count == 0)
            return Array.Empty<string>();

        if (indentSize < 1)
            indentSize = 4;

        var result = new string[rawLines.Count];

        bool inBlockComment = false;
        int blockCommentIndentLevel = 0;
        bool inMacroContinuation = false;

        int braceDepth = 0;
        int parenDepth = 0;
        var caseBodyActiveStack = new Stack<bool>();
        bool justSawCaseLabel = false;
        int pendingSingleStmtIndent = 0;
        bool pendingControlParen = false;

        for (int lineIdx = 0; lineIdx < rawLines.Count; lineIdx++)
        {
            string rawLine = rawLines[lineIdx] ?? string.Empty;
            string trimmed = rawLine.Trim(' ', '\t', '\r');

            if (trimmed.Length == 0)
            {
                result[lineIdx] = string.Empty;
                if (!inBlockComment)
                    inMacroContinuation = false;
                continue;
            }

            // 1. 이전 줄에서 시작된 블록 주석(/* ... */) 내부인 경우
            if (inBlockComment)
            {
                string expanded = ExpandInternalTabs(trimmed, indentSize);
                int commentSpaces = blockCommentIndentLevel * indentSize;
                if (expanded.StartsWith('*'))
                {
                    result[lineIdx] = new string(' ', commentSpaces + 1) + expanded;
                }
                else
                {
                    result[lineIdx] = new string(' ', commentSpaces) + expanded;
                }

                int closeIdx = expanded.IndexOf("*/", StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    inBlockComment = false;
                    string afterComment = expanded.Substring(closeIdx + 2).Trim();
                    if (afterComment.Length > 0)
                    {
                        ScanCodeLineAndUpdateState(
                            afterComment,
                            0,
                            blockCommentIndentLevel,
                            ref braceDepth,
                            ref parenDepth,
                            caseBodyActiveStack,
                            ref justSawCaseLabel,
                            ref pendingSingleStmtIndent,
                            ref pendingControlParen,
                            ref inBlockComment,
                            ref blockCommentIndentLevel);
                    }
                }
                continue;
            }

            // 2. 이전 줄의 전처리기 매크로 줄바꿈(\)이 이어지는 경우
            if (inMacroContinuation)
            {
                int contIndentLevel = Math.Max(1, braceDepth + 1);
                string expanded = ExpandInternalTabs(trimmed, indentSize);
                result[lineIdx] = new string(' ', contIndentLevel * indentSize) + expanded;
                UpdateBlockCommentStateInPreprocessor(expanded, contIndentLevel, ref inBlockComment, ref blockCommentIndentLevel);
                inMacroContinuation = EndsWithLineContinuation(expanded);
                continue;
            }

            // 3. 전처리기 지시문(#include, #define, #ifdef 등)
            if (trimmed[0] == '#')
            {
                string expanded = ExpandInternalTabs(trimmed, indentSize);
                result[lineIdx] = expanded;
                UpdateBlockCommentStateInPreprocessor(expanded, 0, ref inBlockComment, ref blockCommentIndentLevel);
                inMacroContinuation = EndsWithLineContinuation(expanded);
                continue;
            }

            // 4. 일반 C 코드 줄: 선행 닫는 중괄호/괄호 처리
            int scanStart = ConsumeLeadingCloseBraces(trimmed, ref braceDepth, caseBodyActiveStack, out int leadingCloseBraces);
            if (leadingCloseBraces > 0)
            {
                pendingSingleStmtIndent = 0;
                justSawCaseLabel = false;
            }

            int leadingCloseParens = CountLeadingCloseParens(trimmed, scanStart);

            bool startsWithOpenBrace = scanStart < trimmed.Length && trimmed[scanStart] == '{';
            if (startsWithOpenBrace)
            {
                // if (...) 다음 줄에 오는 { 는 if 와 같은 들여쓰기 레벨에 맞춤
                pendingSingleStmtIndent = 0;

                // case 라벨 바로 다음 줄에 { 가 오는 경우 이중 들여쓰기 방지
                if (justSawCaseLabel && caseBodyActiveStack.Count > 0 && caseBodyActiveStack.Peek())
                {
                    caseBodyActiveStack.Pop();
                    caseBodyActiveStack.Push(false);
                }
            }

            bool isCaseOrDefault = IsCaseOrDefaultLabel(trimmed);
            bool isGotoLabel = !isCaseOrDefault && braceDepth > 0 && IsGotoLabel(trimmed);

            if (isCaseOrDefault)
            {
                pendingSingleStmtIndent = 0;
                if (caseBodyActiveStack.Count > 0 && caseBodyActiveStack.Peek())
                {
                    caseBodyActiveStack.Pop();
                    caseBodyActiveStack.Push(false);
                }
            }

            int caseOffset = (caseBodyActiveStack.Count > 0 && caseBodyActiveStack.Peek()) ? 1 : 0;
            int effectiveParenIndent = Math.Max(0, parenDepth - leadingCloseParens) > 0 ? 1 : 0;
            int labelOutdent = isGotoLabel ? 1 : 0;

            int lineIndentLevel = Math.Max(
                0,
                braceDepth + caseOffset + pendingSingleStmtIndent + effectiveParenIndent - labelOutdent);

            string expandedCode = ExpandInternalTabs(trimmed, indentSize);
            result[lineIdx] = new string(' ', lineIndentLevel * indentSize) + expandedCode;

            // case / default 라벨 처리 후 다음 줄부터 본문 들여쓰기(+1) 활성화
            if (isCaseOrDefault)
            {
                if (caseBodyActiveStack.Count > 0)
                {
                    caseBodyActiveStack.Pop();
                    caseBodyActiveStack.Push(true);
                }
                justSawCaseLabel = true;
            }
            else if (!IsPureCommentLine(trimmed))
            {
                justSawCaseLabel = false;
            }

            // 줄의 나머지 토큰을 스캔하여 중괄호/괄호 깊이, 주석 상태, 단일 문장 제어문 상태를 갱신
            ScanCodeLineAndUpdateState(
                trimmed,
                scanStart,
                lineIndentLevel,
                ref braceDepth,
                ref parenDepth,
                caseBodyActiveStack,
                ref justSawCaseLabel,
                ref pendingSingleStmtIndent,
                ref pendingControlParen,
                ref inBlockComment,
                ref blockCommentIndentLevel);
        }

        return result;
    }

    private static int ConsumeLeadingCloseBraces(
        string trimmed,
        ref int braceDepth,
        Stack<bool> caseBodyActiveStack,
        out int consumedCount)
    {
        consumedCount = 0;
        int i = 0;
        while (i < trimmed.Length)
        {
            while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t'))
                i++;

            if (i < trimmed.Length && trimmed[i] == '}')
            {
                consumedCount++;
                if (braceDepth > 0)
                    braceDepth--;
                if (caseBodyActiveStack.Count > 0)
                    caseBodyActiveStack.Pop();
                i++;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    private static int CountLeadingCloseParens(string trimmed, int startIndex)
    {
        int count = 0;
        int i = startIndex;
        while (i < trimmed.Length)
        {
            while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t'))
                i++;

            if (i < trimmed.Length && (trimmed[i] == ')' || trimmed[i] == ']'))
            {
                count++;
                i++;
            }
            else
            {
                break;
            }
        }
        return count;
    }

    private static void ScanCodeLineAndUpdateState(
        string line,
        int startIndex,
        int currentLineIndentLevel,
        ref int braceDepth,
        ref int parenDepth,
        Stack<bool> caseBodyActiveStack,
        ref bool justSawCaseLabel,
        ref int pendingSingleStmtIndent,
        ref bool pendingControlParen,
        ref bool inBlockComment,
        ref int blockCommentIndentLevel)
    {
        int n = line.Length;
        int i = startIndex;
        char lastSignificantChar = '\0';
        string? firstKeyword = null;
        string? lastKeyword = null;
        bool sawOpenBrace = false;
        bool sawCloseBrace = startIndex > 0;
        bool sawSemicolonAtTopParens = false;

        while (i < n)
        {
            char c = line[i];

            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                break; // 한 줄 주석 이후는 무시
            }

            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlockComment = true;
                    blockCommentIndentLevel = currentLineIndentLevel;
                    return;
                }
                i = end + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(line, i);
                lastSignificantChar = c;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                caseBodyActiveStack.Push(false);
                sawOpenBrace = true;
                pendingSingleStmtIndent = 0;
                pendingControlParen = false;
                lastSignificantChar = '{';
                i++;
                continue;
            }

            if (c == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                if (caseBodyActiveStack.Count > 0)
                    caseBodyActiveStack.Pop();
                sawCloseBrace = true;
                pendingSingleStmtIndent = 0;
                pendingControlParen = false;
                lastSignificantChar = '}';
                i++;
                continue;
            }

            if (c == '(' || c == '[')
            {
                parenDepth++;
                lastSignificantChar = c;
                i++;
                continue;
            }

            if (c == ')' || c == ']')
            {
                if (parenDepth > 0)
                    parenDepth--;
                lastSignificantChar = c;
                i++;
                continue;
            }

            if (c == ';')
            {
                if (parenDepth == 0)
                    sawSemicolonAtTopParens = true;
                lastSignificantChar = ';';
                i++;
                continue;
            }

            if (IsIdentStart(c))
            {
                int j = i + 1;
                while (j < n && IsIdentPart(line[j]))
                    j++;
                string word = line.Substring(i, j - i);
                firstKeyword ??= word;
                lastKeyword = word;
                lastSignificantChar = word[^1];
                i = j;
                continue;
            }

            if (c != ' ' && c != '\t')
            {
                lastSignificantChar = c;
            }

            i++;
        }

        // 주석만 있는 줄이면 제어문 상태 유지
        if (lastSignificantChar == '\0' && !sawCloseBrace)
            return;

        if (sawOpenBrace)
        {
            pendingSingleStmtIndent = 0;
            pendingControlParen = false;
            return;
        }

        // if (...), for (...), while (...), switch (...) 또는 else / do 뒤의 단일 문장 들여쓰기 판단
        bool startsControlWithParen = firstKeyword is "if" or "for" or "while" or "switch" ||
                                      (firstKeyword == "else" && string.Equals(lastKeyword, "if", StringComparison.Ordinal));
        bool endsWithElseOrDo = string.Equals(lastKeyword, "else", StringComparison.Ordinal) ||
                                string.Equals(lastKeyword, "do", StringComparison.Ordinal);

        if (startsControlWithParen && parenDepth > 0)
        {
            pendingControlParen = true;
            return;
        }

        if ((startsControlWithParen || pendingControlParen) && parenDepth == 0 && lastSignificantChar == ')' && !sawSemicolonAtTopParens)
        {
            pendingControlParen = false;
            pendingSingleStmtIndent = 1;
            return;
        }

        if (endsWithElseOrDo && parenDepth == 0 && !sawSemicolonAtTopParens)
        {
            pendingControlParen = false;
            pendingSingleStmtIndent = 1;
            return;
        }

        if (sawSemicolonAtTopParens || sawCloseBrace)
        {
            pendingSingleStmtIndent = 0;
            pendingControlParen = false;
            if (justSawCaseLabel && (firstKeyword is "break" or "return" or "continue" or "goto"))
            {
                justSawCaseLabel = false;
            }
        }
    }

    private static void UpdateBlockCommentStateInPreprocessor(
        string line,
        int indentLevel,
        ref bool inBlockComment,
        ref int blockCommentIndentLevel)
    {
        int i = 0;
        int n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
                return;

            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlockComment = true;
                    blockCommentIndentLevel = indentLevel;
                    return;
                }
                i = end + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(line, i);
                continue;
            }

            i++;
        }
    }

    private static bool EndsWithLineContinuation(string trimmed)
    {
        return trimmed.Length > 0 && trimmed[^1] == '\\';
    }

    private static bool IsPureCommentLine(string trimmed)
    {
        return trimmed.StartsWith("//", StringComparison.Ordinal) ||
               (trimmed.StartsWith("/*", StringComparison.Ordinal) && trimmed.EndsWith("*/", StringComparison.Ordinal));
    }

    private static bool IsCaseOrDefaultLabel(string trimmed)
    {
        if (trimmed.StartsWith("default", StringComparison.Ordinal))
        {
            int i = "default".Length;
            while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t'))
                i++;
            return i < trimmed.Length && trimmed[i] == ':' && (i + 1 >= trimmed.Length || trimmed[i + 1] != ':');
        }

        if (trimmed.StartsWith("case", StringComparison.Ordinal) &&
            trimmed.Length > 4 &&
            (trimmed[4] == ' ' || trimmed[4] == '\t' || trimmed[4] == '(' || trimmed[4] == '\''))
        {
            return ContainsLabelColon(trimmed, 4);
        }

        return false;
    }

    private static bool IsGotoLabel(string trimmed)
    {
        if (!IsIdentStart(trimmed[0]))
            return false;

        int i = 1;
        while (i < trimmed.Length && IsIdentPart(trimmed[i]))
            i++;

        string ident = trimmed.Substring(0, i);
        if (ident is "case" or "default")
            return false;

        while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t'))
            i++;

        if (i >= trimmed.Length || trimmed[i] != ':')
            return false;

        if (i + 1 < trimmed.Length && trimmed[i + 1] == ':')
            return false;

        // 라벨 뒤에는 주석이나 공백만 있거나 문장이 올 수 있으나 삼항 연산자(? :)와 구분하기 위해 처음 식별자 바로 뒤 콜론만 인정
        return true;
    }

    private static bool ContainsLabelColon(string line, int startIndex)
    {
        int i = startIndex;
        int n = line.Length;
        int paren = 0;
        while (i < n)
        {
            char c = line[i];
            if (c == '/' && i + 1 < n && (line[i + 1] == '/' || line[i + 1] == '*'))
                return false;
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(line, i);
                continue;
            }
            if (c == '(') paren++;
            else if (c == ')' && paren > 0) paren--;
            else if (c == ':' && paren == 0)
                return true;
            else if (c == ';' && paren == 0)
                return false;
            i++;
        }
        return false;
    }

    private static string ExpandInternalTabs(string text, int tabSize)
    {
        if (!text.Contains('\t'))
            return text;

        var sb = new StringBuilder(text.Length + 8);
        int col = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\t')
            {
                int spaces = tabSize - (col % tabSize);
                sb.Append(' ', spaces);
                col += spaces;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }

    private static int SkipLiteral(string line, int quoteStart)
    {
        char quote = line[quoteStart];
        int i = quoteStart + 1;
        while (i < line.Length)
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
        return line.Length;
    }

    private static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private static bool IsIdentPart(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');
}
