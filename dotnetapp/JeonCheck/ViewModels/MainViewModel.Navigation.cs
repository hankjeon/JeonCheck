using System.IO;
using JeonCheck.Models;

namespace JeonCheck.ViewModels;

/// <summary>
/// 코드 뷰어 탐색 히스토리에 저장되는 파일 및 줄 위치 정보.
/// </summary>
public readonly record struct CodeNavigationEntry(string FilePath, int LineNumber)
{
    public string NormalizedPath => AnalysisIssue.NormalizePath(FilePath);

    public string DisplayLabel => $"{Path.GetFileName(FilePath)}:{LineNumber}";

    public bool IsSameLocation(CodeNavigationEntry other)
        => string.Equals(NormalizedPath, other.NormalizedPath, StringComparison.Ordinal)
           && LineNumber == other.LineNumber;
}

public partial class MainViewModel
{
    private readonly List<CodeNavigationEntry> _backHistory = new();
    private readonly List<CodeNavigationEntry> _forwardHistory = new();
    private const int MaxNavigationHistoryCount = 100;
    private int _currentCodeLineNumber = 1;

    /// <summary>
    /// 현재 코드 뷰어에서 보고 있는 줄 번호(1 기반).
    /// </summary>
    public int CurrentCodeLineNumber
    {
        get => _currentCodeLineNumber;
        private set
        {
            if (_currentCodeLineNumber != value)
            {
                _currentCodeLineNumber = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanNavigateBack => _backHistory.Count > 0;

    public bool CanNavigateForward => _forwardHistory.Count > 0;

    public string BackNavigationToolTip => CanNavigateBack
        ? $"뒤로 가기 (Alt+← / 마우스 뒤로) — {_backHistory[^1].DisplayLabel}"
        : "뒤로 가기 (이동 이력 없음)";

    public string ForwardNavigationToolTip => CanNavigateForward
        ? $"앞으로 가기 (Alt+→ / 마우스 앞으로) — {_forwardHistory[^1].DisplayLabel}"
        : "앞으로 가기 (이동 이력 없음)";

    /// <summary>
    /// 탐색 동작으로 특정 줄로 스크롤/선택 이동이 필요할 때 발생합니다.
    /// </summary>
    public event Action<int>? NavigationLineRequested;

    /// <summary>
    /// 사용자가 코드 뷰어에서 줄을 선택할 때 히스토리 스택을 변경하지 않고 현재 줄 번호만 갱신합니다.
    /// </summary>
    public void UpdateCurrentCodeLine(int lineNumber)
    {
        if (lineNumber > 0)
            CurrentCodeLineNumber = lineNumber;
    }

    /// <summary>
    /// 탐색 히스토리와 현재 줄 번호를 초기화합니다.
    /// </summary>
    public void ClearNavigationHistory()
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        CurrentCodeLineNumber = 1;
        NotifyNavigationStateChanged();
    }

    public void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        OnPropertyChanged(nameof(BackNavigationToolTip));
        OnPropertyChanged(nameof(ForwardNavigationToolTip));
    }

    /// <summary>
    /// 지정한 파일과 줄로 이동하며, 기존 위치가 있으면 뒤로 가기 히스토리에 기록합니다.
    /// </summary>
    public bool NavigateToCodeLocation(string filePath, int line = 1, string? encodingName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            StatusMessage = $"이동할 파일을 찾을 수 없습니다: {filePath}";
            return false;
        }

        string targetFullPath = Path.GetFullPath(filePath);
        string targetNorm = AnalysisIssue.NormalizePath(targetFullPath);
        string currentNorm = AnalysisIssue.NormalizePath(CurrentCodeFilePath);
        int targetLine = Math.Max(1, line);

        if (!string.IsNullOrEmpty(CurrentCodeFilePath) &&
            (!string.Equals(currentNorm, targetNorm, StringComparison.Ordinal) || CurrentCodeLineNumber != targetLine))
        {
            var currentEntry = new CodeNavigationEntry(CurrentCodeFilePath, Math.Max(1, CurrentCodeLineNumber));
            PushNavigationEntry(_backHistory, currentEntry);
            _forwardHistory.Clear();
            NotifyNavigationStateChanged();
        }

        if (!string.Equals(currentNorm, targetNorm, StringComparison.Ordinal) || encodingName != null)
        {
            LoadFileIntoCodeViewer(targetFullPath, encodingName);
        }

        CurrentCodeLineNumber = CurrentFileLines.Count > 0
            ? Math.Clamp(targetLine, 1, CurrentFileLines.Count)
            : targetLine;

        NavigationLineRequested?.Invoke(CurrentCodeLineNumber);
        return true;
    }

    /// <summary>
    /// 이전 코드 위치로 뒤로 이동합니다.
    /// </summary>
    public bool NavigateBack() => NavigateFromHistory(_backHistory, _forwardHistory);

    /// <summary>
    /// 뒤로 이동했던 코드 위치로 다시 앞으로 이동합니다.
    /// </summary>
    public bool NavigateForward() => NavigateFromHistory(_forwardHistory, _backHistory);

    private bool NavigateFromHistory(List<CodeNavigationEntry> sourceStack, List<CodeNavigationEntry> oppositeStack)
    {
        if (sourceStack.Count == 0)
            return false;

        var target = sourceStack[^1];
        sourceStack.RemoveAt(sourceStack.Count - 1);

        if (!File.Exists(target.FilePath))
        {
            NotifyNavigationStateChanged();
            StatusMessage = $"이동할 파일을 찾을 수 없습니다: {target.FilePath}";
            return false;
        }

        if (!string.IsNullOrEmpty(CurrentCodeFilePath))
        {
            var currentEntry = new CodeNavigationEntry(CurrentCodeFilePath, Math.Max(1, CurrentCodeLineNumber));
            PushNavigationEntry(oppositeStack, currentEntry);
        }

        string targetNorm = AnalysisIssue.NormalizePath(Path.GetFullPath(target.FilePath));
        string currentNorm = AnalysisIssue.NormalizePath(CurrentCodeFilePath);
        if (!string.Equals(currentNorm, targetNorm, StringComparison.Ordinal))
        {
            LoadFileIntoCodeViewer(target.FilePath);
        }

        int targetLine = Math.Max(1, target.LineNumber);
        CurrentCodeLineNumber = CurrentFileLines.Count > 0
            ? Math.Clamp(targetLine, 1, CurrentFileLines.Count)
            : targetLine;

        NotifyNavigationStateChanged();
        NavigationLineRequested?.Invoke(CurrentCodeLineNumber);
        return true;
    }

    private static void PushNavigationEntry(List<CodeNavigationEntry> stack, CodeNavigationEntry entry)
    {
        if (stack.Count > 0 && stack[^1].IsSameLocation(entry))
            return;

        if (stack.Count >= MaxNavigationHistoryCount)
            stack.RemoveAt(0);

        stack.Add(entry);
    }
}
