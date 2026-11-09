using JeonCheck.Services;
using System.ComponentModel;

namespace JeonCheck.Models;

public enum CodeLineStatus
{
    Neutral,    // 공백, 주석, 전처리 지시문 줄
    Analyzed,   // 전처리 통과 및 정적 분석 완료 줄 (🟢)
    Skipped,    // #ifdef 조건 불충족 등으로 전처리에서 제외된 미검사 줄 (⚪)
    Advisory,   // 권고(Style/Advisory) 규칙 위반 검출 줄 (🔵)
    Warning,    // 경고/필수 규칙 위반 검출 줄 (🟠)
    Error,      // 치명적 오류 검출 줄 (🔴)
    Suppressed  // 사용자가 '이 줄만 예외(무시) 처리'한 줄 (🔇 음영 표시 + 바로 복원 가능)
}

public class CodeLineViewModel : INotifyPropertyChanged
{
    private IReadOnlyList<CodeTextRange> _searchRanges = Array.Empty<CodeTextRange>();
    public IReadOnlyList<CodeTextRange> SearchRanges
    {
        get => _searchRanges;
        set { _searchRanges = value; PropertyChanged?.Invoke(this, new(nameof(SearchRanges))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public int LineNumber { get; set; }
    public string CodeText { get; set; } = string.Empty;
    public List<CodeSegment> HighlightSpans { get; set; } = new();
    public CodeLineStatus Status { get; set; } = CodeLineStatus.Neutral;
    public string InlineAnnotation { get; set; } = string.Empty;
    public string TooltipText { get; set; } = string.Empty;
    public List<AnalysisIssue> Issues { get; set; } = new();
    public List<SuppressedIssueEntry> SuppressedEntries { get; set; } = new();

    public string StatusSymbol => Status switch
    {
        CodeLineStatus.Error => "🔴",
        CodeLineStatus.Warning => "🟠",
        CodeLineStatus.Advisory => "🔵",
        CodeLineStatus.Suppressed => "🔇",
        CodeLineStatus.Analyzed => "🟢",
        CodeLineStatus.Skipped => "⚪",
        _ => ""
    };

    public bool HasInlineAnnotation => !string.IsNullOrEmpty(InlineAnnotation);
    public bool CanSuppressLine => (Status == CodeLineStatus.Error || Status == CodeLineStatus.Warning || Status == CodeLineStatus.Advisory) && Issues.Count > 0;
    public bool CanRestoreLine => Status == CodeLineStatus.Suppressed && SuppressedEntries.Count > 0;
}
