using System.Globalization;

namespace JeonCheck.Services;

public readonly record struct CodeTextRange(int Start, int Length);

// 더블 클릭과 검색의 단어 경계를 동일하게 유지한다.
public static class CodeTextSelection
{
    public const string WordCharacters = @"\p{L}\p{Nd}\p{Mn}\p{Mc}\p{Pc}";

    public static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) ||
        char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;

    public static CodeTextRange WordAt(string text, int index)
    {
        if (text.Length == 0) return new(0, 0);
        index = Math.Clamp(index, 0, text.Length - 1);
        bool word = IsWordCharacter(text[index]);
        bool space = char.IsWhiteSpace(text[index]);
        if (!word && !space) return new(index, 1);
        bool SameKind(char c) => word ? IsWordCharacter(c) : char.IsWhiteSpace(c);
        int start = index, end = index + 1;
        while (start > 0 && SameKind(text[start - 1])) start--;
        while (end < text.Length && SameKind(text[end])) end++;
        return new(start, end - start);
    }

    public static CodeTextRange ExtendWord(string text, CodeTextRange anchor, int index)
    {
        var word = WordAt(text, index);
        int start = Math.Min(anchor.Start, word.Start);
        int end = Math.Max(anchor.Start + anchor.Length, word.Start + word.Length);
        return new(start, end - start);
    }
}
