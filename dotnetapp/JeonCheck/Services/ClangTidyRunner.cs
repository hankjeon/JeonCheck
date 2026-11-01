using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class ClangTidyRunner
{
    private static readonly Regex IncludeQuoteRegex = new(
        @"^\s*#\s*include\s*[""<]([^"">]+)["" >]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex DiagnosticLineRegex = new(
        @"^(?<file>[A-Za-z]:[^:\r\n]+):(?<line>\d+):(?<col>\d+):\s+(?<sev>warning|error):\s+(?<msg>.+?)\s+\[(?<ids>[^\]]+)\]\s*$",
        RegexOptions.Compiled);

    public async Task<List<AnalysisIssue>> RunAsync(
        TargetPreset targetPreset,
        string cStandard,
        string customDefines,
        IReadOnlyList<string> inputSourceFiles,
        IReadOnlyList<string> includeDirs,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        HashSet<string> existingPositionKeys,
        List<AnalysisIssue> notes,
        Action<int, int, string>? onFileProgress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<AnalysisIssue>();
        string clangTidyExe = PathResolver.ClangTidyExePath;
        if (!File.Exists(clangTidyExe) || inputSourceFiles.Count == 0)
            return issues;

        string stubDir = Path.Combine(Path.GetTempPath(), "jeoncheck_stubs_" + Guid.NewGuid().ToString("N"));
        // 상대 경로(../ 등)로 올라가는 #include도 임시 폴더 안에서 해석되도록 3단계 하위 폴더를 기준점으로 사용
        string stubBaseDir = Path.Combine(stubDir, "d0", "d1", "d2");
        try
        {
            Directory.CreateDirectory(stubBaseDir);
            CreateMissingHeaderStubs(inputSourceFiles, includeDirs, stubDir, stubBaseDir);

            string clangTarget = !string.IsNullOrWhiteSpace(targetPreset.ClangTarget)
                ? targetPreset.ClangTarget
                : "armv7r-none-eabi";
            string builtinInc = PathResolver.ClangTidyIncludeDirectory;
            string workingDir = Path.GetDirectoryName(clangTidyExe) ?? PathResolver.RootDirectory;

            var failedFiles = new List<(string FileName, int ExitCode, string Summary)>();

            for (int i = 0; i < inputSourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string src = inputSourceFiles[i];
                string fileName = Path.GetFileName(src);
                onFileProgress?.Invoke(i, inputSourceFiles.Count, fileName);

                var psi = new ProcessStartInfo
                {
                    FileName = clangTidyExe,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("-checks=-*,bugprone-*,cert-*,clang-analyzer-*");
                psi.ArgumentList.Add("--quiet");
                psi.ArgumentList.Add(src);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("-xc");
                psi.ArgumentList.Add($"-std={cStandard}");
                psi.ArgumentList.Add($"--target={clangTarget}");
                psi.ArgumentList.Add("-nostdinc");

                if (Directory.Exists(builtinInc))
                {
                    psi.ArgumentList.Add("-isystem");
                    psi.ArgumentList.Add(builtinInc);
                }

                foreach (var inc in includeDirs)
                {
                    psi.ArgumentList.Add("-I");
                    psi.ArgumentList.Add(inc);
                }

                // 누락 헤더 자동 더미(Stub) 폴더를 마지막 검색 경로로 추가하여 fatal error 방지
                psi.ArgumentList.Add("-I");
                psi.ArgumentList.Add(stubBaseDir);

                foreach (var def in targetPreset.Defines)
                {
                    if (!string.IsNullOrWhiteSpace(def))
                        psi.ArgumentList.Add($"-D{def.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(customDefines))
                {
                    foreach (var cleaned in ProcessHelper.ParseCustomDefines(customDefines))
                    {
                        psi.ArgumentList.Add($"-D{cleaned}");
                    }
                }

                psi.ArgumentList.Add("-Wno-everything");

                var outputBuilder = new StringBuilder();
                int exitCode;
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                            outputBuilder.AppendLine(e.Data);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                            outputBuilder.AppendLine(e.Data);
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    await ProcessHelper.WaitForExitOrKillAsync(proc, cancellationToken);
                    exitCode = proc.ExitCode;
                }

                string outputText = outputBuilder.ToString();
                ParseClangTidyOutput(
                    outputText,
                    targetFilesNormalized,
                    rulesById,
                    existingPositionKeys,
                    issues);

                if (exitCode != 0)
                {
                    failedFiles.Add((fileName, exitCode, ProcessHelper.SummarizeErrorLines(outputText)));
                }

                onFileProgress?.Invoke(i + 1, inputSourceFiles.Count, fileName);
            }

            // Clang-Tidy 비정상 종료(컴파일 오류 등)는 분석 결과 일부 누락 가능성을 노트로 안내
            if (failedFiles.Count > 0)
            {
                var firstFail = failedFiles[0];
                string fileList = string.Join(", ", failedFiles.Select(f => f.FileName));
                notes.Add(new AnalysisIssue
                {
                    RuleId = "clangTidyExit",
                    Category = "포함 파일 / 환경 안내",
                    Severity = "Info",
                    Obligation = "참고(Note)",
                    Title = "Clang-Tidy 비정상 종료 안내",
                    Message = $"[Clang-Tidy 안내] {failedFiles.Count}개 파일({fileList})에서 Clang-Tidy가 종료 코드 {firstFail.ExitCode}(으)로 끝났습니다. 컴파일 오류가 있는 파일은 Clang-Tidy 결과가 일부 누락되었을 수 있습니다. {firstFail.Summary}".Trim(),
                    IsInfoOrNote = true
                });
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(stubDir))
                    Directory.Delete(stubDir, recursive: true);
            }
            catch
            {
                // 임시 폴더 정리 실패 무시
            }
        }

        return issues;
    }

    private static void CreateMissingHeaderStubs(
        IReadOnlyList<string> inputSourceFiles,
        IReadOnlyList<string> includeDirs,
        string stubRootDir,
        string stubBaseDir)
    {
        var filesToScan = new Queue<string>(inputSourceFiles);
        var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string builtinInc = PathResolver.ClangTidyIncludeDirectory;
        string stubRootPrefix = Path.GetFullPath(stubRootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        while (filesToScan.Count > 0)
        {
            string currentFile = filesToScan.Dequeue();
            if (!File.Exists(currentFile) || !scannedFiles.Add(currentFile))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(currentFile);
            }
            catch
            {
                continue;
            }

            string? currentFileDir = Path.GetDirectoryName(currentFile);
            foreach (Match m in IncludeQuoteRegex.Matches(content))
            {
                string incName = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(incName))
                    continue;

                string? resolved = TryFindHeader(incName, currentFileDir, includeDirs, builtinInc);
                if (resolved != null)
                {
                    filesToScan.Enqueue(resolved);
                    continue;
                }

                // 절대 경로 #include는 더미 생성 대상에서 제외 (사용자 디스크에 파일을 만들지 않음)
                string relPath = incName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relPath))
                    continue;

                string stubFilePath;
                try
                {
                    stubFilePath = Path.GetFullPath(Path.Combine(stubBaseDir, relPath));
                }
                catch
                {
                    continue;
                }

                // 보안 및 안전성: 정규화된 경로가 임시 더미 폴더 밖으로 벗어나면 생성하지 않음
                if (!stubFilePath.StartsWith(stubRootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 헤더가 어디에도 존재하지 않으면 임시 더미 폴더에 빈 헤더 생성
                string? stubParent = Path.GetDirectoryName(stubFilePath);
                if (!string.IsNullOrEmpty(stubParent))
                    Directory.CreateDirectory(stubParent);
                if (!File.Exists(stubFilePath))
                    File.WriteAllText(stubFilePath, "/* Auto-generated stub header for clang-tidy analysis */\n");
            }
        }
    }

    private static string? TryFindHeader(
        string incName,
        string? currentFileDir,
        IReadOnlyList<string> includeDirs,
        string builtinInc)
    {
        if (!string.IsNullOrEmpty(currentFileDir))
        {
            string cand = Path.Combine(currentFileDir, incName);
            if (File.Exists(cand))
                return Path.GetFullPath(cand);
        }

        foreach (var dir in includeDirs)
        {
            string cand = Path.Combine(dir, incName);
            if (File.Exists(cand))
                return Path.GetFullPath(cand);
        }

        if (Directory.Exists(builtinInc))
        {
            string cand = Path.Combine(builtinInc, incName);
            if (File.Exists(cand))
                return Path.GetFullPath(cand);
        }

        return null;
    }

    private static void ParseClangTidyOutput(
        string outputText,
        HashSet<string> targetFilesNormalized,
        Dictionary<string, RuleInfo> rulesById,
        HashSet<string> existingPositionKeys,
        List<AnalysisIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return;

        using var reader = new StringReader(outputText);
        string? lineText;
        while ((lineText = reader.ReadLine()) != null)
        {
            var m = DiagnosticLineRegex.Match(lineText);
            if (!m.Success)
                continue;

            string file = m.Groups["file"].Value.Trim();
            int line = int.TryParse(m.Groups["line"].Value, out int l) ? l : 0;
            int col = int.TryParse(m.Groups["col"].Value, out int c) ? c : 0;
            string rawMsg = m.Groups["msg"].Value.Trim();
            string idsRaw = m.Groups["ids"].Value.Trim();

            if (line <= 0 || !targetFilesNormalized.Contains(AnalysisIssue.NormalizePath(file)))
                continue;

            var checkIds = idsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !x.StartsWith("clang-diagnostic-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (checkIds.Count == 0)
                continue;

            // 축소 변환은 기존 규칙과 ON/OFF를 공유한다. DB에 없을 때도 공통 미등록 경로로 표시한다.
            if (checkIds.Contains("bugprone-narrowing-conversions", StringComparer.OrdinalIgnoreCase))
                checkIds = new List<string> { "implicitNarrowing" };

            // 2) 실수형 루프 카운터 진단은 CERT FLP30-C로 표시 (C# 분석기의 MISRA 14.1 결과와 별개로 모두 표시)
            if (checkIds.Any(id => id.Contains("float-loop-counter", StringComparison.OrdinalIgnoreCase) ||
                                   id.Contains("FloatLoopCounter", StringComparison.OrdinalIgnoreCase) ||
                                   id.Equals("cert-flp30-c", StringComparison.OrdinalIgnoreCase)))
            {
                checkIds = new List<string> { "cert-flp30-c" };
            }

            // 대표 Rule ID 선택: 같은 진단에 별칭이 함께 붙는 경우(예: [bugprone-signed-char-misuse,cert-str34-c])
            // 등록된 ID 중 켜져(ON) 있는 것을 우선 사용하고, 등록된 별칭이 모두 꺼져(OFF) 있으면 보고하지 않음
            var registeredIds = checkIds.Where(id => rulesById.ContainsKey(id)).ToList();
            if (registeredIds.Count > 0 && registeredIds.All(id => !rulesById[id].IsEnabled))
                continue;

            string selectedId = registeredIds.FirstOrDefault(id => rulesById[id].IsEnabled)
                                ?? checkIds.FirstOrDefault(id => id.StartsWith("cert-", StringComparison.OrdinalIgnoreCase))
                                ?? checkIds[0];

            if (rulesById.TryGetValue(selectedId, out var ruleInfo))
            {
                if (!ruleInfo.IsEnabled)
                    continue;
            }
            else
            {
                ruleInfo = RuleInfo.CreateUnregistered(selectedId,
                    m.Groups["sev"].Value == "error" ? "Error" : "Warning", "Clang-Tidy");
            }

            // 같은 위치(줄+열)의 중복만 제거 (여러 소스 파일에 포함된 헤더의 반복 보고 등)
            if (!existingPositionKeys.Add(AnalysisIssue.MakePositionKey(selectedId, file, line, col)))
                continue;

            string displayMsg = !string.IsNullOrEmpty(ruleInfo.Summary) && !string.Equals(ruleInfo.Summary, rawMsg, StringComparison.Ordinal)
                ? $"{ruleInfo.Summary} ({rawMsg})"
                : rawMsg;

            issues.Add(new AnalysisIssue
            {
                RuleId = selectedId,
                Category = ruleInfo.Category,
                Severity = ruleInfo.IsUnregistered ? (m.Groups["sev"].Value == "error" ? "Error" : "Warning") : ruleInfo.Severity,
                Obligation = ruleInfo.Obligation,
                Title = ruleInfo.Title,
                Message = displayMsg,
                FilePath = Path.GetFullPath(file),
                Line = line,
                Column = col,
                RuleDetails = ruleInfo
            });
        }
    }
}
