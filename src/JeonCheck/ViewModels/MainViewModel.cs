using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using JeonCheck.Models;
using JeonCheck.Services;

namespace JeonCheck.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly DataRepository _repository = new();
    private readonly CppcheckRunner _runner = new();

    private TargetPreset? _selectedTargetPreset;
    private string _selectedCStandard = "c99";
    private string _customDefines = string.Empty;
    private bool _forceAllIfdefConfigs;
    private bool _enableClangTidy = true;
    private bool _isAnalyzing;
    private string _statusMessage = "준비 완료";
    private string _currentProjectFilePath = string.Empty;
    private string _currentCodeFilePath = string.Empty;
    private string _codeStatsSummary = "파일을 선택하면 라인별 검사 상태가 표시됩니다.";
    private AnalysisIssue? _selectedIssue;
    private string _defectSearchText = string.Empty;
    private string _selectedDefectCategoryFilter = "전체";

    private List<AnalysisIssue> _allRawDefects = new();
    private Dictionary<string, FileLineCoverage> _coverageByFile = new();
    private readonly HashSet<string> _excludedTargetSubpaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedNonTargetSubpaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCts;

    public ObservableCollection<TargetPreset> TargetPresets { get; } = new();
    public ObservableCollection<string> CStandards { get; } = new() { "c89", "c99", "c11", "c17" };
    public ObservableCollection<RuleInfo> AllRules { get; } = new();

    public ObservableCollection<SourceTreeNode> TargetRoots { get; } = new();
    public ObservableCollection<SourceTreeNode> NonTargetRoots { get; } = new();

    public ObservableCollection<CodeLineViewModel> CurrentFileLines { get; } = new();
    public ObservableCollection<AnalysisIssue> FilteredDefects { get; } = new();
    public ObservableCollection<RuleIssueGroup> RuleIssueGroups { get; } = new();
    public ObservableCollection<AnalysisIssue> NotesAndInfos { get; } = new();
    public ObservableCollection<SuppressedIssueEntry> SuppressedIssues { get; } = new();
    public ObservableCollection<string> DefectCategoryFilters { get; } = new()
    {
        "전체", "타입 및 형변환", "MISRA C 2012", "Clang-Tidy / CERT-C", "메모리 및 포인터", "산술 및 연산", "제어 흐름 및 품질", "구문 및 환경"
    };

    public TargetPreset? SelectedTargetPreset
    {
        get => _selectedTargetPreset;
        set { if (_selectedTargetPreset != value) { _selectedTargetPreset = value; OnPropertyChanged(); } }
    }

    public string SelectedCStandard
    {
        get => _selectedCStandard;
        set { if (_selectedCStandard != value) { _selectedCStandard = value; OnPropertyChanged(); } }
    }

    public string CustomDefines
    {
        get => _customDefines;
        set { if (_customDefines != value) { _customDefines = value; OnPropertyChanged(); } }
    }

    public bool ForceAllIfdefConfigs
    {
        get => _forceAllIfdefConfigs;
        set { if (_forceAllIfdefConfigs != value) { _forceAllIfdefConfigs = value; OnPropertyChanged(); } }
    }

    public bool EnableClangTidy
    {
        get => _enableClangTidy;
        set { if (_enableClangTidy != value) { _enableClangTidy = value; OnPropertyChanged(); } }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set { if (_isAnalyzing != value) { _isAnalyzing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAnalyze)); } }
    }

    public bool CanAnalyze => !_isAnalyzing;

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public string CurrentProjectTitle => string.IsNullOrEmpty(_currentProjectFilePath)
        ? "전검사 (JeonCheck) - 임베디드 C 정적 분석 도우미 (미저장 프로젝트)"
        : $"전검사 (JeonCheck) - {Path.GetFileName(_currentProjectFilePath)}";

    public string CurrentCodeFilePath
    {
        get => _currentCodeFilePath;
        set
        {
            if (_currentCodeFilePath != value)
            {
                _currentCodeFilePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentCodeFileName));
            }
        }
    }

    public string CurrentCodeFileName => string.IsNullOrEmpty(_currentCodeFilePath)
        ? "선택된 소스 파일 없음"
        : $"{Path.GetFileName(_currentCodeFilePath)}  ({ _currentCodeFilePath })";

    public string CodeStatsSummary
    {
        get => _codeStatsSummary;
        set { if (_codeStatsSummary != value) { _codeStatsSummary = value; OnPropertyChanged(); } }
    }

    public AnalysisIssue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (_selectedIssue != value)
            {
                _selectedIssue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedIssue));
                OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
                OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
            }
        }
    }

    public bool HasSelectedIssue => _selectedIssue != null;

    public bool IsSelectedIssueSuppressed =>
        _selectedIssue != null && SuppressedIssues.Any(s => s.SuppressionKey == _selectedIssue.SuppressionKey);

    public string SelectedIssueToggleButtonText =>
        IsSelectedIssueSuppressed ? "↩ 이 줄 예외 해제 (복원)" : "🔇 이 줄만 예외 처리";

    public string DefectSearchText
    {
        get => _defectSearchText;
        set
        {
            if (_defectSearchText != value)
            {
                _defectSearchText = value;
                OnPropertyChanged();
                RefreshFilteredDefects();
            }
        }
    }

    public string SelectedDefectCategoryFilter
    {
        get => _selectedDefectCategoryFilter;
        set
        {
            if (_selectedDefectCategoryFilter != value)
            {
                _selectedDefectCategoryFilter = value;
                OnPropertyChanged();
                RefreshFilteredDefects();
            }
        }
    }

    public string RulesetButtonText => $"⚙ 검사 규칙 설정 ({AllRules.Count(r => r.IsEnabled)}/{AllRules.Count})";
    public string DefectsTabHeader => $"🚨 결함 및 규칙 위반 ({FilteredDefects.Count})";
    public string RuleSummaryTabHeader => $"📊 규칙별 요약 ({RuleIssueGroups.Count}개)";
    public string RuleSummaryStatusText => $"{RuleIssueGroups.Count}개 규칙 · {FilteredDefects.Count}건 (현재 검색·분류·예외 처리 기준)";
    public string NotesTabHeader => $"ℹ️ 노트 / 누락 파일 안내 ({NotesAndInfos.Count})";
    public string SuppressedTabHeader => $"🔇 예외(무시) 항목 ({SuppressedIssues.Count})";
    public string TargetFilesSummary => "검사대상 코드";
    public string NonTargetFilesSummary => "참조코드(비대상)";

    public MainViewModel()
    {
        InitializeData();
    }

    private void InitializeData()
    {
        var loadErrors = new List<string>();

        List<TargetPreset> presets;
        try
        {
            presets = _repository.LoadTargetPresets();
        }
        catch (Exception ex)
        {
            presets = new List<TargetPreset>();
            loadErrors.Add($"타겟 프리셋(targets.json) 읽기 실패: {ex.Message}");
        }
        foreach (var p in presets)
            TargetPresets.Add(p);

        SelectedTargetPreset = TargetPresets.FirstOrDefault(p => p.Id == "ti-c6678-eabi") ?? TargetPresets.FirstOrDefault();

        List<RuleInfo> rules;
        try
        {
            rules = _repository.LoadRules();
        }
        catch (Exception ex)
        {
            rules = new List<RuleInfo>();
            loadErrors.Add($"규칙 DB(rules_ko.json) 읽기 실패: {ex.Message}");
        }
        foreach (var r in rules)
        {
            AddRule(r);
        }

        // 초기 샘플 코드가 존재하면 바로 테스트할 수 있도록 로드
        if (Directory.Exists(PathResolver.SamplesDirectory))
        {
            LoadSampleWorkspace();
        }

        if (loadErrors.Count > 0)
        {
            StatusMessage = "데이터 파일 오류: " + string.Join(" / ", loadErrors);
        }
    }

    public void LoadSampleWorkspace()
    {
        ClearWorkspace();
        string sampleSrc = Path.Combine(PathResolver.SamplesDirectory, "src");
        string sampleInc = Path.Combine(PathResolver.SamplesDirectory, "include");
        string sampleDrv = Path.Combine(PathResolver.SamplesDirectory, "drivers");

        if (Directory.Exists(sampleSrc))
            AddTargetPath(sampleSrc);
        if (Directory.Exists(sampleInc))
            AddNonTargetPath(sampleInc);
        if (Directory.Exists(sampleDrv))
            AddNonTargetPath(sampleDrv);

        var firstFile = TargetRoots.SelectMany(r => r.EnumerateFiles()).FirstOrDefault();
        if (firstFile != null)
        {
            LoadFileIntoCodeViewer(firstFile.FullPath);
        }
        StatusMessage = "샘플 프로젝트가 로드되었습니다. 상단 [▶ 정적 분석 시작]을 눌러 바로 확인해 보세요.";
    }

    public void ClearWorkspace()
    {
        // 진행 중인 분석이 있으면 취소 (이전 작업 공간의 결과가 새 작업 공간에 반영되지 않도록)
        CancelAnalysis();

        _currentProjectFilePath = string.Empty;
        _excludedTargetSubpaths.Clear();
        _excludedNonTargetSubpaths.Clear();
        TargetRoots.Clear();
        NonTargetRoots.Clear();
        _allRawDefects.Clear();
        _coverageByFile.Clear();
        FilteredDefects.Clear();
        RuleIssueGroups.Clear();
        NotesAndInfos.Clear();
        SuppressedIssues.Clear();
        CurrentFileLines.Clear();
        CurrentCodeFilePath = string.Empty;
        SelectedIssue = null;
        CodeStatsSummary = "파일을 선택하면 라인별 검사 상태가 표시됩니다.";
        NotifyCountsChanged();
        OnPropertyChanged(nameof(CurrentProjectTitle));
        StatusMessage = "새 프로젝트가 준비되었습니다.";
    }

    public void AddTargetPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        _excludedTargetSubpaths.Remove(fullPath);

        // 만약 상위 폴더가 이미 TargetRoots에 존재한다면 상위 트리에 병합/재구성
        var parentRoot = TargetRoots.FirstOrDefault(r =>
            r.IsDirectory && fullPath.StartsWith(r.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (parentRoot != null)
        {
            int idx = TargetRoots.IndexOf(parentRoot);
            TargetRoots[idx] = SourceTreeNode.CreateFromPath(parentRoot.FullPath, isTarget: true, _excludedTargetSubpaths);
            UpdateTreeDefectBadges();
            NotifyCountsChanged();
            return;
        }

        if (TargetRoots.Any(r => string.Equals(r.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var node = SourceTreeNode.CreateFromPath(fullPath, isTarget: true, _excludedTargetSubpaths);
        if (node.IsDirectory && node.Children.Count == 0)
            return;

        TargetRoots.Add(node);
        NotifyCountsChanged();
    }

    public void AddNonTargetPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        _excludedNonTargetSubpaths.Remove(fullPath);
        if (NonTargetRoots.Any(r => string.Equals(r.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var node = SourceTreeNode.CreateFromPath(fullPath, isTarget: false, _excludedNonTargetSubpaths);
        if (node.IsDirectory && node.Children.Count == 0)
            return;

        NonTargetRoots.Add(node);
        NotifyCountsChanged();
    }

    public void RemoveNode(SourceTreeNode node)
    {
        bool wasTarget = node.IsTarget;

        if (node.Parent != null)
        {
            var parent = node.Parent;
            parent.Children.Remove(node);
            // 상위 폴더가 다시 스캔(프로젝트 불러오기 등)되어도 제거한 항목이 되살아나지 않도록 제외 목록에 기록
            if (wasTarget)
                _excludedTargetSubpaths.Add(node.FullPath);
            else
                _excludedNonTargetSubpaths.Add(node.FullPath);

            // 하위 항목이 모두 제거되어 빈 폴더가 된 상위 노드는 연쇄 정리
            while (parent != null && parent.Children.Count == 0)
            {
                var upper = parent.Parent;
                if (upper != null)
                {
                    upper.Children.Remove(parent);
                }
                else
                {
                    TargetRoots.Remove(parent);
                    NonTargetRoots.Remove(parent);
                }
                parent = upper;
            }
        }
        else
        {
            TargetRoots.Remove(node);
            NonTargetRoots.Remove(node);
        }

        if (wasTarget)
        {
            var remainingTargetFiles = TargetRoots
                .SelectMany(r => r.EnumerateFiles())
                .Select(f => AnalysisIssue.NormalizePath(f.FullPath))
                .ToHashSet();

            _allRawDefects.RemoveAll(d => !remainingTargetFiles.Contains(AnalysisIssue.NormalizePath(d.FilePath)));
            RefreshFilteredDefects();
            UpdateTreeDefectBadges();
        }

        NotifyCountsChanged();
        StatusMessage = $"'{node.Name}' 항목(및 하위 항목)을 목록에서 제거했습니다.";
    }

    public void MoveNodeToNonTarget(SourceTreeNode node)
    {
        if (!node.IsTarget)
            return;

        RemoveNode(node);
        AddNonTargetPath(node.FullPath);

        // 현재 열려 있던 결함 중 비대상으로 이동된 파일의 결함 제거
        var targetSet = TargetRoots.SelectMany(r => r.EnumerateFiles()).Select(f => AnalysisIssue.NormalizePath(f.FullPath)).ToHashSet();
        _allRawDefects.RemoveAll(d => !targetSet.Contains(AnalysisIssue.NormalizePath(d.FilePath)));
        RefreshFilteredDefects();
        UpdateTreeDefectBadges();
        StatusMessage = $"'{node.Name}' 항목을 비대상/참조 코드로 이동했습니다.";
    }

    public void MoveNodeToTarget(SourceTreeNode node)
    {
        if (node.IsTarget)
            return;

        RemoveNode(node);
        _excludedTargetSubpaths.Remove(node.FullPath);

        // 만약 상위 폴더가 이미 TargetRoots에 있다면 그 트리를 재구성하고, 없으면 루트로 추가
        var parentRoot = TargetRoots.FirstOrDefault(r =>
            r.IsDirectory && node.FullPath.StartsWith(r.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (parentRoot != null)
        {
            int idx = TargetRoots.IndexOf(parentRoot);
            TargetRoots[idx] = SourceTreeNode.CreateFromPath(parentRoot.FullPath, isTarget: true, _excludedTargetSubpaths);
        }
        else
        {
            AddTargetPath(node.FullPath);
        }

        NotifyCountsChanged();
        StatusMessage = $"'{node.Name}' 항목을 검사 대상 코드로 이동했습니다.";
    }

    public async Task RunAnalysisAsync()
    {
        if (IsAnalyzing)
            return;

        if (SelectedTargetPreset == null)
        {
            StatusMessage = "타겟 프리셋을 먼저 선택해 주세요.";
            return;
        }

        int targetFileCount = CountFiles(TargetRoots);
        if (targetFileCount == 0)
        {
            StatusMessage = "검사 대상 코드에 추가된 소스 파일이 없습니다. 좌측 상단에 폴더나 파일을 추가해 주세요.";
            return;
        }

        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        try
        {
            IsAnalyzing = true;
            StatusMessage = $"[{SelectedTargetPreset.Name}] 프리셋으로 {targetFileCount}개 파일 정적 분석 및 AST 검사 중... (취소하려면 [■ 분석 취소])";

            // 분석 중 UI에서 트리/설정을 바꿔도 영향이 없도록 입력값 스냅샷을 만든 뒤 백그라운드 스레드에서 실행
            var preset = SelectedTargetPreset;
            string cStandard = SelectedCStandard;
            string customDefines = CustomDefines;
            bool forceAllIfdef = ForceAllIfdefConfigs;
            bool enableClangTidy = EnableClangTidy;
            var targetSnapshot = TargetRoots.Select(r => r.CloneTree()).ToList();
            var nonTargetSnapshot = NonTargetRoots.Select(r => r.CloneTree()).ToList();
            var rulesSnapshot = AllRules.ToList();
            var token = cts.Token;

            var runResult = await Task.Run(() => _runner.RunAnalysisAsync(
                preset,
                cStandard,
                customDefines,
                forceAllIfdef,
                enableClangTidy,
                targetSnapshot,
                nonTargetSnapshot,
                rulesSnapshot,
                token), token);

            // 분석 도중 작업 공간이 바뀌었으면(새 프로젝트/불러오기) 이전 결과는 버림
            if (!ReferenceEquals(_analysisCts, cts))
                return;

            _allRawDefects = runResult.Defects;
            _coverageByFile = runResult.CoverageByFile;

            NotesAndInfos.Clear();
            foreach (var n in runResult.NotesAndInfos)
                NotesAndInfos.Add(n);

            RefreshFilteredDefects();
            UpdateTreeDefectBadges();

            // 현재 열려 있는 파일이 있으면 라인별 상태(검사됨/미검사/결함) 즉시 갱신, 없으면 첫 번째 파일 열기
            if (!string.IsNullOrEmpty(CurrentCodeFilePath) && File.Exists(CurrentCodeFilePath))
            {
                LoadFileIntoCodeViewer(CurrentCodeFilePath);
            }
            else
            {
                var firstTargetFile = TargetRoots.SelectMany(r => r.EnumerateFiles()).FirstOrDefault();
                if (firstTargetFile != null)
                    LoadFileIntoCodeViewer(firstTargetFile.FullPath);
            }

            SelectedIssue = FilteredDefects.FirstOrDefault();

            StatusMessage = $"분석 완료: 결함 {FilteredDefects.Count}건 검출 (참고 노트 {NotesAndInfos.Count}건, 예외 무시 {SuppressedIssues.Count}건)";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_analysisCts, cts))
                StatusMessage = "분석이 취소되었습니다. (실행 중이던 분석 도구 프로세스는 종료되었습니다)";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_analysisCts, cts))
                StatusMessage = $"분석 중 오류 발생: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
                _analysisCts = null;
            cts.Dispose();
            IsAnalyzing = false;
            NotifyCountsChanged();
        }
    }

    /// <summary>
    /// 진행 중인 분석을 취소 (Cppcheck / Clang-Tidy 프로세스는 강제 종료되고 임시 dump 파일은 정리됨)
    /// </summary>
    public void CancelAnalysis()
    {
        var cts = _analysisCts;
        if (cts == null)
            return;

        _analysisCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이미 끝난 분석
        }
        StatusMessage = "분석을 취소하고 있습니다...";
    }

    public void LoadFileIntoCodeViewer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        CurrentCodeFilePath = Path.GetFullPath(filePath);
        CurrentFileLines.Clear();

        string[] rawLines = File.ReadAllLines(CurrentCodeFilePath);
        string normKey = AnalysisIssue.NormalizePath(CurrentCodeFilePath);

        _coverageByFile.TryGetValue(normKey, out var coverage);

        var fileDefectsByLine = FilteredDefects
            .Where(d => AnalysisIssue.NormalizePath(d.FilePath) == normKey)
            .GroupBy(d => d.Line)
            .ToDictionary(g => g.Key, g => g.ToList());

        var suppressedByLine = SuppressedIssues
            .Where(s => AnalysisIssue.NormalizePath(s.FilePath) == normKey)
            .GroupBy(s => s.Line)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allRawByLine = _allRawDefects
            .Where(d => AnalysisIssue.NormalizePath(d.FilePath) == normKey)
            .GroupBy(d => d.Line)
            .ToDictionary(g => g.Key, g => g.ToList());

        int analyzedCount = 0;
        int skippedCount = 0;
        int defectLineCount = 0;
        int suppressedLineCount = 0;

        for (int i = 0; i < rawLines.Length; i++)
        {
            int lineNum = i + 1;
            string text = rawLines[i];
            var lineVm = new CodeLineViewModel
            {
                LineNumber = lineNum,
                CodeText = text
            };

            if (fileDefectsByLine.TryGetValue(lineNum, out var lineIssues) && lineIssues.Count > 0)
            {
                defectLineCount++;
                analyzedCount++;
                lineVm.Issues = lineIssues;
                bool hasError = lineIssues.Any(x => x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
                lineVm.Status = hasError ? CodeLineStatus.Error : CodeLineStatus.Warning;

                var primary = lineIssues[0];
                string extra = lineIssues.Count > 1 ? $" +{lineIssues.Count - 1}" : string.Empty;
                lineVm.InlineAnnotation = $"[{primary.RuleId}{extra}]";
                lineVm.TooltipText = string.Join("\n\n", lineIssues.Select(x => $"[{x.RuleId} - {x.SeverityDisplay}]\n{x.Message}"));
            }
            else if (suppressedByLine.TryGetValue(lineNum, out var supEntries) && supEntries.Count > 0)
            {
                suppressedLineCount++;
                analyzedCount++;
                lineVm.Status = CodeLineStatus.Suppressed;
                lineVm.SuppressedEntries = supEntries;
                if (allRawByLine.TryGetValue(lineNum, out var rawIssues))
                {
                    lineVm.Issues = rawIssues;
                }

                var primarySup = supEntries[0];
                string extra = supEntries.Count > 1 ? $" +{supEntries.Count - 1}" : string.Empty;
                lineVm.InlineAnnotation = $"[예외: {primarySup.RuleId}{extra}]";
                lineVm.TooltipText = $"[사용자에 의해 이 줄의 결함이 예외(무시) 처리됨]\n규칙: {primarySup.RuleId}\n내용: {primarySup.Message}\n(우측의 [↩ 다시 검사(복원)] 버튼을 누르면 다시 복원됩니다.)";
            }
            else if (coverage != null)
            {
                if (coverage.AnalyzedLines.Contains(lineNum))
                {
                    analyzedCount++;
                    lineVm.Status = CodeLineStatus.Analyzed;
                    lineVm.TooltipText = "검사 완료 줄 (전처리 및 AST 정적 분석 수행됨)";
                }
                else if (coverage.SkippedLines.Contains(lineNum))
                {
                    skippedCount++;
                    lineVm.Status = CodeLineStatus.Skipped;
                    lineVm.InlineAnnotation = "[미검사]";
                    lineVm.TooltipText = "전처리기 조건(#ifdef/#if) 불충족으로 이번 분석에서 제외된 코드 줄입니다.";
                }
            }

            CurrentFileLines.Add(lineVm);
        }

        if (coverage != null)
        {
            int totalCodeLines = analyzedCount + skippedCount;
            double rate = totalCodeLines > 0 ? (analyzedCount * 100.0 / totalCodeLines) : 100.0;
            CodeStatsSummary = $"총 {rawLines.Length}줄  |  🟢 검사 완료: {analyzedCount}줄 ({rate:F0}%)  |  ⚪ 미검사: {skippedCount}줄  |  🔴 결함: {defectLineCount}줄  |  🔇 예외 제외: {suppressedLineCount}줄";
        }
        else
        {
            CodeStatsSummary = $"총 {rawLines.Length}줄  |  (아직 정적 분석이 실행되지 않았거나 비대상 파일입니다)";
        }
    }

    public void RefreshFilteredDefects()
    {
        // 엔진이 보고한 미등록 ID도 기본 ON으로 목록에 추가해 필터에서 유실되지 않게 한다.
        var knownRules = AllRules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var issue in _allRawDefects)
        {
            if (!knownRules.TryGetValue(issue.RuleId, out var rule))
            {
                rule = issue.RuleDetails ?? RuleInfo.CreateUnregistered(issue.RuleId, issue.Severity);
                AddRule(rule);
                knownRules.Add(rule.Id, rule);
            }
            issue.RuleDetails = rule;
        }

        var suppressedKeys = SuppressedIssues.Select(s => s.SuppressionKey).ToHashSet();
        var enabledRuleIds = AllRules.Where(r => r.IsEnabled).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        FilteredDefects.Clear();

        foreach (var d in _allRawDefects)
        {
            if (!enabledRuleIds.Contains(d.RuleId))
                continue;

            if (suppressedKeys.Contains(d.SuppressionKey))
                continue;

            if (!string.IsNullOrEmpty(SelectedDefectCategoryFilter) && SelectedDefectCategoryFilter != "전체")
            {
                if (!string.Equals(d.Category, SelectedDefectCategoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!string.IsNullOrWhiteSpace(DefectSearchText))
            {
                string q = DefectSearchText.Trim();
                bool match = d.RuleId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             d.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             d.Message.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             d.FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!match)
                    continue;
            }

            FilteredDefects.Add(d);
        }

        var expandedRules = RuleIssueGroups.ToDictionary(g => g.RuleId, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);
        RuleIssueGroups.Clear();
        foreach (var group in RuleSummaryBuilder.Build(FilteredDefects))
        {
            if (expandedRules.TryGetValue(group.RuleId, out bool expanded))
                group.IsExpanded = expanded;
            RuleIssueGroups.Add(group);
        }

        UpdateTreeDefectBadges();
        if (!string.IsNullOrEmpty(CurrentCodeFilePath) && File.Exists(CurrentCodeFilePath))
        {
            LoadFileIntoCodeViewer(CurrentCodeFilePath);
        }
        NotifyCountsChanged();
    }

    public void ToggleSelectedIssueSuppression()
    {
        if (SelectedIssue == null)
            return;

        var existing = SuppressedIssues.FirstOrDefault(s => s.SuppressionKey == SelectedIssue.SuppressionKey);
        if (existing != null)
        {
            RestoreSuppressedIssue(existing);
        }
        else
        {
            SuppressSingleIssue(SelectedIssue);
        }

        OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
        OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
    }

    public void SuppressCodeLine(CodeLineViewModel lineVm)
    {
        if (lineVm.Issues.Count == 0)
            return;

        foreach (var issue in lineVm.Issues.ToList())
        {
            if (!SuppressedIssues.Any(s => s.SuppressionKey == issue.SuppressionKey))
            {
                SuppressedIssues.Add(new SuppressedIssueEntry
                {
                    RuleId = issue.RuleId,
                    FilePath = issue.FilePath,
                    Line = issue.Line,
                    Message = issue.Message
                });
            }
        }

        RefreshFilteredDefects();
        OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
        OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
        StatusMessage = $"{lineVm.LineNumber}번째 줄의 결함이 예외(무시) 처리되었습니다. 줄 우측의 [↩ 예외 해제] 버튼으로 언제든 복원할 수 있습니다.";
    }

    public void RestoreCodeLine(CodeLineViewModel lineVm)
    {
        if (lineVm.SuppressedEntries.Count == 0)
            return;

        foreach (var entry in lineVm.SuppressedEntries.ToList())
        {
            SuppressedIssues.Remove(entry);
        }

        RefreshFilteredDefects();
        OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
        OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
        StatusMessage = $"{lineVm.LineNumber}번째 줄의 예외 처리가 해제되어 다시 결함 검사 대상으로 복원되었습니다.";
    }

    public void SuppressSingleIssue(AnalysisIssue issue)
    {
        if (SuppressedIssues.Any(s => s.SuppressionKey == issue.SuppressionKey))
            return;

        SuppressedIssues.Add(new SuppressedIssueEntry
        {
            RuleId = issue.RuleId,
            FilePath = issue.FilePath,
            Line = issue.Line,
            Message = issue.Message
        });

        RefreshFilteredDefects();
        OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
        OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
        StatusMessage = $"예외(무시) 처리됨: {issue.FileName}:{issue.Line} [{issue.RuleId}]";
    }

    public void RestoreSuppressedIssue(SuppressedIssueEntry entry)
    {
        SuppressedIssues.Remove(entry);
        RefreshFilteredDefects();
        OnPropertyChanged(nameof(IsSelectedIssueSuppressed));
        OnPropertyChanged(nameof(SelectedIssueToggleButtonText));
        StatusMessage = $"예외 해제(복원)됨: {Path.GetFileName(entry.FilePath)}:{entry.Line} [{entry.RuleId}]";
    }

    public void RestoreAllSuppressedIssues()
    {
        if (SuppressedIssues.Count == 0)
            return;
        SuppressedIssues.Clear();
        RefreshFilteredDefects();
        StatusMessage = "모든 예외(무시) 처리 항목이 다시 결함 목록으로 복원되었습니다.";
    }

    public void DisableRuleEntirely(string ruleId)
    {
        var rule = AllRules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule != null)
        {
            rule.IsEnabled = false;
        }
        else
        {
            _allRawDefects.RemoveAll(d => string.Equals(d.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        }

        RefreshFilteredDefects();
        OnPropertyChanged(nameof(RulesetButtonText));
        StatusMessage = $"규칙 '{ruleId}' 전체가 현재 프로젝트 검사에서 제외(OFF)되었습니다.";
    }

    public void SaveProjectToFile(string filePath)
    {
        var targetRootPaths = TargetRoots.Select(r => r.FullPath).ToList();
        var nonTargetRootPaths = NonTargetRoots.Select(r => r.FullPath).ToList();

        var model = new ProjectFileModel
        {
            ProjectName = Path.GetFileNameWithoutExtension(filePath),
            TargetPresetId = SelectedTargetPreset?.Id ?? "ti-c6678-eabi",
            CStandard = SelectedCStandard,
            CustomDefines = CustomDefines,
            ForceAllIfdefConfigs = ForceAllIfdefConfigs,
            EnableClangTidy = EnableClangTidy,
            TargetPaths = targetRootPaths,
            NonTargetPaths = nonTargetRootPaths,
            // 현재 루트 폴더 아래에 있는 제외 항목만 저장 (루트 자체를 지운 경우 남은 기록은 정리)
            ExcludedTargetPaths = _excludedTargetSubpaths.Where(p => IsUnderAnyRoot(p, targetRootPaths)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            ExcludedNonTargetPaths = _excludedNonTargetSubpaths.Where(p => IsUnderAnyRoot(p, nonTargetRootPaths)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            EnabledRuleIds = AllRules.Where(r => r.IsEnabled).Select(r => r.Id).ToList(),
            DisabledRuleIds = AllRules.Where(r => !r.IsEnabled).Select(r => r.Id).ToList(),
            SuppressedIssues = SuppressedIssues.ToList()
        };

        try
        {
            _repository.SaveProject(filePath, model);
        }
        catch (Exception ex)
        {
            StatusMessage = $"프로젝트 저장 실패: {ex.Message}";
            return;
        }

        _currentProjectFilePath = filePath;
        OnPropertyChanged(nameof(CurrentProjectTitle));
        StatusMessage = $"프로젝트 저장 완료: {filePath}";
    }

    public void LoadProjectFromFile(string filePath)
    {
        ProjectFileModel? model;
        try
        {
            model = _repository.LoadProject(filePath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"프로젝트 파일을 불러올 수 없습니다 (파일 형식 오류): {ex.Message}";
            return;
        }

        if (model == null)
        {
            StatusMessage = "프로젝트 파일을 불러올 수 없습니다.";
            return;
        }

        ClearWorkspace();
        _currentProjectFilePath = filePath;
        OnPropertyChanged(nameof(CurrentProjectTitle));

        SelectedTargetPreset = TargetPresets.FirstOrDefault(t => t.Id == model.TargetPresetId) ?? TargetPresets.FirstOrDefault();
        SelectedCStandard = model.CStandard;
        CustomDefines = model.CustomDefines;
        ForceAllIfdefConfigs = model.ForceAllIfdefConfigs;
        EnableClangTidy = model.EnableClangTidy;

        // DB에 없는 ID도 프로젝트에 저장된 ON/OFF 선택을 재분석 전에 복원한다.
        var savedIds = model.EnabledRuleIds.Concat(model.DisabledRuleIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase);
        var knownIds = AllRules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in savedIds)
            if (knownIds.Add(id)) AddRule(RuleInfo.CreateUnregistered(id));

        if (model.DisabledRuleIds != null)
        {
            // 저장 당시 켜짐/꺼짐 목록에 있는 규칙은 그대로 복원하고,
            // 어느 목록에도 없는 규칙(저장 이후 rules_ko.json에 새로 추가된 규칙)은 규칙 DB의 기본값(defaultEnabled)을 따름
            var enabledSet = model.EnabledRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var disabledSet = model.DisabledRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in AllRules)
            {
                if (disabledSet.Contains(r.Id))
                    r.IsEnabled = false;
                else if (enabledSet.Contains(r.Id))
                    r.IsEnabled = true;
                else
                    r.IsEnabled = r.DefaultEnabled;
            }
        }
        else
        {
            // 이전 버전 프로젝트 파일(disabledRuleIds 없음): 켜진 규칙 목록 기준으로 복원
            var enabledSet = model.EnabledRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in AllRules)
            {
                r.IsEnabled = enabledSet.Contains(r.Id);
            }
        }

        // 제외 목록을 먼저 복원해야 루트 폴더를 다시 스캔할 때 제거/이동했던 하위 항목이 되살아나지 않음
        foreach (var p in model.ExcludedTargetPaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                _excludedTargetSubpaths.Add(Path.GetFullPath(p));
        }
        foreach (var p in model.ExcludedNonTargetPaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                _excludedNonTargetSubpaths.Add(Path.GetFullPath(p));
        }

        foreach (var p in model.TargetPaths)
            AddTargetPath(p);

        foreach (var p in model.NonTargetPaths)
            AddNonTargetPath(p);

        foreach (var s in model.SuppressedIssues)
            SuppressedIssues.Add(s);

        var firstFile = TargetRoots.SelectMany(r => r.EnumerateFiles()).FirstOrDefault();
        if (firstFile != null)
            LoadFileIntoCodeViewer(firstFile.FullPath);

        NotifyCountsChanged();
        StatusMessage = $"프로젝트 불러오기 완료: {filePath}";
    }

    public void OpenCurrentFileInExternalEditor(int line = 1)
    {
        if (string.IsNullOrEmpty(CurrentCodeFilePath) || !File.Exists(CurrentCodeFilePath))
            return;

        try
        {
            // VS Code가 설치되어 있으면 해당 줄로 이동하여 열고, 없으면 메모장으로 열기
            string editorName = ExternalEditorLauncher.Open(CurrentCodeFilePath, line);
            StatusMessage = editorName == "VS Code"
                ? $"VS Code로 열었습니다: {Path.GetFileName(CurrentCodeFilePath)} ({line}번째 줄)"
                : $"메모장으로 열었습니다: {Path.GetFileName(CurrentCodeFilePath)} (VS Code가 설치되어 있지 않아 줄 이동은 지원되지 않습니다)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"외부 편집기 실행 실패: {ex.Message}";
        }
    }

    public void OpenCurrentFileFolder()
    {
        if (string.IsNullOrEmpty(CurrentCodeFilePath) || !File.Exists(CurrentCodeFilePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{CurrentCodeFilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"폴더 열기 실패: {ex.Message}";
        }
    }

    private void UpdateTreeDefectBadges()
    {
        var countsByFile = FilteredDefects
            .GroupBy(d => AnalysisIssue.NormalizePath(d.FilePath))
            .ToDictionary(g => g.Key, g => g.Count());

        int UpdateNodeCount(SourceTreeNode node)
        {
            if (!node.IsDirectory)
            {
                string key = AnalysisIssue.NormalizePath(node.FullPath);
                node.DefectCount = countsByFile.TryGetValue(key, out int c) ? c : 0;
                return node.DefectCount;
            }

            int sum = 0;
            foreach (var child in node.Children)
                sum += UpdateNodeCount(child);
            node.DefectCount = sum;
            return sum;
        }

        foreach (var root in TargetRoots)
            UpdateNodeCount(root);
    }

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> rootPaths)
    {
        return rootPaths.Any(root =>
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountFiles(IEnumerable<SourceTreeNode> roots)
    {
        return roots.SelectMany(r => r.EnumerateFiles()).Count();
    }

    private void AddRule(RuleInfo rule)
    {
        rule.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RuleInfo.IsEnabled))
                OnPropertyChanged(nameof(RulesetButtonText));
        };
        AllRules.Add(rule);
        if (!string.IsNullOrWhiteSpace(rule.Category) && !DefectCategoryFilters.Contains(rule.Category))
            DefectCategoryFilters.Add(rule.Category);
    }

    public void NotifyCountsChanged()
    {
        OnPropertyChanged(nameof(RulesetButtonText));
        OnPropertyChanged(nameof(DefectsTabHeader));
        OnPropertyChanged(nameof(RuleSummaryTabHeader));
        OnPropertyChanged(nameof(RuleSummaryStatusText));
        OnPropertyChanged(nameof(NotesTabHeader));
        OnPropertyChanged(nameof(SuppressedTabHeader));
        OnPropertyChanged(nameof(TargetFilesSummary));
        OnPropertyChanged(nameof(NonTargetFilesSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
