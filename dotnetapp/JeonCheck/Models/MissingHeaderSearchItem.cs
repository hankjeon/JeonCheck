using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace JeonCheck.Models;

public enum MissingHeaderMatchStatus
{
    NotSearched,
    FoundSingle,
    FoundMultiple,
    NotFound
}

public class MissingHeaderSearchItem : INotifyPropertyChanged
{
    private static readonly Regex IncludeMsgRegex = new(
        @"Include\s+file:\s*[<""](?<inc>[^>""]+)[>""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SourceLineIncludeRegex = new(
        @"#\s*include\s*[<""](?<inc>[^>""]+)[>""]",
        RegexOptions.Compiled);

    private bool _isSelected;
    private string _selectedCandidatePath = string.Empty;
    private MissingHeaderMatchStatus _status = MissingHeaderMatchStatus.NotSearched;

    /// <summary>원본 #include 문자열 (예: "ti/csl/cslr_device.h" 또는 "missing_vendor_sdk.h")</summary>
    public string RawInclude { get; set; } = string.Empty;

    /// <summary>순수 파일명 (예: "cslr_device.h")</summary>
    public string HeaderFileName => Path.GetFileName(RawInclude.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>이 헤더를 #include 한 소스 파일 경로 목록</summary>
    public List<string> ReferencedByFiles { get; set; } = new();

    public string ReferencedByDisplay => ReferencedByFiles.Count == 0
        ? "-"
        : (ReferencedByFiles.Count == 1
            ? Path.GetFileName(ReferencedByFiles[0])
            : $"{Path.GetFileName(ReferencedByFiles[0])} 외 {ReferencedByFiles.Count - 1}개");

    public ObservableCollection<string> CandidatePaths { get; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string SelectedCandidatePath
    {
        get => _selectedCandidatePath;
        set
        {
            if (_selectedCandidatePath != value)
            {
                _selectedCandidatePath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public MissingHeaderMatchStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(HasMultipleCandidates));
                OnPropertyChanged(nameof(HasSingleOrNoCandidate));
                OnPropertyChanged(nameof(SingleOrNotFoundDisplay));
            }
        }
    }

    public bool CanSelect => Status is MissingHeaderMatchStatus.FoundSingle or MissingHeaderMatchStatus.FoundMultiple;
    public bool HasMultipleCandidates => Status == MissingHeaderMatchStatus.FoundMultiple;
    public bool HasSingleOrNoCandidate => Status != MissingHeaderMatchStatus.FoundMultiple;

    public string StatusDisplay => Status switch
    {
        MissingHeaderMatchStatus.FoundSingle => "✅ 발견됨 (1개)",
        MissingHeaderMatchStatus.FoundMultiple => $"⚠️ 중복 ({CandidatePaths.Count}개 후보)",
        MissingHeaderMatchStatus.NotFound => "❌ 미발견",
        _ => "⏳ 검색 전"
    };

    public string SingleOrNotFoundDisplay => Status switch
    {
        MissingHeaderMatchStatus.FoundSingle => SelectedCandidatePath,
        MissingHeaderMatchStatus.NotFound => "(선택한 폴더 하위에서 파일을 찾지 못했습니다)",
        _ => "(상단 [📂 검색할 폴더 선택...] 버튼을 눌러 폴더를 지정하세요)"
    };

    public void RefreshStatusBindings()
    {
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(HasMultipleCandidates));
        OnPropertyChanged(nameof(HasSingleOrNoCandidate));
        OnPropertyChanged(nameof(SingleOrNotFoundDisplay));
    }

    /// <summary>
    /// 노트 항목(missingInclude / missingIncludeSystem)에서 누락된 #include 헤더 이름을 추출한다.
    /// </summary>
    public static string? ExtractMissingHeaderName(AnalysisIssue note)
    {
        if (note == null)
            return null;

        if (!string.Equals(note.RuleId, "missingInclude", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(note.RuleId, "missingIncludeSystem", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var m = IncludeMsgRegex.Match(note.Message ?? string.Empty);
        if (m.Success)
        {
            string inc = m.Groups["inc"].Value.Trim();
            if (!string.IsNullOrEmpty(inc))
                return inc;
        }

        // 메시지에서 추출되지 않으면 실제 소스 파일의 해당 줄에서 #include 구문을 직접 확인
        if (!string.IsNullOrEmpty(note.FilePath) && File.Exists(note.FilePath) && note.Line > 0)
        {
            try
            {
                string? lineText = File.ReadLines(note.FilePath).Skip(note.Line - 1).FirstOrDefault();
                if (!string.IsNullOrEmpty(lineText))
                {
                    var lineMatch = SourceLineIncludeRegex.Match(lineText);
                    if (lineMatch.Success)
                        return lineMatch.Groups["inc"].Value.Trim();
                }
            }
            catch
            {
                // 파일 읽기 실패 무시
            }
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
