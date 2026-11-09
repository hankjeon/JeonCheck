using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using JeonCheck.Models;
using JeonCheck.Services;

namespace JeonCheck.ViewModels;

public partial class MainViewModel
{
    private string _sourceSearchText = string.Empty;
    private int _sourceSearchScope;
    private bool _sourceSearchRegex, _sourceSearchMatchCase, _sourceSearchWholeWord, _isSourceSearching;
    private string _sourceSearchStatus = "검색어를 입력하고 [검색] 버튼을 누르세요.";
    private CancellationTokenSource? _sourceSearchCts;
    private SourceSearchOptions? _executedSearchOptions;

    public IReadOnlyList<string> SourceSearchScopes { get; } = new[] { "전체 프로젝트", "대상 파일", "참조 파일", "현재 파일" };
    public ObservableCollection<SourceSearchFile> SourceSearchFiles { get; } = new();
    public string SourceSearchText
    {
        get => _sourceSearchText;
        set { _sourceSearchText = value; OnPropertyChanged(); }
    }
    public int SourceSearchScope
    {
        get => _sourceSearchScope;
        set { _sourceSearchScope = value; OnPropertyChanged(); }
    }
    public bool SourceSearchRegex
    {
        get => _sourceSearchRegex;
        set { _sourceSearchRegex = value; OnPropertyChanged(); }
    }
    public bool SourceSearchMatchCase
    {
        get => _sourceSearchMatchCase;
        set { _sourceSearchMatchCase = value; OnPropertyChanged(); }
    }
    public bool SourceSearchWholeWord
    {
        get => _sourceSearchWholeWord;
        set { _sourceSearchWholeWord = value; OnPropertyChanged(); }
    }
    public bool IsSourceSearching
    {
        get => _isSourceSearching;
        private set { _isSourceSearching = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSourceSearch)); }
    }
    public bool CanSourceSearch => !IsSourceSearching;
    public string SourceSearchStatus
    {
        get => _sourceSearchStatus;
        private set { _sourceSearchStatus = value; OnPropertyChanged(); }
    }

    public void PrepareSourceSearch(string text)
    {
        SourceSearchText = text;
        SourceSearchRegex = false;
        // 이전 검색 결과와 실행 조건은 [검색]을 누를 때까지 유지한다.
    }

    public async Task SearchSourceAsync()
    {
        if (IsSourceSearching) return;
        var options = new SourceSearchOptions(SourceSearchText, SourceSearchRegex, SourceSearchMatchCase, SourceSearchWholeWord);
        Regex regex;
        try { regex = options.CreateRegex(); }
        catch (ArgumentException ex)
        {
            SourceSearchStatus = $"검색 조건 오류: {ex.Message}";
            return;
        }

        string[] paths = SourceSearch.CollectPaths(TargetRoots, NonTargetRoots, SourceSearchScope, CurrentCodeFilePath);
        string currentPath = CurrentCodeFilePath;
        string? encoding = _fileEncodingPinned ? CurrentFileEncodingName : null;
        var cts = new CancellationTokenSource();
        _sourceSearchCts = cts;
        IsSourceSearching = true;
        SourceSearchFiles.Clear();
        ClearSearchHighlights();
        _executedSearchOptions = options;
        SourceSearchStatus = $"{paths.Length}개 파일 검색 중...";
        try
        {
            var report = await Task.Run(() => SourceSearch.Search(paths, regex, cts.Token, currentPath, encoding), cts.Token);
            if (_sourceSearchCts != cts) return;
            cts.Token.ThrowIfCancellationRequested();
            foreach (var file in report.Files) SourceSearchFiles.Add(file);
            SourceSearchStatus = $"{report.Files.Count}개 파일, {report.Files.Sum(f => f.Hits.Count)}건" +
                (paths.Length == 0 ? " — 검색 범위에 파일이 없습니다." : "") +
                (report.Warnings.Count == 0 ? "" : $"\n{string.Join("\n", report.Warnings)}");
        }
        catch (OperationCanceledException)
        {
            if (_sourceSearchCts == cts) SourceSearchStatus = "검색을 취소했습니다.";
        }
        catch (RegexMatchTimeoutException)
        {
            if (_sourceSearchCts == cts) SourceSearchStatus = "정규식 처리 시간이 초과되었습니다. 검색식을 단순하게 바꿔 주세요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_sourceSearchCts == cts) SourceSearchStatus = $"검색 실패: {ex.Message}";
        }
        finally
        {
            if (_sourceSearchCts == cts)
            {
                _sourceSearchCts = null;
                IsSourceSearching = false;
            }
            cts.Dispose();
        }
    }

    public void CancelSourceSearch() => _sourceSearchCts?.Cancel();

    private void ResetSourceSearch()
    {
        CancelSourceSearch();
        _sourceSearchCts = null;
        IsSourceSearching = false;
        SourceSearchFiles.Clear();
        _executedSearchOptions = null;
        SourceSearchStatus = "검색어를 입력하고 [검색] 버튼을 누르세요.";
    }

    private void ClearSearchHighlights()
    {
        foreach (var line in CurrentFileLines)
            if (line.SearchRanges.Count > 0) line.SearchRanges = Array.Empty<CodeTextRange>();
    }

    public bool OpenSourceSearchHit(SourceSearchHit hit)
    {
        try
        {
            var source = SourceFileEncoding.ReadAllLines(hit.FilePath, hit.EncodingName);
            if (hit.Line < 1 || hit.Line > source.Lines.Length || source.Lines[hit.Line - 1] != hit.LineText)
            {
                SourceSearchStatus = "검색 후 파일 내용이 변경되었습니다. 다시 검색해 주세요.";
                return false;
            }
            LoadFileIntoCodeViewer(hit.FilePath, hit.EncodingName);
            ClearSearchHighlights();
            if (_executedSearchOptions != null)
            {
                var regex = _executedSearchOptions.CreateRegex();
                var line = CurrentFileLines[hit.Line - 1];
                // 원본에서 검색한 범위를 변환하므로 들여쓰기 의존 정규식도 결과와 강조가 일치한다.
                // 선택한 결과를 맨 앞에 두어 같은 줄의 여러 일치 중 정확한 열로 가로 스크롤한다.
                line.SearchRanges = regex.Matches(hit.LineText).Cast<Match>().Where(m => m.Length > 0)
                    .OrderBy(m => m.Index == hit.Column - 1 ? 0 : 1)
                    .Select(m => CCodePrettifier.MapRangeToDisplay(hit.LineText, line.CodeText, new(m.Index, m.Length)))
                    .Where(r => r.Length > 0).ToArray();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            SourceSearchStatus = $"검색 위치를 열 수 없습니다: {ex.Message}";
            return false;
        }
    }
}
