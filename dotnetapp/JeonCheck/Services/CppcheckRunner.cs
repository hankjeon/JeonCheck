using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JeonCheck.Models;

namespace JeonCheck.Services;

public record AnalysisProgressInfo(double Percent, string DetailMessage);

public class AnalysisRunResult
{
    public List<AnalysisIssue> Defects { get; set; } = new();
    public List<AnalysisIssue> NotesAndInfos { get; set; } = new();
    public Dictionary<string, FileLineCoverage> CoverageByFile { get; set; } = new();
    public string CommandLineSummary { get; set; } = string.Empty;

    // 코드 뷰어 호버 팝업용 심볼 (정규화 경로 -> 심볼 목록). 정보가 없는 이름은 항목 없음.
    public Dictionary<string, List<Models.SnapshotSymbol>> SymbolsByFile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // .dump 해석 중 수집한 소스별 참조 파일(포함 헤더). 키·값 모두 정규화 경로.
    public Dictionary<string, HashSet<string>> IncludeMapByFile { get; set; } = new();
}

public class CppcheckRunner
{
    private readonly DumpFileParser _dumpParser = new();
    private readonly TypeConversionAnalyzer _typeAnalyzer = new();
    private readonly ClangTidyRunner _clangTidyRunner = new();

    private static readonly Regex CppcheckPercentRegex = new(
        @"(?<done>\d+)\s*/\s*(?<total>\d+)\s+files\s+checked\s+(?<pct>\d+)%\s+done",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CppcheckCheckingRegex = new(
        @"^Checking\s+(?<target>.+?)\.\.\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// MISRA C 2012 Rule 9.1(초기화 전 값 사용)에 해당하는 Cppcheck 결함 ID.
    /// misra.py도 9.1을 직접 검사하지 않고 Cppcheck의 uninitvar 계열 검사에 위임한다(getCppcheckRules).
    /// </summary>
    private static readonly HashSet<string> Misra91SourceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninitvar",
        "uninitdata",
        "uninitStructMember"
    };

    private static readonly HashSet<string> IgnoredCppcheckIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "checkersReport",
        "unusedFunction",
        "unmatchedSuppression",
        "purgedConfiguration"
    };

    /// <summary>
    /// 분석 범위가 제한되었음을 알리는 Cppcheck 진단 ID → 노트 탭 안내 문구
    /// (결함은 아니지만, 사용자가 일부 코드가 검사되지 않았음을 알 수 있도록 표시)
    /// </summary>
    private static readonly Dictionary<string, (string Title, string Guide)> AnalysisLimitNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["toomanyconfigs"] = ("#ifdef 설정 조합 일부만 검사됨",
            "#ifdef 조합이 너무 많아 Cppcheck가 일부 설정만 검사했습니다. 모든 분기를 검사하려면 상단의 [모든 #ifdef 분기 강제 검사(--force)] 옵션을 켜십시오."),
        ["normalCheckLevelMaxBranches"] = ("분기 분석이 제한됨",
            "함수가 복잡하여 일부 분기의 값 흐름 분석이 제한되었습니다."),
        ["normalCheckLevelConditionExpressions"] = ("조건식 분석이 제한됨",
            "복잡한 조건식의 분석이 제한되었습니다."),
        ["checkLevelNormal"] = ("분석 깊이 제한",
            "분석이 너무 복잡하여 일부 검사가 제한되었습니다.")
    };

    public async Task<AnalysisRunResult> RunAnalysisAsync(
        TargetPreset targetPreset,
        string cStandard,
        string customDefines,
        bool forceAllIfdefConfigs,
        bool enableClangTidy,
        IReadOnlyList<SourceTreeNode> targetRoots,
        IReadOnlyList<SourceTreeNode> nonTargetRoots,
        IReadOnlyList<RuleInfo> allRules,
        IProgress<AnalysisProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? incrementalFiles = null)
    {
        var result = new AnalysisRunResult();
        string cppcheckExe = PathResolver.CppcheckExePath;
        if (!File.Exists(cppcheckExe))
        {
            throw new FileNotFoundException($"Cppcheck 실행 파일을 찾을 수 없습니다: {cppcheckExe}");
        }

        // 1. 검사 대상 파일 및 헤더 경로(-I) 수집
        var targetFiles = targetRoots
            .SelectMany(r => r.EnumerateFiles())
            .Select(f => f.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 증분 실행: 전체 트리 대신 바뀐 파일만 대상으로 한다.
        if (incrementalFiles != null)
        {
            targetFiles = incrementalFiles
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => Path.GetFullPath(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (targetFiles.Count == 0)
            return result;

        var targetFilesNormalized = targetFiles
            .Select(AnalysisIssue.NormalizePath)
            .ToHashSet();

        // 실제 Cppcheck 입력 파일: .c/.cpp 파일 우선, 없으면 .h 파일 직접 검사
        // (.c가 있으면 헤더는 #include 경로를 통해 소스 문맥 안에서 분석되며,
        //  어느 소스에도 포함되지 않은 단독 헤더는 아래 2차 분석에서 직접 검사)
        var inputSourceFiles = targetFiles
            .Where(f => f.EndsWith(".c", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool headersExcludedFromFirstPass = inputSourceFiles.Count > 0;
        if (inputSourceFiles.Count == 0)
        {
            inputSourceFiles = new List<string>(targetFiles);
        }

        using var includeResolver = IncludePathResolver.Create(targetRoots, nonTargetRoots);
        var includeDirs = includeResolver.IncludeDirectories;
        var rulesById = allRules.ToDictionary(r => r.Id, r => r, StringComparer.OrdinalIgnoreCase);

        // 2. Cppcheck 공통 명령행 인자 구성 (입력 파일은 분석 단계별로 추가)
        int jobCount = Math.Max(1, Environment.ProcessorCount);
        var baseArgs = new List<string>
        {
            "--enable=all",
            "--inconclusive",
            // 가능한 많은 결함을 검출하기 위해 분기·조건식 분석 제한 없이 전체 분석 (분석 시간은 늘어남)
            "--check-level=exhaustive",
            $"-j{jobCount}",
            "--xml",
            "--dump",
            $"--std={cStandard}"
        };

        if (forceAllIfdefConfigs)
        {
            baseArgs.Add("--force");
        }

        string platformXmlPath = PathResolver.GetPlatformXmlPath(targetPreset.PlatformXml);
        if (File.Exists(platformXmlPath))
        {
            baseArgs.Add($"--platform={platformXmlPath}");
        }

        foreach (var def in targetPreset.Defines)
        {
            if (!string.IsNullOrWhiteSpace(def))
                baseArgs.Add($"-D{def.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(customDefines))
        {
            foreach (var cleaned in ProcessHelper.ParseCustomDefines(customDefines))
            {
                baseArgs.Add($"-D{cleaned}");
            }
        }

        foreach (var incDir in includeDirs)
        {
            baseArgs.Add("-I");
            baseArgs.Add(incDir);
        }

        result.CommandLineSummary = $"cppcheck {string.Join(" ", baseArgs.Concat(inputSourceFiles).Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";

        bool runClangTidy = enableClangTidy && File.Exists(PathResolver.ClangTidyExePath);
        double firstPassEndPct = runClangTidy ? 50.0 : 90.0;
        double secondPassEndPct = runClangTidy ? 55.0 : 98.0;

        progress?.Report(new AnalysisProgressInfo(2.0, $"Cppcheck 정적 분석 준비 중 ({inputSourceFiles.Count}개 파일)..."));

        // 분석 전체(1차/2차 Cppcheck, Clang-Tidy)에서 공유하는 중복 제거 키
        var keys = new DedupKeys();
        var clangInputFiles = new List<string>(inputSourceFiles);

        // Cppcheck는 --dump 파일을 항상 '소스파일.dump'로 소스 옆에 생성하므로(출력 위치 지정 불가),
        // 같은 이름의 기존 파일은 실행 전에 백업 이름으로 옮겨 두고, 분석이 끝나면(정상/취소/오류 모두)
        // 이번에 생성된 파일을 삭제한 뒤 원래 파일을 복원한다.
        using var dumpGuard = new DumpFileGuard();
        {
            // 3~5. 1차 분석: 소스 파일(.c) 기준
            await RunCppcheckPassAsync(cppcheckExe, baseArgs, inputSourceFiles, dumpGuard,
                targetFilesNormalized, rulesById, result, keys,
                2.0, firstPassEndPct, progress, cancellationToken, result.IncludeMapByFile);

            // 5-1. 2차 분석: 어느 소스 파일에도 포함되지 않아 1차 분석 dump에 나타나지 않은 대상 헤더를 직접 검사
            if (headersExcludedFromFirstPass)
            {
                var uncoveredHeaders = targetFiles
                    .Where(IsHeaderFile)
                    .Where(h => !result.CoverageByFile.ContainsKey(AnalysisIssue.NormalizePath(h)))
                    .ToList();

                if (uncoveredHeaders.Count > 0)
                {
                    result.NotesAndInfos.Add(CreateNote(
                        "standaloneHeader",
                        "단독 헤더 직접 분석 안내",
                        $"[분석 범위 안내] 어느 소스 파일(.c)에도 포함되지 않은 검사 대상 헤더 {uncoveredHeaders.Count}개({string.Join(", ", uncoveredHeaders.Select(Path.GetFileName))})를 단독으로 직접 분석했습니다. 소스 문맥(앞서 포함되는 헤더의 타입·매크로 정의) 없이 분석되므로 일부 결과가 실제와 다를 수 있습니다.",
                        string.Empty));

                    await RunCppcheckPassAsync(cppcheckExe, baseArgs, uncoveredHeaders, dumpGuard,
                        targetFilesNormalized, rulesById, result, keys,
                        firstPassEndPct, secondPassEndPct, progress, cancellationToken, result.IncludeMapByFile);
                    clangInputFiles.AddRange(uncoveredHeaders);
                }
            }

            // 6. Clang-Tidy 심층 분석 실행 (CERT-C / bugprone / clang-analyzer 통합 및 누락 헤더 자동 더미 처리)
            if (runClangTidy)
            {
                double clangStartPct = secondPassEndPct;
                double clangSpan = 99.0 - clangStartPct;

                var clangIssues = await _clangTidyRunner.RunAsync(
                    targetPreset,
                    cStandard,
                    customDefines,
                    clangInputFiles,
                    includeDirs,
                    targetFilesNormalized,
                    rulesById,
                    keys.PositionKeys,
                    result.NotesAndInfos,
                    (doneCount, totalCount, fileName) =>
                    {
                        double ratio = totalCount > 0 ? (double)doneCount / totalCount : 1.0;
                        double pct = clangStartPct + (clangSpan * ratio);
                        int displayIdx = Math.Clamp(doneCount, 1, totalCount);
                        progress?.Report(new AnalysisProgressInfo(
                            pct,
                            $"Clang-Tidy 심층 분석 중 ({displayIdx}/{totalCount}): {fileName}"));
                    },
                    cancellationToken);

                result.Defects.AddRange(clangIssues);
            }
        }

        progress?.Report(new AnalysisProgressInfo(100.0, "분석 결과 정리 중..."));

        // 정렬: 파일명 -> 줄 번호 -> 중요도 순
        result.Defects = result.Defects
            .OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ThenBy(d => d.RuleId)
            .ToList();

        return result;
    }

    /// <summary>
    /// Cppcheck 1회 실행 단위: 프로세스 실행 → XML 결과 해석 → 입력 파일별 .dump 해석(MISRA 검사 + 라인 커버리지 + 자체 타입 검사)
    /// </summary>
    private async Task RunCppcheckPassAsync(
        string cppcheckExe,
        IReadOnlyList<string> baseArgs,
        IReadOnlyList<string> inputFiles,
        DumpFileGuard dumpGuard,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        AnalysisRunResult result,
        DedupKeys keys,
        double startPct,
        double endPct,
        IProgress<AnalysisProgressInfo>? progress,
        CancellationToken cancellationToken,
        Dictionary<string, HashSet<string>>? includeMap = null)
    {
        double totalSpan = Math.Max(1.0, endPct - startPct);
        double cppcheckExecEndPct = startPct + (totalSpan * 0.75);
        double cppcheckSpan = cppcheckExecEndPct - startPct;
        double currentCppcheckRatio = 0.0;

        var psi = new ProcessStartInfo
        {
            FileName = cppcheckExe,
            WorkingDirectory = Path.GetDirectoryName(cppcheckExe) ?? PathResolver.RootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in baseArgs)
            psi.ArgumentList.Add(a);
        foreach (var f in inputFiles)
            psi.ArgumentList.Add(f);

        // 기존 '소스.dump' / '소스.ctu-info' 파일을 백업 이름으로 옮겨, 실행 후 존재하는 파일은 모두 이번 실행 결과임을 보장
        foreach (var f in inputFiles)
        {
            dumpGuard.Protect(f + ".dump");
            dumpGuard.Protect(f + ".ctu-info");
        }

        var stderrBuilder = new StringBuilder();
        var stdoutBuilder = new StringBuilder();
        string currentCheckingFile = Path.GetFileName(inputFiles[0]);
        int exitCode;
        using (var proc = new Process { StartInfo = psi })
        {
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderrBuilder.AppendLine(e.Data);
            };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                stdoutBuilder.AppendLine(e.Data);

                var pctMatch = CppcheckPercentRegex.Match(e.Data);
                if (pctMatch.Success && int.TryParse(pctMatch.Groups["pct"].Value, out int pctVal))
                {
                    currentCppcheckRatio = Math.Clamp(pctVal / 100.0, currentCppcheckRatio, 1.0);
                    string doneStr = pctMatch.Groups["done"].Value;
                    string totalStr = pctMatch.Groups["total"].Value;
                    progress?.Report(new AnalysisProgressInfo(
                        startPct + (cppcheckSpan * currentCppcheckRatio),
                        $"Cppcheck 분석 중 ({doneStr}/{totalStr}): {currentCheckingFile}"));
                    return;
                }

                var chkMatch = CppcheckCheckingRegex.Match(e.Data);
                if (chkMatch.Success)
                {
                    string rawTarget = chkMatch.Groups["target"].Value.Trim();
                    string shortTarget = Path.GetFileName(rawTarget);
                    if (!string.IsNullOrEmpty(shortTarget))
                        currentCheckingFile = shortTarget;

                    // 한 파일 내에서 여러 #ifdef 분기를 검사할 때도 게이지가 미세하게 전진하도록 처리
                    currentCppcheckRatio = Math.Min(0.95, currentCppcheckRatio + (0.18 / Math.Max(1, inputFiles.Count)));
                    progress?.Report(new AnalysisProgressInfo(
                        startPct + (cppcheckSpan * currentCppcheckRatio),
                        $"Cppcheck 분석 중: {currentCheckingFile}"));
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await ProcessHelper.WaitForExitOrKillAsync(proc, cancellationToken);
            exitCode = proc.ExitCode;
        }

        // 4. Cppcheck XML 결과 파싱 (결함 vs 누락 헤더 Info/Note 분리)
        string stderrText = stderrBuilder.ToString();
        if (!ParseCppcheckXmlOutput(stderrText, targetFilesNormalized, rulesById, result, keys))
        {
            // XML 결과가 없으면 Cppcheck 자체 실패이므로 '결함 0건'으로 오인되지 않도록 오류로 처리
            string detail = ProcessHelper.SummarizeErrorLines(stdoutBuilder.ToString() + stderrText);
            if (string.IsNullOrEmpty(detail))
            {
                string firstLine = (stdoutBuilder.ToString() + stderrText)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? string.Empty;
                detail = string.IsNullOrEmpty(firstLine) ? string.Empty : "원문: " + firstLine;
            }
            throw new InvalidOperationException(
                $"Cppcheck 실행 결과(XML)를 해석할 수 없습니다 (종료 코드 {exitCode}). {detail}".Trim());
        }

        if (exitCode != 0)
        {
            result.NotesAndInfos.Add(CreateNote(
                "cppcheckExit",
                "Cppcheck 비정상 종료 안내",
                $"[Cppcheck 안내] Cppcheck가 종료 코드 {exitCode}(으)로 끝났습니다. 일부 파일의 분석 결과가 누락되었을 수 있습니다. {ProcessHelper.SummarizeErrorLines(stdoutBuilder.ToString())}".Trim(),
                string.Empty));
        }

        // 5. 생성된 .dump 파일 해석 (MISRA 검사 + 라인별 검사 완료 / 미검사 판별 + 자체 타입 변환 검사)
        //    파일별 .dump XML 파싱·MISRA 검사·타입 변환 검사는 CPU 코어 수만큼 병렬로 수행하고,
        //    결과 병합(중복 제거·커버리지·노트)은 입력 파일 순서대로 직렬 반영하여 결정론적 결과를 보장한다.
        double dumpSpan = endPct - cppcheckExecEndPct;
        var dumpResults = new DumpPassItemResult[inputFiles.Count];
        for (int i = 0; i < inputFiles.Count; i++)
        {
            string dumpFile = inputFiles[i] + ".dump";
            dumpResults[i] = new DumpPassItemResult
            {
                DumpExists = dumpGuard.IsGeneratedByThisRun(dumpFile)
            };
        }

        int completedDumps = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            CancellationToken = cancellationToken
        };

        Parallel.For(0, inputFiles.Count, parallelOptions, i =>
        {
            var item = dumpResults[i];
            string srcFile = inputFiles[i];
            string srcName = Path.GetFileName(srcFile);

            if (item.DumpExists)
            {
                string dumpFile = srcFile + ".dump";

                // MISRA C 2012: Cppcheck 2.22 원본 misra.py를 C#으로 옮긴 검사기
                item.MisraResult = Misra.MisraChecker.CheckDump(dumpFile);

                try
                {
                    var dumpDoc = _dumpParser.Parse(dumpFile);
                    item.DumpDoc = dumpDoc;

                    try
                    {
                        item.Symbols = SymbolTooltipBuilder.Build(dumpDoc);
                    }
                    catch
                    {
                        item.Symbols = new Dictionary<string, List<Models.SnapshotSymbol>>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (includeMap != null)
                    {
                        string srcNorm = AnalysisIssue.NormalizePath(srcFile);
                        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var cfgUnit in dumpDoc.Configurations)
                        {
                            foreach (var tok in cfgUnit.Tokens)
                            {
                                if (!string.IsNullOrEmpty(tok.File))
                                    referenced.Add(AnalysisIssue.NormalizePath(tok.File));
                            }
                        }
                        referenced.Remove(srcNorm);
                        item.ReferencedHeaders = referenced;
                    }

                    // 자체 타입 변환 검사 (implicitNarrowing, implicitSignConversion, explicitNarrowingCast, pointerSizeMismatch)
                    item.TypeIssues = _typeAnalyzer.Analyze(dumpDoc, targetFilesNormalized, rulesById);
                }
                catch (Exception ex)
                {
                    item.DumpParseError = ex;
                }
            }

            int done = Interlocked.Increment(ref completedDumps);
            double dumpRatio = (double)done / inputFiles.Count;
            progress?.Report(new AnalysisProgressInfo(
                cppcheckExecEndPct + (dumpSpan * dumpRatio),
                $"AST 및 MISRA 정밀 분석 중 ({done}/{inputFiles.Count}): {srcName}"));
        });

        for (int i = 0; i < inputFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string srcFile = inputFiles[i];
            string srcName = Path.GetFileName(srcFile);
            var item = dumpResults[i];

            if (!item.DumpExists)
            {
                result.NotesAndInfos.Add(CreateNote(
                    "dumpMissing",
                    "AST 덤프 파일 미생성",
                    $"[분석 범위 안내] '{srcName}'의 AST 덤프(.dump) 파일이 생성되지 않아 라인별 검사 상태 표시와 MISRA·축소 변환 검사를 수행하지 못했습니다. 소스 폴더에 쓰기 권한이 있는지, 구문 오류가 없는지 확인하세요.",
                    srcFile));
                continue;
            }

            if (item.MisraResult != null)
            {
                AddMisraViolations(cppcheckExe, item.MisraResult, srcFile, srcName, targetFilesNormalized, rulesById, result, keys);
            }

            if (item.DumpParseError != null || item.DumpDoc == null)
            {
                result.NotesAndInfos.Add(CreateNote(
                    "dumpParseError",
                    "AST 덤프 파일 해석 실패",
                    $"[분석 범위 안내] '{srcName}'의 AST 덤프 파일을 해석하지 못해 라인별 검사 상태 표시와 축소 변환 검사를 건너뛰었습니다. ({item.DumpParseError?.Message})",
                    srcFile));
                continue;
            }

            if (includeMap != null && item.ReferencedHeaders != null)
            {
                string srcNorm = AnalysisIssue.NormalizePath(srcFile);
                includeMap[srcNorm] = item.ReferencedHeaders;
            }

            // 라인별 커버리지 병합
            foreach (var kvp in item.DumpDoc.CoverageByFile)
            {
                if (!result.CoverageByFile.TryGetValue(kvp.Key, out var existingCov))
                {
                    result.CoverageByFile[kvp.Key] = kvp.Value;
                }
                else
                {
                    existingCov.AnalyzedLines.UnionWith(kvp.Value.AnalyzedLines);
                    existingCov.DirectiveLines.UnionWith(kvp.Value.DirectiveLines);
                    foreach (var sLine in kvp.Value.SkippedLines)
                    {
                        if (!existingCov.AnalyzedLines.Contains(sLine))
                            existingCov.SkippedLines.Add(sLine);
                    }
                    existingCov.SkippedLines.ExceptWith(existingCov.AnalyzedLines);
                }
            }

            // 호버 심볼 병합 (같은 파일·줄·이름은 첫 항목 유지)
            if (item.Symbols != null)
            {
                foreach (var kvp in item.Symbols)
                {
                    if (!result.SymbolsByFile.TryGetValue(kvp.Key, out var existing))
                    {
                        result.SymbolsByFile[kvp.Key] = kvp.Value;
                    }
                    else
                    {
                        var seen = new HashSet<string>(existing.Select(s => $"{s.Line}\n{s.Name}"), StringComparer.Ordinal);
                        foreach (var sym in kvp.Value)
                        {
                            if (seen.Add($"{sym.Line}\n{sym.Name}"))
                                existing.Add(sym);
                        }
                    }
                }
            }

            if (item.TypeIssues != null)
            {
                foreach (var issue in item.TypeIssues)
                {
                    if (keys.TryAdd(issue.RuleId, issue.FilePath, issue.Line, issue.Column))
                        result.Defects.Add(issue);
                }
            }
        }
    }

    private sealed class DumpPassItemResult
    {
        public bool DumpExists { get; init; }
        public Misra.MisraResult? MisraResult { get; set; }
        public DumpDocument? DumpDoc { get; set; }
        public Dictionary<string, List<Models.SnapshotSymbol>>? Symbols { get; set; }
        public Exception? DumpParseError { get; set; }
        public HashSet<string>? ReferencedHeaders { get; set; }
        public List<AnalysisIssue>? TypeIssues { get; set; }
    }

    private static void AddMisraViolations(
        string cppcheckExe,
        Misra.MisraResult misra,
        string srcFile,
        string srcName,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        AnalysisRunResult result,
        DedupKeys keys)
    {
        if (misra.Error != null && misra.Violations.Count == 0)
        {
            // 원본 misra.py가 실패해 위반을 하나도 내지 못한 경우와 같은 안내
            result.NotesAndInfos.Add(CreateNote(
                "misraAddonError",
                "MISRA 검사 실패",
                $"[분석 범위 안내] '{srcName}'의 MISRA 검사가 실패하여 MISRA 검사 결과가 없습니다. ({misra.Error.GetType().Name}: {misra.Error.Message})",
                srcFile));
            return;
        }

        // dump의 상대 경로는 Cppcheck 실행 폴더 기준이다
        string baseDir = Path.GetDirectoryName(cppcheckExe) ?? PathResolver.RootDirectory;
        foreach (var v in misra.Violations)
        {
            if (string.IsNullOrEmpty(v.File))
                continue;
            string file = Path.GetFullPath(v.File, baseDir);
            if (!targetFilesNormalized.Contains(AnalysisIssue.NormalizePath(file)))
                continue;

            string ruleId = v.RuleId;
            if (rulesById.TryGetValue(ruleId, out var rule))
            {
                if (!rule.IsEnabled)
                    continue;
            }
            else
            {
                // misra.py 출력 심각도: 규칙 위반은 style, 설정 누락(misra-config)은 error
                rule = RuleInfo.CreateUnregistered(ruleId, ruleId == "misra-config" ? "Error" : "Style", "MISRA addon");
                rule.Title = $"MISRA C 2012 Rule {ruleId.Replace("misra-c2012-", string.Empty)}";
            }

            if (!keys.TryAdd(ruleId, file, v.Line, v.Column))
                continue;

            string raw = $"misra.py 검출: MISRA C 2012 Rule {ruleId.Replace("misra-c2012-", string.Empty)}";
            result.Defects.Add(new AnalysisIssue
            {
                RuleId = ruleId,
                Category = rule.Category,
                Severity = rule.Severity,
                Obligation = rule.Obligation,
                Title = rule.Title,
                Message = !string.IsNullOrEmpty(rule.Summary) ? $"{rule.Summary} ({raw})" : raw,
                FilePath = file,
                Line = v.Line,
                Column = v.Column,
                IsInfoOrNote = false,
                RuleDetails = rule
            });
        }
    }

    private static bool IsHeaderFile(string path) =>
        path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 결함 중복 제거 키 묶음: 같은 위치(규칙+파일+줄+열) 중복 제거용 키와 노트 중복 키
    /// </summary>
    private sealed class DedupKeys
    {
        public HashSet<string> PositionKeys { get; } = new();
        public HashSet<string> NoteKeys { get; } = new();

        public bool TryAdd(string ruleId, string filePath, int line, int column)
            => PositionKeys.Add(AnalysisIssue.MakePositionKey(ruleId, filePath, line, column));
    }

    /// <summary>
    /// Cppcheck가 소스 옆에 생성하는 '.dump' / '.ctu-info' 파일 보호.
    /// 실행 전 같은 이름의 기존 파일을 백업 이름으로 옮기고, Dispose 시 이번 실행에서 생긴 파일을 삭제한 뒤 원래 파일을 복원한다.
    /// 수정 시각에 의존하지 않으므로 네트워크 드라이브의 시계 차이나 파일 시스템 시각 해상도와 무관하게 동작한다.
    /// </summary>
    private sealed class DumpFileGuard : IDisposable
    {
        private readonly List<(string Path, string? BackupPath)> _entries = new();
        private readonly HashSet<string> _unprotected = new(StringComparer.OrdinalIgnoreCase);

        public void Protect(string path)
        {
            if (_entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) || _unprotected.Contains(path))
                return;

            string? backupPath = null;
            if (File.Exists(path))
            {
                backupPath = $"{path}.jeoncheck-backup-{Guid.NewGuid():N}";
                try
                {
                    File.Move(path, backupPath);
                }
                catch
                {
                    // 읽기 전용 폴더·파일 잠금 등으로 옮기지 못하면 기존 파일을 그대로 두고 이번 결과로 사용하지도, 삭제하지도 않음
                    _unprotected.Add(path);
                    return;
                }
            }
            _entries.Add((path, backupPath));
        }

        /// <summary>이번 실행에서 생성된 파일인지 확인 (기존 파일을 백업하지 못한 경로는 항상 false)</summary>
        public bool IsGeneratedByThisRun(string path) => !_unprotected.Contains(path) && File.Exists(path);

        public void Dispose()
        {
            foreach (var (path, backupPath) in _entries)
            {
                try
                {
                    // 백업 후 존재하는 파일은 모두 이번 실행에서 생성된 것이므로 삭제
                    if (File.Exists(path))
                        File.Delete(path);
                    if (backupPath != null && File.Exists(backupPath))
                        File.Move(backupPath, path);
                }
                catch
                {
                    // 정리/복원 실패 시 백업 파일('*.jeoncheck-backup-*')은 그대로 남아 수동 복구 가능
                }
            }
            _entries.Clear();
        }
    }

    /// <returns>Cppcheck XML 결과를 정상적으로 해석했으면 true, XML이 없거나 손상되었으면 false</returns>
    private static bool ParseCppcheckXmlOutput(
        string xmlText,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        AnalysisRunResult result,
        DedupKeys keys)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
            return false;

        int xmlStart = xmlText.IndexOf("<?xml", StringComparison.Ordinal);
        if (xmlStart < 0)
            return false;
        if (xmlStart > 0)
            xmlText = xmlText.Substring(xmlStart);

        XDocument xdoc;
        try
        {
            xdoc = XDocument.Parse(xmlText);
        }
        catch
        {
            return false;
        }

        var errorsElem = xdoc.Root?.Element("errors");
        if (errorsElem == null)
            return false;


        foreach (var errElem in errorsElem.Elements("error"))
        {
            string id = errElem.Attribute("id")?.Value ?? string.Empty;
            string severity = errElem.Attribute("severity")?.Value ?? "warning";
            string rawMsg = errElem.Attribute("msg")?.Value ?? string.Empty;

            if (string.IsNullOrEmpty(id) || IgnoredCppcheckIds.Contains(id))
                continue;

            var locElem = errElem.Elements("location").FirstOrDefault();
            string file = locElem?.Attribute("file")?.Value ?? string.Empty;
            int line = int.TryParse(locElem?.Attribute("line")?.Value, out int l) ? l : 0;
            int col = int.TryParse(locElem?.Attribute("column")?.Value, out int c) ? c : 0;

            // 0) 분석 범위 제한 안내는 결함이 아닌 Info/Note 탭으로 분리
            if (AnalysisLimitNotes.TryGetValue(id, out var limitNote))
            {
                if (keys.NoteKeys.Add($"{id}|{AnalysisIssue.NormalizePath(file)}|{line}"))
                {
                    result.NotesAndInfos.Add(new AnalysisIssue
                    {
                        RuleId = id,
                        Category = "포함 파일 / 환경 안내",
                        Severity = "Info",
                        Obligation = "참고(Note)",
                        Title = limitNote.Title,
                        Message = $"[분석 범위 안내] {limitNote.Guide} (Cppcheck: {rawMsg})",
                        FilePath = string.IsNullOrEmpty(file) ? string.Empty : Path.GetFullPath(file),
                        Line = line,
                        Column = col,
                        IsInfoOrNote = true
                    });
                }
                continue;
            }

            // 1) 누락된 포함 파일(missingInclude / missingIncludeSystem)은 Info/Note 탭으로 분리
            if (id.Equals("missingInclude", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("missingIncludeSystem", StringComparison.OrdinalIgnoreCase))
            {
                string normNoteFile = AnalysisIssue.NormalizePath(file);
                string noteKey = $"{id}|{normNoteFile}|{line}|{rawMsg}";
                if (keys.NoteKeys.Add(noteKey))
                {
                    string korNoteMsg = id.Equals("missingInclude", StringComparison.OrdinalIgnoreCase)
                        ? $"[참조 헤더 누락 안내] {rawMsg} (해당 헤더 없이 가능한 범위까지 분석을 계속 수행했습니다. 필요 시 비대상/참조 코드에 헤더 폴더를 추가하세요.)"
                        : $"[표준 헤더 누락 안내] {rawMsg} (내장 라이브러리 모델을 활용해 가능한 범위를 분석했습니다. 모델은 실제 헤더의 타입·선언·매크로를 완전히 대체하지 않으므로 결과가 제한될 수 있습니다. 필요 시 참조 코드에 해당 타겟의 헤더 폴더를 추가하세요.)";

                    result.NotesAndInfos.Add(new AnalysisIssue
                    {
                        RuleId = id,
                        Category = "포함 파일 / 환경 안내",
                        Severity = "Info",
                        Obligation = "참고(Note)",
                        Title = id.Equals("missingInclude", StringComparison.OrdinalIgnoreCase) ? "사용자 포함 파일(#include) 미발견" : "시스템 표준 헤더 참조 안내",
                        Message = korNoteMsg,
                        FilePath = string.IsNullOrEmpty(file) ? string.Empty : Path.GetFullPath(file),
                        Line = line,
                        Column = col,
                        IsInfoOrNote = true
                    });
                }
                continue;
            }

            // 2) 검사 대상 파일에 속하지 않은 결함은 제외하되,
            //    비대상(참조) 헤더에서 #error(preprocessorErrorDirective)나 구문 오류(syntaxError/internalAstError)가 발생하면
            //    이를 포함한 검사 대상(.c) 파일 전체가 [미검사]로 빠지므로 원인 파악을 위해 반드시 노트 탭에 안내
            if (string.IsNullOrEmpty(file) || !targetFilesNormalized.Contains(AnalysisIssue.NormalizePath(file)))
            {
                if (id.Equals("preprocessorErrorDirective", StringComparison.OrdinalIgnoreCase) ||
                    id.Equals("syntaxError", StringComparison.OrdinalIgnoreCase) ||
                    id.Equals("internalAstError", StringComparison.OrdinalIgnoreCase))
                {
                    string normErrFile = AnalysisIssue.NormalizePath(file);
                    string noteKey = $"{id}|{normErrFile}|{line}|{rawMsg}";
                    if (keys.NoteKeys.Add(noteKey))
                    {
                        string errTitle = id.Equals("preprocessorErrorDirective", StringComparison.OrdinalIgnoreCase)
                            ? "참조 헤더 전처리기 #error 발생 (분석 중단 원인)"
                            : "참조 헤더 구문 해석 오류 (분석 중단 원인)";

                        string errGuide = id.Equals("preprocessorErrorDirective", StringComparison.OrdinalIgnoreCase)
                            ? $"[전처리기 #error 중단 안내] 참조 헤더 '{Path.GetFileName(file)}' ({line}번째 줄)에서 '{rawMsg}' 오류가 발생하여 이를 포함한 소스 파일이 미검사 처리되었습니다. 타겟 프로세서 설정이 해당 헤더의 아키텍처와 맞는지 확인하거나 상단 [-D 매크로]에 필요한 매크로를 추가하세요."
                            : $"[참조 헤더 구문 오류 안내] 참조 헤더 '{Path.GetFileName(file)}' ({line}번째 줄)에서 구문 오류({rawMsg})가 발생하여 이를 포함한 소스 파일 분석이 중단되었습니다. 컴파일러 전용 비표준 키워드나 매크로를 상단 [-D 매크로]에 정의하세요.";

                        result.NotesAndInfos.Add(new AnalysisIssue
                        {
                            RuleId = id,
                            Category = "포함 파일 / 환경 안내",
                            Severity = "Error",
                            Obligation = "참고(Note)",
                            Title = errTitle,
                            Message = errGuide,
                            FilePath = string.IsNullOrEmpty(file) ? string.Empty : Path.GetFullPath(file),
                            Line = line,
                            Column = col,
                            IsInfoOrNote = true
                        });
                    }
                }
                continue;
            }

            // 2-1) 초기화되지 않은 값 사용은 MISRA 9.1로도 보고 (원래 Cppcheck ID의 ON/OFF와 무관하게 9.1 규칙 설정을 따름)
            if (Misra91SourceIds.Contains(id) &&
                rulesById.TryGetValue("misra-c2012-9.1", out var misra91) && misra91.IsEnabled &&
                keys.TryAdd("misra-c2012-9.1", file, line, col))
            {
                result.Defects.Add(new AnalysisIssue
                {
                    RuleId = misra91.Id,
                    Category = misra91.Category,
                    Severity = misra91.Severity,
                    Obligation = misra91.Obligation,
                    Title = misra91.Title,
                    Message = $"{misra91.Summary} (Cppcheck {id}: {rawMsg})",
                    FilePath = Path.GetFullPath(file),
                    Line = line,
                    Column = col,
                    IsInfoOrNote = false,
                    RuleDetails = misra91
                });
            }

            // 3) 룰셋에 등록된 규칙 확인 및 활성화(IsEnabled) 여부 체크
            if (rulesById.TryGetValue(id, out var ruleInfo))
            {
                if (!ruleInfo.IsEnabled)
                    continue;
            }
            else
            {
                // rules_ko.json에 아직 등록되지 않은 Cppcheck 세부 ID도 한글 카테고리로 감싸서 제공
                ruleInfo = RuleInfo.CreateUnregistered(id, NormalizeSeverity(severity), "Cppcheck");
            }

            if (!keys.TryAdd(id, file, line, col))
                continue;

            string displayMsg = !string.IsNullOrEmpty(ruleInfo.Summary)
                ? $"{ruleInfo.Summary} ({rawMsg})"
                : rawMsg;

            result.Defects.Add(new AnalysisIssue
            {
                RuleId = id,
                Category = ruleInfo.Category,
                Severity = ruleInfo.IsUnregistered ? NormalizeSeverity(severity) : ruleInfo.Severity,
                Obligation = ruleInfo.Obligation,
                Title = ruleInfo.Title,
                Message = displayMsg,
                FilePath = Path.GetFullPath(file),
                Line = line,
                Column = col,
                IsInfoOrNote = false,
                RuleDetails = ruleInfo
            });
        }

        return true;
    }

    private static AnalysisIssue CreateNote(string id, string title, string message, string filePath)
    {
        return new AnalysisIssue
        {
            RuleId = id,
            Category = "포함 파일 / 환경 안내",
            Severity = "Info",
            Obligation = "참고(Note)",
            Title = title,
            Message = message,
            FilePath = filePath,
            IsInfoOrNote = true
        };
    }

    private static List<string> CollectIncludeDirectories(
        IReadOnlyList<SourceTreeNode> targetRoots,
        IReadOnlyList<SourceTreeNode> nonTargetRoots)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectFromNode(SourceTreeNode node)
        {
            if (node.IsDirectory)
            {
                if (Directory.Exists(node.FullPath))
                    dirs.Add(node.FullPath);
                foreach (var child in node.Children)
                    CollectFromNode(child);
            }
            else
            {
                string? dir = Path.GetDirectoryName(node.FullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    dirs.Add(dir);
            }
        }

        foreach (var r in targetRoots)
            CollectFromNode(r);
        foreach (var r in nonTargetRoots)
            CollectFromNode(r);

        return dirs.OrderBy(d => d).ToList();
    }

    private static string NormalizeSeverity(string cppcheckSev)
    {
        return cppcheckSev.ToLowerInvariant() switch
        {
            "error" => "Error",
            "warning" or "performance" or "portability" => "Warning",
            "style" => "Style",
            _ => "Info"
        };
    }
}
