using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class AnalysisRunResult
{
    public List<AnalysisIssue> Defects { get; set; } = new();
    public List<AnalysisIssue> NotesAndInfos { get; set; } = new();
    public Dictionary<string, FileLineCoverage> CoverageByFile { get; set; } = new();
    public string CommandLineSummary { get; set; } = string.Empty;
}

public class CppcheckRunner
{
    private readonly DumpFileParser _dumpParser = new();
    private readonly MisraAndTypeAnalyzer _misraAnalyzer = new();
    private readonly ClangTidyRunner _clangTidyRunner = new();

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
        CancellationToken cancellationToken = default)
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
        var baseArgs = new List<string>
        {
            "--enable=all",
            "--inconclusive",
            // 가능한 많은 결함을 검출하기 위해 분기·조건식 분석 제한 없이 전체 분석 (분석 시간은 늘어남)
            "--check-level=exhaustive",
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
            var splitDefs = customDefines.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in splitDefs)
            {
                string cleaned = d.Trim();
                if (cleaned.StartsWith("-D", StringComparison.Ordinal))
                    cleaned = cleaned.Substring(2);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    baseArgs.Add($"-D{cleaned}");
            }
        }

        foreach (var incDir in includeDirs)
        {
            baseArgs.Add("-I");
            baseArgs.Add(incDir);
        }

        result.CommandLineSummary = $"cppcheck {string.Join(" ", baseArgs.Concat(inputSourceFiles).Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";

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
                targetFilesNormalized, rulesById, result, keys, cancellationToken);

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
                        targetFilesNormalized, rulesById, result, keys, cancellationToken);
                    clangInputFiles.AddRange(uncoveredHeaders);
                }
            }

            // 6. Clang-Tidy 심층 분석 실행 (CERT-C / bugprone / clang-analyzer 통합 및 누락 헤더 자동 더미 처리)
            if (enableClangTidy && File.Exists(PathResolver.ClangTidyExePath))
            {
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
                    cancellationToken);

                result.Defects.AddRange(clangIssues);
            }
        }

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
    /// Cppcheck 1회 실행 단위: 프로세스 실행 → XML 결과 해석 → 입력 파일별 .dump 해석(라인 커버리지 + C# 네이티브 검사)
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
        CancellationToken cancellationToken)
    {
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
                if (e.Data != null)
                    stdoutBuilder.AppendLine(e.Data);
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

        // 5. 생성된 .dump 파일 파싱 (라인별 검사 완료 / 미검사 판별 + C# 네이티브 MISRA / Narrowing 검출)
        foreach (var srcFile in inputFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dumpFile = srcFile + ".dump";
            if (!dumpGuard.IsGeneratedByThisRun(dumpFile))
            {
                result.NotesAndInfos.Add(CreateNote(
                    "dumpMissing",
                    "AST 덤프 파일 미생성",
                    $"[분석 범위 안내] '{Path.GetFileName(srcFile)}'의 AST 덤프(.dump) 파일이 생성되지 않아 라인별 검사 상태 표시와 C# 네이티브 MISRA/축소 변환 검사를 수행하지 못했습니다. 소스 폴더에 쓰기 권한이 있는지, 구문 오류가 없는지 확인하세요.",
                    srcFile));
                continue;
            }

            DumpDocument dumpDoc;
            try
            {
                dumpDoc = _dumpParser.Parse(dumpFile);
            }
            catch (Exception ex)
            {
                result.NotesAndInfos.Add(CreateNote(
                    "dumpParseError",
                    "AST 덤프 파일 해석 실패",
                    $"[분석 범위 안내] '{Path.GetFileName(srcFile)}'의 AST 덤프 파일을 해석하지 못해 라인별 검사 상태 표시와 C# 네이티브 검사를 건너뛰었습니다. ({ex.Message})",
                    srcFile));
                continue;
            }

            // 라인별 커버리지 병합
            foreach (var kvp in dumpDoc.CoverageByFile)
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

            // C# 네이티브 MISRA C 2012 & Narrowing Conversion 검사
            var astIssues = _misraAnalyzer.Analyze(dumpDoc, targetFilesNormalized, rulesById);
            foreach (var issue in astIssues)
            {
                if (keys.TryAdd(issue.RuleId, issue.FilePath, issue.Line, issue.Column))
                    result.Defects.Add(issue);
            }
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

            // 2) 검사 대상 파일에 속하지 않은 결함은 제외
            if (string.IsNullOrEmpty(file) || !targetFilesNormalized.Contains(AnalysisIssue.NormalizePath(file)))
                continue;

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
