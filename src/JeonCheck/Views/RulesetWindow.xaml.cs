using System.Windows;
using System.Windows.Controls;
using JeonCheck.Models;
using JeonCheck.Services;

namespace JeonCheck.Views;

public partial class RulesetWindow : Window
{
    private readonly IReadOnlyList<RuleInfo> _allRules;
    private List<RuleInfo> _visibleRules = new(); // 현재 카테고리·검색어 필터를 통과해 목록에 보이는 규칙

    public RulesetWindow(IReadOnlyList<RuleInfo> allRules)
    {
        InitializeComponent();
        DarkTitleBarHelper.Apply(this);
        _allRules = allRules;

        var categories = new List<string> { "전체" };
        categories.AddRange(_allRules.Select(r => r.Category).Distinct());
        CategoryComboBox.ItemsSource = categories;
        CategoryComboBox.SelectedIndex = 0;

        foreach (var r in _allRules)
        {
            r.PropertyChanged += (_, _) => UpdateActiveCount();
        }

        ApplyFilter();
        UpdateActiveCount();
    }

    private void ApplyFilter()
    {
        if (RulesDataGrid == null)
            return;

        string selectedCategory = CategoryComboBox.SelectedItem as string ?? "전체";
        string query = SearchTextBox?.Text?.Trim() ?? string.Empty;

        var filtered = _allRules.Where(r =>
        {
            if (selectedCategory != "전체" && !string.Equals(r.Category, selectedCategory, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(query))
            {
                return r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       r.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       r.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }).ToList();

        _visibleRules = filtered;
        RulesDataGrid.ItemsSource = filtered;
        if (filtered.Count > 0)
            RulesDataGrid.SelectedIndex = 0;
    }

    private void UpdateActiveCount()
    {
        if (ActiveCountText == null)
            return;
        int enabled = _allRules.Count(r => r.IsEnabled);
        ActiveCountText.Text = $"현재 선택된 활성 규칙: {enabled}개 / 전체 {_allRules.Count}개";
    }

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void RulesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulesDataGrid.SelectedItem is RuleInfo rule)
        {
            PreviewTitleText.Text = $"[{rule.Id}] {rule.Title}";
            PreviewMetaText.Text = $"분류: {rule.Category}  |  등급: {rule.Obligation}  |  기본 심각도: {rule.Severity}";
            PreviewDescText.Text = rule.Description;
            const string noExample = "(이 규칙에는 등록된 코드 예시가 없습니다. 위 설명을 참고하십시오.)";
            PreviewBadCodeText.Text = string.IsNullOrWhiteSpace(rule.BadExample) ? noExample : rule.BadExample;
            PreviewGoodCodeText.Text = string.IsNullOrWhiteSpace(rule.GoodExample) ? noExample : rule.GoodExample;
        }
    }

    // [목록 전체 선택/해제]는 현재 카테고리·검색어로 걸러져 목록에 보이는 규칙에만 적용 (카테고리별 일괄 선택/해제)
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _visibleRules)
            r.IsEnabled = true;
        RulesDataGrid.Items.Refresh();
        UpdateActiveCount();
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _visibleRules)
            r.IsEnabled = false;
        RulesDataGrid.Items.Refresh();
        UpdateActiveCount();
    }

    private void SelectRequiredOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _allRules)
        {
            r.IsEnabled = r.Obligation.Contains("필수", StringComparison.OrdinalIgnoreCase) ||
                          r.Obligation.Contains("치명", StringComparison.OrdinalIgnoreCase) ||
                          r.Obligation.Contains("강제", StringComparison.OrdinalIgnoreCase);
        }
        RulesDataGrid.Items.Refresh();
        UpdateActiveCount();
    }

    private void SelectConversionOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _allRules)
        {
            r.IsEnabled = r.Category.Contains("형변환", StringComparison.OrdinalIgnoreCase) ||
                          r.Id.StartsWith("misra-c2012-10.", StringComparison.OrdinalIgnoreCase) ||
                          r.Id.StartsWith("misra-c2012-11.", StringComparison.OrdinalIgnoreCase);
        }
        RulesDataGrid.Items.Refresh();
        UpdateActiveCount();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
