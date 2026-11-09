using System.IO;
using System.Text.RegularExpressions;
using JeonCheck.Models;

namespace JeonCheck.Services;

public sealed record SourceSearchOptions(string Text, bool UseRegex, bool MatchCase, bool WholeWord)
{
    public Regex CreateRegex()
    {
        if (string.IsNullOrEmpty(Text)) throw new ArgumentException("검색어를 입력하세요.");
        if (Text.Contains('\r') || Text.Contains('\n')) throw new ArgumentException("검색어는 한 줄로 입력하세요.");
        string pattern = UseRegex ? Text : Regex.Escape(Text);
        if (WholeWord)
            pattern = $@"(?<![{CodeTextSelection.WordCharacters}])(?:{pattern})(?![{CodeTextSelection.WordCharacters}])";
        return new Regex(pattern, RegexOptions.CultureInvariant |
            (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(250));
    }
}

public sealed record SourceSearchHit(string FilePath, int Line, int Column, int Length, string LineText, string EncodingName);
public sealed record SourceSearchFile(string FilePath, IReadOnlyList<SourceSearchHit> Hits)
{
    public string Header => $"{FilePath} ({Hits.Count}건)";
}
public sealed record SourceSearchReport(IReadOnlyList<SourceSearchFile> Files, IReadOnlyList<string> Warnings);

public static class SourceSearch
{
    // 디스크를 다시 재귀 탐색하지 않고 현재 트리에 남아 있는 파일만 수집한다.
    public static string[] CollectPaths(IEnumerable<SourceTreeNode> targets, IEnumerable<SourceTreeNode> references,
        int scope, string currentFile)
    {
        IEnumerable<string> paths = scope switch
        {
            1 => targets.SelectMany(n => n.EnumerateFiles()).Select(n => n.FullPath),
            2 => references.SelectMany(n => n.EnumerateFiles()).Select(n => n.FullPath),
            3 => string.IsNullOrEmpty(currentFile) ? Array.Empty<string>() : new[] { currentFile },
            _ => targets.Concat(references).SelectMany(n => n.EnumerateFiles()).Select(n => n.FullPath)
        };
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static SourceSearchReport Search(IEnumerable<string> paths, Regex regex, CancellationToken token,
        string currentFile = "", string? currentEncoding = null)
    {
        var files = new List<SourceSearchFile>();
        var warnings = new List<string>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var source = SourceFileEncoding.ReadAllLines(path,
                    string.Equals(path, currentFile, StringComparison.OrdinalIgnoreCase) ? currentEncoding : null);
                var hits = new List<SourceSearchHit>();
                for (int i = 0; i < source.Lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (Match match in regex.Matches(source.Lines[i]))
                    {
                        token.ThrowIfCancellationRequested();
                        // 위치만 일치하는 ^, $ 등은 선택·강조할 텍스트가 없으므로 제외한다.
                        if (match.Length > 0)
                            hits.Add(new(path, i + 1, match.Index + 1, match.Length, source.Lines[i], source.EncodingName));
                    }
                }
                if (hits.Count > 0) files.Add(new(path, hits));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add($"읽기 실패: {path} — {ex.Message}");
            }
        }
        return new(files, warnings);
    }
}
