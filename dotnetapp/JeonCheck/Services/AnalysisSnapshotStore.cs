using System.IO;
using System.Security.Cryptography;
using JeonCheck.Models;

namespace JeonCheck.Services;

// 분석 결과 스냅샷의 지문·구간·예외 재고정·병합 도우미. UI 없이 순수 로직만 둔다.
public static class AnalysisSnapshotStore
{
    // 파일 내용 해시(SHA-256). 읽을 수 없으면 null.
    public static string? ComputeFileHash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    // 분석 입력 전체의 해시. 달라지면 전체 재분석이 필요하다.
    public static string ComputeSettingsHash(
        string presetId,
        string? platformXml,
        string cStandard,
        IEnumerable<string> defines,
        bool forceAllIfdefConfigs,
        bool enableClangTidy,
        IEnumerable<string> enabledRuleIds,
        string rulesDbHash)
    {
        var parts = new List<string>
        {
            presetId ?? string.Empty,
            platformXml ?? string.Empty,
            cStandard ?? string.Empty,
            string.Join(" ", (defines ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).OrderBy(d => d)),
            forceAllIfdefConfigs ? "force" : string.Empty,
            enableClangTidy ? "clang" : string.Empty,
            string.Join(",", (enabledRuleIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id)),
            rulesDbHash ?? string.Empty
        };
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))));
    }

    // 줄 번호 집합을 연속 구간으로 압축 (프로젝트 파일 크기 절감)
    public static List<SnapshotLineRange> ToRanges(IEnumerable<int> lines)
    {
        var ranges = new List<SnapshotLineRange>();
        foreach (int line in (lines ?? Enumerable.Empty<int>()).Where(l => l > 0).Distinct().OrderBy(l => l))
        {
            if (ranges.Count > 0 && line == ranges[^1].End + 1)
                ranges[^1].End = line;
            else if (ranges.Count == 0 || line > ranges[^1].End)
                ranges.Add(new SnapshotLineRange { Start = line, End = line });
        }
        return ranges;
    }

    public static HashSet<int> FromRanges(IEnumerable<SnapshotLineRange>? ranges)
    {
        var set = new HashSet<int>();
        if (ranges == null)
            return set;
        foreach (var r in ranges)
        {
            if (r == null || r.End < r.Start)
                continue;
            for (int line = Math.Max(r.Start, 1); line <= r.End; line++)
                set.Add(line);
        }
        return set;
    }

    public sealed record ReanchorSummary(int Reanchored, int NeedsReview);

    // 예외 항목의 줄을 코드 내용 기준으로 재고정. 파일이 바뀌어 줄이 밀렸으면 근처에서 같은 코드를 찾는다.
    public static ReanchorSummary ReanchorSuppressions(
        IList<SuppressedIssueEntry> entries,
        Func<string, string[]?> readLines,
        int window = 20)
    {
        int reanchored = 0;
        int needsReview = 0;
        var cache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.CodeText))
            {
                entry.NeedsReview = false;
                continue;
            }

            string key = AnalysisIssue.NormalizePath(entry.FilePath);
            if (!cache.TryGetValue(key, out var lines))
            {
                try { lines = readLines(entry.FilePath); }
                catch { lines = null; }
                cache[key] = lines;
            }

            if (lines == null || lines.Length == 0)
            {
                entry.NeedsReview = true;
                needsReview++;
                continue;
            }

            if (entry.Line >= 1 && entry.Line <= lines.Length && SameCode(lines[entry.Line - 1], entry.CodeText))
            {
                entry.NeedsReview = false;
                continue;
            }

            int found = -1;
            for (int d = 1; d <= window && found < 0; d++)
            {
                foreach (int cand in new[] { entry.Line - d, entry.Line + d })
                {
                    if (cand >= 1 && cand <= lines.Length && SameCode(lines[cand - 1], entry.CodeText))
                    {
                        found = cand;
                        break;
                    }
                }
            }

            if (found > 0)
            {
                entry.Line = found;
                entry.NeedsReview = false;
                reanchored++;
            }
            else
            {
                entry.NeedsReview = true;
                needsReview++;
            }
        }

        return new ReanchorSummary(reanchored, needsReview);
    }

    private static bool SameCode(string? a, string? b)
        => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);

    // 증분 실행 결과를 기존 결과에 병합. replacedNormPaths에 속한 파일의 기존 결함·커버리지·파일 노트를 교체한다.
    public static void ReplaceFileResults(
        List<AnalysisIssue> allDefects,
        Dictionary<string, FileLineCoverage> coverageByFile,
        List<AnalysisIssue> notesAndInfos,
        ISet<string> replacedNormPaths,
        AnalysisRunResult fresh,
        bool replaceGlobalNotes)
    {
        allDefects.RemoveAll(d => replacedNormPaths.Contains(AnalysisIssue.NormalizePath(d.FilePath)));
        var keptKeys = new HashSet<string>(allDefects.Select(d =>
            AnalysisIssue.MakePositionKey(d.RuleId, d.FilePath, d.Line, d.Column)));
        foreach (var d in fresh.Defects)
        {
            if (keptKeys.Add(AnalysisIssue.MakePositionKey(d.RuleId, d.FilePath, d.Line, d.Column)))
                allDefects.Add(d);
        }

        foreach (string key in replacedNormPaths)
            coverageByFile.Remove(key);
        foreach (var kvp in fresh.CoverageByFile)
            coverageByFile[kvp.Key] = kvp.Value;

        notesAndInfos.RemoveAll(n => !string.IsNullOrEmpty(n.FilePath)
            && replacedNormPaths.Contains(AnalysisIssue.NormalizePath(n.FilePath)));
        var keptNoteKeys = new HashSet<string>(notesAndInfos.Select(n =>
            n.RuleId + "|" + AnalysisIssue.NormalizePath(n.FilePath) + "|" + n.Line + "|" + n.Message));
        foreach (var n in fresh.NotesAndInfos.Where(n => !string.IsNullOrEmpty(n.FilePath)))
        {
            string key = n.RuleId + "|" + AnalysisIssue.NormalizePath(n.FilePath) + "|" + n.Line + "|" + n.Message;
            if (keptNoteKeys.Add(key))
                notesAndInfos.Add(n);
        }
        if (replaceGlobalNotes)
        {
            notesAndInfos.RemoveAll(n => string.IsNullOrEmpty(n.FilePath));
            notesAndInfos.AddRange(fresh.NotesAndInfos);
        }
        else
        {
            foreach (var g in fresh.NotesAndInfos.Where(n => string.IsNullOrEmpty(n.FilePath)))
            {
                if (!notesAndInfos.Any(x => string.IsNullOrEmpty(x.FilePath) && x.RuleId == g.RuleId && x.Message == g.Message))
                    notesAndInfos.Add(g);
            }
        }
    }
}
