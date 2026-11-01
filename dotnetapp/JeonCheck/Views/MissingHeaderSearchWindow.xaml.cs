using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using JeonCheck.Models;
using JeonCheck.Services;

namespace JeonCheck.Views;

public partial class MissingHeaderSearchWindow : Window
{
    private readonly ObservableCollection<MissingHeaderSearchItem> _items = new();
    private readonly List<string> _searchedFolders = new();

    /// <summary>사용자가 최종 확인을 눌렀을 때 비대상(참조 코드) 트리에 추가할 경로(폴더 또는 파일) 목록</summary>
    public List<string> SelectedPathsToAdd { get; } = new();

    public MissingHeaderSearchWindow(IEnumerable<MissingHeaderSearchItem> missingItems, string? initialSearchFolder = null)
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);

        foreach (var item in missingItems)
            _items.Add(item);

        HeadersDataGrid.ItemsSource = _items;
        UpdateSummaryText();

        if (!string.IsNullOrWhiteSpace(initialSearchFolder) && Directory.Exists(initialSearchFolder))
        {
            Loaded += (_, _) => PerformSearchInFolder(initialSearchFolder);
        }
    }

    private void SelectFolderAndSearch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "누락된 헤더 파일을 검색할 상위 폴더 선택 (하위 모든 폴더 자동 탐색)"
        };

        if (dlg.ShowDialog(this) == true && Directory.Exists(dlg.FolderName))
        {
            PerformSearchInFolder(dlg.FolderName);
        }
    }

    public void PerformSearchInFolder(string rootFolder)
    {
        string fullRoot = Path.GetFullPath(rootFolder);
        if (!_searchedFolders.Contains(fullRoot, StringComparer.OrdinalIgnoreCase))
        {
            _searchedFolders.Add(fullRoot);
            SearchedFoldersText.Text = string.Join(" ; ", _searchedFolders);
            SearchedFoldersText.ToolTip = SearchedFoldersText.Text;
        }

        // 필요한 파일명 목록만 빠르게 인덱싱 (접근 권한 없는 하위 폴더도 안전하게 건너뜀)
        var targetFileNames = _items
            .Select(x => x.HeaderFileName)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var foundByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            foreach (var filePath in Directory.EnumerateFiles(fullRoot, "*", enumOptions))
            {
                string fileName = Path.GetFileName(filePath);
                if (!targetFileNames.Contains(fileName))
                    continue;

                if (!foundByFileName.TryGetValue(fileName, out var list))
                {
                    list = new List<string>();
                    foundByFileName[fileName] = list;
                }
                list.Add(Path.GetFullPath(filePath));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"폴더 탐색 중 오류가 발생했습니다:\n{ex.Message}", "폴더 검색 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        foreach (var item in _items)
        {
            if (foundByFileName.TryGetValue(item.HeaderFileName, out var newMatches))
            {
                foreach (var m in newMatches)
                {
                    if (!item.CandidatePaths.Contains(m, StringComparer.OrdinalIgnoreCase))
                        item.CandidatePaths.Add(m);
                }
            }

            if (item.CandidatePaths.Count == 0)
            {
                item.Status = MissingHeaderMatchStatus.NotFound;
                item.IsSelected = false;
                item.SelectedCandidatePath = string.Empty;
            }
            else if (item.CandidatePaths.Count == 1)
            {
                item.SelectedCandidatePath = item.CandidatePaths[0];
                item.Status = MissingHeaderMatchStatus.FoundSingle;
                item.IsSelected = true;
            }
            else
            {
                // 중복 후보들을 스마트 매칭 점수 순으로 정렬하여 가장 적합한 후보를 1순위로 자동 선택
                var ranked = RankCandidates(item.RawInclude, item.ReferencedByFiles, item.CandidatePaths.ToList());
                item.CandidatePaths.Clear();
                foreach (var r in ranked)
                    item.CandidatePaths.Add(r);

                item.SelectedCandidatePath = item.CandidatePaths[0];
                item.Status = MissingHeaderMatchStatus.FoundMultiple;
                item.IsSelected = true;
            }

            item.RefreshStatusBindings();
        }

        UpdateSummaryText();
    }

    /// <summary>
    /// 동일 이름의 헤더가 여러 하위 폴더에서 발견되었을 때:
    /// 1순위: #include "sub/header.h" 상대 경로 접미사가 일치하는 정도
    /// 2순위: 이 헤더를 #include 한 소스 파일(.c)과의 공통 상위 경로 길이
    /// 3순위: 전체 경로 길이가 더 짧고 간결한 쪽
    /// </summary>
    private static List<string> RankCandidates(string rawInclude, List<string> referencedByFiles, List<string> candidates)
    {
        var incParts = rawInclude
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .ToArray();

        int ScoreCandidate(string candPath)
        {
            int suffixScore = 0;
            var candParts = candPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int i = incParts.Length - 1;
            int j = candParts.Length - 1;
            while (i >= 0 && j >= 0 && string.Equals(incParts[i], candParts[j], StringComparison.OrdinalIgnoreCase))
            {
                suffixScore += 100;
                i--;
                j--;
            }

            int proximityScore = 0;
            foreach (var refFile in referencedByFiles)
            {
                int commonLen = GetCommonPrefixLength(refFile, candPath);
                if (commonLen > proximityScore)
                    proximityScore = commonLen;
            }

            return suffixScore + proximityScore;
        }

        return candidates
            .OrderByDescending(ScoreCandidate)
            .ThenBy(c => c.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetCommonPrefixLength(string pathA, string pathB)
    {
        int max = Math.Min(pathA.Length, pathB.Length);
        int count = 0;
        for (int i = 0; i < max; i++)
        {
            if (char.ToUpperInvariant(pathA[i]) == char.ToUpperInvariant(pathB[i]))
                count++;
            else
                break;
        }
        return count;
    }

    private void UpdateSummaryText()
    {
        int total = _items.Count;
        int single = _items.Count(x => x.Status == MissingHeaderMatchStatus.FoundSingle);
        int multi = _items.Count(x => x.Status == MissingHeaderMatchStatus.FoundMultiple);
        int notFound = _items.Count(x => x.Status == MissingHeaderMatchStatus.NotFound);

        SummaryStatusText.Text =
            $"총 누락 헤더 {total}종  |  ✅ 단일 발견: {single}건  |  ⚠️ 중복 발견(경로 선택 가능): {multi}건  |  ❌ 미발견: {notFound}건";
    }

    private void SelectAllFound_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            if (item.CanSelect)
                item.IsSelected = true;
        }
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsSelected = false;
        }
    }

    private void ConfirmAdd_Click(object sender, RoutedEventArgs e)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _items)
        {
            if (!item.IsSelected || !item.CanSelect || string.IsNullOrWhiteSpace(item.SelectedCandidatePath))
                continue;

            string chosenFile = item.SelectedCandidatePath;
            if (!File.Exists(chosenFile))
                continue;

            paths.Add(Path.GetFullPath(chosenFile));
        }

        if (paths.Count == 0)
        {
            MessageBox.Show(this, "추가할 항목이 선택되지 않았거나 발견된 헤더 파일이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPathsToAdd.Clear();
        SelectedPathsToAdd.AddRange(paths);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
