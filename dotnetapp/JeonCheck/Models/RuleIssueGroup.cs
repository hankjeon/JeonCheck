using System.ComponentModel;

namespace JeonCheck.Models;

public class RuleIssueGroup : INotifyPropertyChanged
{
    private bool _isExpanded = false;

    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<RuleIssueOccurrence> Occurrences { get; init; } = new();
    public int Count => Occurrences.Count;
    public string Header => $"{RuleId} · {Title} · {Count}건";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class RuleIssueOccurrence
{
    // 원본 진단을 유지하여 같은 줄의 여러 항목도 정확한 상세 설명으로 연결한다.
    public AnalysisIssue Issue { get; init; } = new();
    public string CodeText { get; init; } = string.Empty;
    public string Location => Issue.Column > 0
        ? $"{Issue.FilePath}:{Issue.Line}:{Issue.Column}"
        : $"{Issue.FilePath}:{Issue.Line}";
}
