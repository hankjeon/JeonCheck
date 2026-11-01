using System.Diagnostics;
using System.IO;

namespace JeonCheck.Services;

/// <summary>
/// 외부 분석 도구(Cppcheck, Clang-Tidy) 프로세스 실행 보조 기능
/// </summary>
public static class ProcessHelper
{
    /// <summary>
    /// 프로세스 종료를 기다리되, 취소되면 프로세스 트리를 강제 종료한 뒤 취소 예외를 다시 던진다.
    /// </summary>
    public static async Task WaitForExitOrKillAsync(Process proc, CancellationToken cancellationToken)
    {
        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
    }

    public static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch
        {
            // 이미 종료되었거나 접근 불가한 경우 무시
        }
    }

    /// <summary>
    /// 도구 출력에서 오류로 보이는 줄을 앞에서부터 최대 몇 줄만 뽑아 안내 메시지로 요약한다.
    /// </summary>
    public static string SummarizeErrorLines(string output, int maxLines = 3)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        var lines = new List<string>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null && lines.Count < maxLines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("<", StringComparison.Ordinal))
                continue;
            if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase))
                lines.Add(trimmed.Length > 300 ? trimmed.Substring(0, 300) + "..." : trimmed);
        }

        return lines.Count == 0 ? string.Empty : "원문: " + string.Join(" / ", lines);
    }

    /// <summary>
    /// 사용자 입력 -D 매크로 문자열을 개별 매크로 정의(NAME 또는 NAME=VALUE) 목록으로 분리한다.
    /// 세미콜론(;), 쉼표(,) 구분자를 지원하며, 공백 구분 시에도 'long int' 같은 다중 단어 타입 값을 보존한다.
    /// </summary>
    public static List<string> ParseCustomDefines(string customDefines)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(customDefines))
            return result;

        // ; 또는 , 가 포함되어 있으면 해당 구분자를 우선 사용하여 공백 포함 매크로 값(예: __INTPTR_TYPE__=long int)을 그대로 유지
        if (customDefines.Contains(';') || customDefines.Contains(','))
        {
            foreach (var part in customDefines.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string cleaned = CleanDefineToken(part);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    result.Add(cleaned);
            }
            return result;
        }

        // 공백으로만 구분된 경우: C 타입 키워드(int, long, short, char, signed, unsigned)가 이전 'NAME=...' 뒤에 이어지면 하나로 결합
        var rawTokens = customDefines.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in rawTokens)
        {
            string cleaned = CleanDefineToken(raw);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            if (result.Count > 0 &&
                result[^1].Contains('=') &&
                !raw.StartsWith("-D", StringComparison.Ordinal) &&
                !cleaned.Contains('=') &&
                IsCTypeContinuationWord(cleaned))
            {
                result[^1] = result[^1] + " " + cleaned;
            }
            else
            {
                result.Add(cleaned);
            }
        }

        return result;
    }

    private static string CleanDefineToken(string token)
    {
        string cleaned = token.Trim().Trim('"');
        if (cleaned.StartsWith("-D", StringComparison.Ordinal))
            cleaned = cleaned.Substring(2).Trim().Trim('"');
        return cleaned;
    }

    private static bool IsCTypeContinuationWord(string word)
    {
        return word.Equals("int", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("long", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("short", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("char", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("signed", StringComparison.OrdinalIgnoreCase) ||
               word.Equals("unsigned", StringComparison.OrdinalIgnoreCase);
    }
}
