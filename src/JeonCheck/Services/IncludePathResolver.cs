using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JeonCheck.Models;

namespace JeonCheck.Services;

/// <summary>
/// 사용자가 폴더나 개별 파일을 임의의 위치에서 추가했을 때,
/// 소스 코드 내의 #include "상대경로/헤더.h" 구문과 실제 디스크 파일 경로가 어긋나도
/// 상위 디렉토리 역추적 및 가상 포워딩 헤더 생성을 통해 자동으로 매칭해 주는 해석기
/// </summary>
public sealed class IncludePathResolver : IDisposable
{
    private static readonly Regex IncludeRegex = new(
        @"^\s*#\s*include\s*[<""]([^>""]+)[>""]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly string _virtualRootDir;
    private readonly string _virtualBaseDir;

    public List<string> IncludeDirectories { get; } = new();

    private IncludePathResolver(string virtualRootDir, string virtualBaseDir)
    {
        _virtualRootDir = virtualRootDir;
        _virtualBaseDir = virtualBaseDir;
    }

    public static IncludePathResolver Create(
        IReadOnlyList<SourceTreeNode> targetRoots,
        IReadOnlyList<SourceTreeNode> nonTargetRoots)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"jeoncheck_vinc_{Guid.NewGuid():N}");
        // 상대 경로(../../ 등)로 올라가는 #include도 대응할 수 있도록 3단계 하위 폴더를 기본 기준점으로 사용
        string virtualBase = Path.Combine(tempRoot, "d0", "d1", "d2");

        var resolver = new IncludePathResolver(tempRoot, virtualBase);
        resolver.BuildIncludePaths(targetRoots, nonTargetRoots);
        return resolver;
    }

    private void BuildIncludePaths(
        IReadOnlyList<SourceTreeNode> targetRoots,
        IReadOnlyList<SourceTreeNode> nonTargetRoots)
    {
        var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allFiles = new List<string>();

        void CollectFromNode(SourceTreeNode node)
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(node.FullPath))
                    dirSet.Add(Path.GetFullPath(node.FullPath));
                foreach (var child in node.Children)
                    CollectFromNode(child);
            }
            else
            {
                if (File.Exists(node.FullPath))
                {
                    string fullFile = Path.GetFullPath(node.FullPath);
                    allFiles.Add(fullFile);
                    string? parentDir = Path.GetDirectoryName(fullFile);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        dirSet.Add(parentDir);
                }
            }
        }

        foreach (var r in targetRoots)
            CollectFromNode(r);
        foreach (var r in nonTargetRoots)
            CollectFromNode(r);

        // 파일명(소문자) -> 사용자가 추가한 실제 파일 전체 경로 목록 인덱싱
        var filesByName = allFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        bool needVirtualBase = false;

        // 모든 대상/비대상 파일(.c, .h) 내의 #include 구문 스캔
        foreach (var srcFile in allFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string content;
            try
            {
                content = File.ReadAllText(srcFile);
            }
            catch
            {
                continue;
            }

            string srcDir = Path.GetDirectoryName(srcFile) ?? string.Empty;

            foreach (Match m in IncludeRegex.Matches(content))
            {
                string rawInc = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(rawInc))
                    continue;

                // 1) 이미 현재 파일 폴더나 등록된 -I 폴더에서 바로 찾아지는지 확인
                if (CanResolveDirectly(rawInc, srcDir, dirSet))
                    continue;

                // 2) 사용자가 추가한 파일 목록 중에서 파일명이 일치하는 후보 검색
                string incFileName = Path.GetFileName(rawInc.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(incFileName) || !filesByName.TryGetValue(incFileName, out var candidates))
                    continue;

                string bestMatchFile = SelectBestMatchingFile(rawInc, candidates);

                // 2-A) 만약 #include "hal/spi_reg.h"이고 실제 파일 경로가 "C:\proj\inc\hal\spi_reg.h"라면
                // 상위 폴더인 "C:\proj\inc"를 역추적하여 -I에 추가할 수 있는지 먼저 시도
                string? ancestorDir = TryExtractAncestorIncludeDir(rawInc, bestMatchFile);
                if (!string.IsNullOrEmpty(ancestorDir) && Directory.Exists(ancestorDir))
                {
                    dirSet.Add(ancestorDir);
                    if (CanResolveDirectly(rawInc, srcDir, dirSet))
                        continue;
                }

                // 2-B) 폴더 구조가 아예 다르거나 상대 경로(../ 등)가 어긋난 경우:
                // 가상 Include 폴더 내에 동일한 상대 경로로 포워딩 헤더 생성
                if (CreateVirtualForwardingHeader(rawInc, bestMatchFile))
                {
                    needVirtualBase = true;
                }
            }
        }

        foreach (var d in dirSet.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            IncludeDirectories.Add(d);
        }

        if (needVirtualBase && Directory.Exists(_virtualBaseDir))
        {
            IncludeDirectories.Add(_virtualBaseDir);
        }
    }

    private static bool CanResolveDirectly(string rawInclude, string sourceFileDir, HashSet<string> includeDirs)
    {
        string normalizedRel = rawInclude.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        if (!string.IsNullOrEmpty(sourceFileDir))
        {
            try
            {
                string localCandidate = Path.GetFullPath(Path.Combine(sourceFileDir, normalizedRel));
                if (File.Exists(localCandidate))
                    return true;
            }
            catch
            {
                // 잘못된 경로 문자 무시
            }
        }

        foreach (var dir in includeDirs)
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, normalizedRel));
                if (File.Exists(candidate))
                    return true;
            }
            catch
            {
                // 무시
            }
        }

        return false;
    }

    private static string SelectBestMatchingFile(string rawInclude, List<string> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        var incParts = rawInclude
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .ToArray();

        string best = candidates[0];
        int bestScore = -1;

        foreach (var cand in candidates)
        {
            var candParts = cand.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            int i = incParts.Length - 1;
            int j = candParts.Length - 1;
            while (i >= 0 && j >= 0 && string.Equals(incParts[i], candParts[j], StringComparison.OrdinalIgnoreCase))
            {
                score++;
                i--;
                j--;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cand;
            }
        }

        return best;
    }

    private static string? TryExtractAncestorIncludeDir(string rawInclude, string actualFilePath)
    {
        var incParts = rawInclude.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (incParts.Any(p => p == "." || p == ".."))
            return null;

        string suffix = Path.Combine(incParts);
        if (actualFilePath.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase))
        {
            string ancestor = actualFilePath.Substring(0, actualFilePath.Length - suffix.Length - 1);
            if (!string.IsNullOrWhiteSpace(ancestor) && Directory.Exists(ancestor))
                return Path.GetFullPath(ancestor);
        }

        return null;
    }

    private bool CreateVirtualForwardingHeader(string rawInclude, string actualFilePath)
    {
        try
        {
            Directory.CreateDirectory(_virtualBaseDir);
            string relPath = rawInclude.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string targetStubPath = Path.GetFullPath(Path.Combine(_virtualBaseDir, relPath));

            // 보안 및 안전성: 가상 루트 폴더 밖으로 벗어나는지 확인
            if (!targetStubPath.StartsWith(_virtualRootDir, StringComparison.OrdinalIgnoreCase))
                return false;

            string? stubDir = Path.GetDirectoryName(targetStubPath);
            if (!string.IsNullOrEmpty(stubDir))
                Directory.CreateDirectory(stubDir);

            if (!File.Exists(targetStubPath))
            {
                // 실제 헤더 파일을 절대 경로로 포워딩하여 내부 상대 #include도 원래 폴더 기준으로 동작하게 함
                string escapedActualPath = actualFilePath.Replace('\\', '/');
                File.WriteAllText(
                    targetStubPath,
                    $"/* JeonCheck auto-mapped header for '{rawInclude}' */\n#include \"{escapedActualPath}\"\n",
                    Encoding.UTF8);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_virtualRootDir))
                Directory.Delete(_virtualRootDir, recursive: true);
        }
        catch
        {
            // 임시 폴더 정리 실패 무시
        }
    }
}
