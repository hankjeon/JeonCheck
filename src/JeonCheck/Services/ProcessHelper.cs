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
}
