using System.IO;
using JeonCheck.Models;

namespace JeonCheck.Services;

public static class RuleSummaryBuilder
{
    public static List<RuleIssueGroup> Build(IEnumerable<AnalysisIssue> issues)
    {
        // 한 번의 갱신에서 같은 파일을 여러 규칙이 참조해도 한 번만 읽는다.
        var sourceLines = new Dictionary<string, string[]?>();

        string ReadCode(AnalysisIssue issue)
        {
            if (issue.Line <= 0)
                return "줄 정보가 없는 진단입니다.";

            string key = AnalysisIssue.NormalizePath(issue.FilePath);
            if (!sourceLines.TryGetValue(key, out var lines))
            {
                try { lines = File.ReadAllLines(issue.FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    lines = null;
                }
                sourceLines[key] = lines;
            }

            if (lines == null)
                return "코드를 읽을 수 없습니다. 파일 경로와 읽기 권한을 확인하십시오.";
            if (issue.Line > lines.Length)
                return "해당 줄이 현재 파일에 없습니다.";

            string code = lines[issue.Line - 1].Trim();
            return code.Length == 0 ? "(빈 줄)" : code;
        }

        return issues
            .GroupBy(issue => issue.RuleId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RuleIssueGroup
            {
                RuleId = group.Key,
                Title = group.First().Title,
                Occurrences = group
                    .OrderBy(issue => issue.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(issue => issue.Line)
                    .ThenBy(issue => issue.Column)
                    .Select(issue => new RuleIssueOccurrence { Issue = issue, CodeText = ReadCode(issue) })
                    .ToList()
            }).ToList();
    }
}
