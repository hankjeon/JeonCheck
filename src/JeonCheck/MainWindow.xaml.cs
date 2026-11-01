using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;
using JeonCheck.Views;

namespace JeonCheck;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush MarkerErrorBrush = CreateFrozenBrush("#F85149");
    private static readonly SolidColorBrush MarkerWarningBrush = CreateFrozenBrush("#D29922");
    private static readonly SolidColorBrush MarkerAdvisoryBrush = CreateFrozenBrush("#58A6FF");

    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        DarkTitleBarHelper.Apply(this);

        ViewModel.CodeLinesReloaded += RenderOverviewMarkers;
        ViewModel.RecentProjectsChanged += RebuildRecentProjectsSubmenu;
        Loaded += (_, _) =>
        {
            RenderOverviewMarkers();
            RebuildRecentProjectsSubmenu();
        };
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void OverviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderOverviewMarkers();
    }

    private void RenderOverviewMarkers()
    {
        if (OverviewCanvas == null)
            return;

        OverviewCanvas.Children.Clear();

        int totalLines = ViewModel.CurrentFileLines.Count;
        double canvasHeight = OverviewCanvas.ActualHeight;
        double canvasWidth = OverviewCanvas.ActualWidth;
        if (totalLines <= 0 || canvasHeight <= 4 || canvasWidth <= 2)
            return;

        // 권고(파랑) -> 경고(주황) -> 오류(빨강) 순서로 그려서, 겹칠 때 더 높은 심각도가 위에 보이도록 함
        var defectLines = ViewModel.CurrentFileLines
            .Where(l => l.Status is CodeLineStatus.Advisory or CodeLineStatus.Warning or CodeLineStatus.Error)
            .OrderBy(l => l.Status switch
            {
                CodeLineStatus.Advisory => 0,
                CodeLineStatus.Warning => 1,
                CodeLineStatus.Error => 2,
                _ => 0
            })
            .ThenBy(l => l.LineNumber);

        foreach (var lineVm in defectLines)
        {
            Brush brush = lineVm.Status switch
            {
                CodeLineStatus.Error => MarkerErrorBrush,
                CodeLineStatus.Warning => MarkerWarningBrush,
                _ => MarkerAdvisoryBrush
            };

            double ratio = totalLines > 1
                ? (double)(lineVm.LineNumber - 1) / (totalLines - 1)
                : 0.0;
            double y = Math.Clamp(ratio * (canvasHeight - 4.0), 0.0, Math.Max(0.0, canvasHeight - 4.0));

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(6.0, canvasWidth - 2.0),
                Height = 3.5,
                RadiusX = 1,
                RadiusY = 1,
                Fill = brush,
                Tag = lineVm,
                ToolTip = $"[{lineVm.LineNumber}번째 줄] {lineVm.InlineAnnotation}\n{lineVm.TooltipText}"
            };

            Canvas.SetLeft(rect, 1.0);
            Canvas.SetTop(rect, y);
            OverviewCanvas.Children.Add(rect);
        }
    }

    private void OverviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Shapes.Rectangle rect && rect.Tag is CodeLineViewModel clickedLineVm)
        {
            JumpToCodeLine(clickedLineVm);
            e.Handled = true;
            return;
        }

        JumpToOverviewPosition(e.GetPosition(OverviewCanvas).Y);
    }

    private void OverviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            JumpToOverviewPosition(e.GetPosition(OverviewCanvas).Y);
        }
    }

    private void JumpToOverviewPosition(double mouseY)
    {
        int totalLines = ViewModel.CurrentFileLines.Count;
        double canvasHeight = OverviewCanvas.ActualHeight;
        if (totalLines <= 0 || canvasHeight <= 1)
            return;

        // 1) 클릭한 Y 좌표 근처(±6px)에 결함 마커가 있으면 가장 가까운 결함 줄로 스냅 이동
        CodeLineViewModel? nearestDefectLine = null;
        double minDistance = double.MaxValue;
        foreach (var lineVm in ViewModel.CurrentFileLines)
        {
            if (lineVm.Status is not (CodeLineStatus.Error or CodeLineStatus.Warning or CodeLineStatus.Advisory))
                continue;

            double ratio = totalLines > 1 ? (double)(lineVm.LineNumber - 1) / (totalLines - 1) : 0.0;
            double markerCenterY = (ratio * (canvasHeight - 4.0)) + 1.75;
            double dist = Math.Abs(mouseY - markerCenterY);
            if (dist <= 6.0 && dist < minDistance)
            {
                minDistance = dist;
                nearestDefectLine = lineVm;
            }
        }

        if (nearestDefectLine != null)
        {
            JumpToCodeLine(nearestDefectLine);
            return;
        }

        // 2) 마커가 없는 빈 트랙을 클릭/드래그한 경우 해당 비율 위치의 코드 줄로 이동
        double clickRatio = Math.Clamp(mouseY / canvasHeight, 0.0, 1.0);
        int targetIndex = (int)Math.Round(clickRatio * (totalLines - 1));
        targetIndex = Math.Clamp(targetIndex, 0, totalLines - 1);
        JumpToCodeLine(ViewModel.CurrentFileLines[targetIndex]);
    }

    private void JumpToCodeLine(CodeLineViewModel targetLineVm)
    {
        CodeViewerListBox.SelectedItem = targetLineVm;
        CodeViewerListBox.ScrollIntoView(targetLineVm);
        if (targetLineVm.Issues.Count > 0)
        {
            ViewModel.SelectedIssue = targetLineVm.Issues[0];
        }
    }

    private void OpenAboutWindow_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearWorkspace();
    }

    private void LoadSample_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadSampleWorkspace();
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "전검사 프로젝트 파일 열기",
            Filter = "전검사 프로젝트 (*.jcproj)|*.jcproj|JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.LoadProjectFromFile(dlg.FileName);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.CurrentProjectFilePath))
        {
            ViewModel.SaveProjectToFile(ViewModel.CurrentProjectFilePath);
            return;
        }

        PromptAndSaveProjectAs();
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        PromptAndSaveProjectAs();
    }

    private void PromptAndSaveProjectAs()
    {
        string defaultFileName = !string.IsNullOrWhiteSpace(ViewModel.CurrentProjectFilePath)
            ? Path.GetFileName(ViewModel.CurrentProjectFilePath)
            : "embedded_analysis.jcproj";

        var dlg = new SaveFileDialog
        {
            Title = "전검사 프로젝트 다른 이름으로 저장",
            Filter = "전검사 프로젝트 (*.jcproj)|*.jcproj",
            DefaultExt = ".jcproj",
            FileName = defaultFileName
        };

        if (!string.IsNullOrWhiteSpace(ViewModel.CurrentProjectFilePath))
        {
            string? currentDir = Path.GetDirectoryName(ViewModel.CurrentProjectFilePath);
            if (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
                dlg.InitialDirectory = currentDir;
        }

        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.SaveProjectToFile(dlg.FileName);
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RebuildRecentProjectsSubmenu();
    }

    private void RebuildRecentProjectsSubmenu()
    {
        if (RecentProjectsMenuItem == null)
            return;

        RecentProjectsMenuItem.Items.Clear();

        var subItemStyle = TryFindResource("DarkSubMenuItemStyle") as Style;
        var sepStyle = TryFindResource("DarkMenuSeparatorStyle") as Style;

        if (ViewModel.RecentProjects.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(최근 사용한 프로젝트 없음)",
                IsEnabled = false,
                Style = subItemStyle
            };
            RecentProjectsMenuItem.Items.Add(emptyItem);
            return;
        }

        for (int i = 0; i < ViewModel.RecentProjects.Count; i++)
        {
            string fullPath = ViewModel.RecentProjects[i];
            string fileName = Path.GetFileName(fullPath);
            string accessKey = (i + 1) <= 9 ? $"_{i + 1}" : "1_0";

            var item = new MenuItem
            {
                Header = $"{accessKey}. {fileName}  ({fullPath})",
                Tag = fullPath,
                ToolTip = fullPath,
                Style = subItemStyle
            };
            item.Click += RecentProjectItem_Click;
            RecentProjectsMenuItem.Items.Add(item);
        }

        RecentProjectsMenuItem.Items.Add(new Separator { Style = sepStyle });

        var clearItem = new MenuItem
        {
            Header = "🗑 최근 목록 비우기(_C)",
            Style = subItemStyle
        };
        clearItem.Click += (_, _) => ViewModel.ClearRecentProjects();
        RecentProjectsMenuItem.Items.Add(clearItem);
    }

    private void RecentProjectItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string path && !string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                var res = MessageBox.Show(
                    this,
                    $"프로젝트 파일을 찾을 수 없습니다:\n{path}\n\n최근 사용한 프로젝트 목록에서 제거할까요?",
                    "파일 없음",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (res == MessageBoxResult.Yes)
                {
                    ViewModel.RemoveRecentProject(path);
                }
                return;
            }

            ViewModel.LoadProjectFromFile(path);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (e.Key == Key.S)
            {
                PromptAndSaveProjectAs();
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                NewProject_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.O:
                OpenProject_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.S:
                SaveProject_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private const string FolderSelectionToken = "현재 폴더 전체 추가";

    private void AddTargetItems_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectFilesOrFolderDialog("검사대상 코드 추가 (파일 다중 선택 또는 폴더 안에서 바로 [열기] 클릭)");
        foreach (var path in paths)
        {
            ViewModel.AddTargetPath(path);
        }
    }

    private void RemoveTargetNode_Click(object sender, RoutedEventArgs e)
    {
        if (TargetTreeView.SelectedItem is SourceTreeNode node)
        {
            ViewModel.RemoveNode(node);
        }
        else
        {
            ViewModel.StatusMessage = "제거할 파일이나 폴더를 [검사대상 코드] 목록에서 먼저 선택해 주세요.";
        }
    }

    private void AddNonTargetItems_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectFilesOrFolderDialog("참조코드(비대상) 추가 (파일 다중 선택 또는 폴더 안에서 바로 [열기] 클릭)");
        foreach (var path in paths)
        {
            ViewModel.AddNonTargetPath(path);
        }
    }

    private void RemoveNonTargetNode_Click(object sender, RoutedEventArgs e)
    {
        if (NonTargetTreeView.SelectedItem is SourceTreeNode node)
        {
            ViewModel.RemoveNode(node);
        }
        else
        {
            ViewModel.StatusMessage = "제거할 파일이나 폴더를 [참조코드(비대상)] 목록에서 먼저 선택해 주세요.";
        }
    }

    /// <summary>
    /// 하나의 열기 다이얼로그에서 파일 1개/다중 선택(Ctrl/Shift) 또는 현재 위치한 폴더 전체 추가를 동시에 처리
    /// </summary>
    private List<string> SelectFilesOrFolderDialog(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "C/C++ 소스 및 헤더 또는 폴더 (*.c;*.h;*.cpp;*.hpp)|*.c;*.h;*.cpp;*.hpp|모든 파일 (*.*)|*.*",
            Multiselect = true,
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = FolderSelectionToken
        };

        var selectedPaths = new List<string>();
        if (dlg.ShowDialog(this) == true)
        {
            foreach (var rawName in dlg.FileNames)
            {
                if (File.Exists(rawName))
                {
                    selectedPaths.Add(Path.GetFullPath(rawName));
                }
                else if (Directory.Exists(rawName))
                {
                    selectedPaths.Add(Path.GetFullPath(rawName));
                }
                else
                {
                    string? dir = Path.GetDirectoryName(rawName);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        selectedPaths.Add(Path.GetFullPath(dir));
                    }
                }
            }
        }

        return selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void MoveToNonTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetTreeView.SelectedItem is SourceTreeNode node)
        {
            ViewModel.MoveNodeToNonTarget(node);
        }
        else
        {
            ViewModel.StatusMessage = "상단 [검사 대상 코드] 트리에서 비대상으로 제외할 폴더나 파일을 먼저 선택해 주세요.";
        }
    }

    private void MoveToTarget_Click(object sender, RoutedEventArgs e)
    {
        if (NonTargetTreeView.SelectedItem is SourceTreeNode node)
        {
            ViewModel.MoveNodeToTarget(node);
        }
        else
        {
            ViewModel.StatusMessage = "하단 [비대상 / 참조 코드] 트리에서 검사 대상으로 포함할 폴더나 파일을 먼저 선택해 주세요.";
        }
    }

    private void TargetTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SourceTreeNode node && !node.IsDirectory)
        {
            ViewModel.LoadFileIntoCodeViewer(node.FullPath);
        }
    }

    private void NonTargetTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SourceTreeNode node && !node.IsDirectory)
        {
            ViewModel.LoadFileIntoCodeViewer(node.FullPath);
        }
    }

    private void OpenRulesetWindow_Click(object sender, RoutedEventArgs e)
    {
        var win = new RulesetWindow(ViewModel.AllRules)
        {
            Owner = this
        };

        if (win.ShowDialog() == true)
        {
            ViewModel.RefreshFilteredDefects();
        }
    }

    private async void RunAnalysis_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunAnalysisAsync();
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelAnalysis();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 창을 닫을 때 진행 중인 분석을 취소하여 Cppcheck/Clang-Tidy 프로세스가 남지 않도록 함
        ViewModel.CancelAnalysis();
    }

    private void DefectsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefectsDataGrid.SelectedItem is AnalysisIssue issue)
        {
            ViewModel.SelectedIssue = issue;
            NavigateToFileAndLine(issue.FilePath, issue.Line);
        }
    }

    private void NotesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is AnalysisIssue note && !string.IsNullOrEmpty(note.FilePath))
        {
            NavigateToFileAndLine(note.FilePath, note.Line);
        }
    }

    private void CodeViewerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 결함 목록에서 같은 줄의 다른 결함을 선택해 이동한 경우에는 그 선택을 유지하고,
        // 코드 뷰어에서 직접 줄을 선택한 경우에만 그 줄의 첫 번째 결함을 선택
        if (CodeViewerListBox.SelectedItem is CodeLineViewModel lineVm && lineVm.Issues.Count > 0 &&
            (ViewModel.SelectedIssue == null || !lineVm.Issues.Contains(ViewModel.SelectedIssue)))
        {
            ViewModel.SelectedIssue = lineVm.Issues[0];
        }
    }

    private void NavigateToFileAndLine(string filePath, int line)
    {
        if (!string.Equals(
                AnalysisIssue.NormalizePath(ViewModel.CurrentCodeFilePath),
                AnalysisIssue.NormalizePath(filePath),
                StringComparison.Ordinal))
        {
            ViewModel.LoadFileIntoCodeViewer(filePath);
        }

        if (line > 0 && line <= ViewModel.CurrentFileLines.Count)
        {
            var targetLineVm = ViewModel.CurrentFileLines[line - 1];
            CodeViewerListBox.SelectedItem = targetLineVm;
            CodeViewerListBox.ScrollIntoView(targetLineVm);
        }
    }

    private void SuppressSingleIssue_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedIssue != null)
        {
            ViewModel.SuppressSingleIssue(ViewModel.SelectedIssue);
        }
    }

    private void ToggleSelectedIssueSuppression_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSelectedIssueSuppression();
    }

    private void InlineSuppressLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CodeLineViewModel lineVm)
        {
            int lineNum = lineVm.LineNumber;
            ViewModel.SuppressCodeLine(lineVm);
            if (lineNum > 0 && lineNum <= ViewModel.CurrentFileLines.Count)
            {
                CodeViewerListBox.SelectedItem = ViewModel.CurrentFileLines[lineNum - 1];
            }
        }
    }

    private void InlineRestoreLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CodeLineViewModel lineVm)
        {
            int lineNum = lineVm.LineNumber;
            ViewModel.RestoreCodeLine(lineVm);
            if (lineNum > 0 && lineNum <= ViewModel.CurrentFileLines.Count)
            {
                CodeViewerListBox.SelectedItem = ViewModel.CurrentFileLines[lineNum - 1];
            }
        }
    }

    private void DisableRuleEntirely_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedIssue != null)
        {
            ViewModel.DisableRuleEntirely(ViewModel.SelectedIssue.RuleId);
        }
    }

    private void RestoreSuppressed_Click(object sender, RoutedEventArgs e)
    {
        if (SuppressedDataGrid.SelectedItem is SuppressedIssueEntry entry)
        {
            ViewModel.RestoreSuppressedIssue(entry);
        }
    }

    private void RestoreRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SuppressedIssueEntry entry)
        {
            ViewModel.RestoreSuppressedIssue(entry);
        }
    }

    private void RestoreAllSuppressed_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RestoreAllSuppressedIssues();
    }

    private void SuppressedDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuppressedDataGrid.SelectedItem is SuppressedIssueEntry entry && !string.IsNullOrEmpty(entry.FilePath))
        {
            NavigateToFileAndLine(entry.FilePath, entry.Line);
        }
    }

    private void OpenExternalEditor_Click(object sender, RoutedEventArgs e)
    {
        int line = (CodeViewerListBox.SelectedItem as CodeLineViewModel)?.LineNumber ?? 1;
        ViewModel.OpenCurrentFileInExternalEditor(line);
    }

    private void OpenFileFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenCurrentFileFolder();
    }

    private void SearchAllMissingHeaders_Click(object sender, RoutedEventArgs e)
    {
        OpenMissingHeaderSearchWorkflow(onlySelectedNote: false);
    }

    private void SearchSelectedMissingHeader_Click(object sender, RoutedEventArgs e)
    {
        OpenMissingHeaderSearchWorkflow(onlySelectedNote: true);
    }

    private void OpenMissingHeaderSearchWorkflow(bool onlySelectedNote)
    {
        IEnumerable<AnalysisIssue> sourceNotes = ViewModel.NotesAndInfos;
        if (onlySelectedNote && NotesDataGrid.SelectedItem is AnalysisIssue selectedNote)
        {
            sourceNotes = new[] { selectedNote };
        }

        var grouped = new Dictionary<string, MissingHeaderSearchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in sourceNotes)
        {
            string? rawInc = MissingHeaderSearchItem.ExtractMissingHeaderName(note);
            if (string.IsNullOrWhiteSpace(rawInc))
                continue;

            if (!grouped.TryGetValue(rawInc, out var item))
            {
                item = new MissingHeaderSearchItem
                {
                    RawInclude = rawInc
                };
                grouped[rawInc] = item;
            }

            if (!string.IsNullOrWhiteSpace(note.FilePath) &&
                !item.ReferencedByFiles.Contains(note.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                item.ReferencedByFiles.Add(note.FilePath);
            }
        }

        // 만약 선택한 단일 행이 누락 헤더 노트가 아니면 전체 누락 헤더로 폴백
        if (grouped.Count == 0 && onlySelectedNote)
        {
            foreach (var note in ViewModel.NotesAndInfos)
            {
                string? rawInc = MissingHeaderSearchItem.ExtractMissingHeaderName(note);
                if (string.IsNullOrWhiteSpace(rawInc))
                    continue;

                if (!grouped.TryGetValue(rawInc, out var item))
                {
                    item = new MissingHeaderSearchItem
                    {
                        RawInclude = rawInc
                    };
                    grouped[rawInc] = item;
                }

                if (!string.IsNullOrWhiteSpace(note.FilePath) &&
                    !item.ReferencedByFiles.Contains(note.FilePath, StringComparer.OrdinalIgnoreCase))
                {
                    item.ReferencedByFiles.Add(note.FilePath);
                }
            }
        }

        if (grouped.Count == 0)
        {
            MessageBox.Show(
                this,
                "현재 노트 목록에 누락된 포함 파일(#include) 안내가 없습니다.\n정적 분석 실행 후 누락된 헤더가 있을 때 사용할 수 있습니다.",
                "누락 헤더 없음",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var folderDlg = new OpenFolderDialog
        {
            Title = $"누락된 헤더({grouped.Count}종)를 하위까지 탐색할 상위 폴더 선택"
        };

        if (folderDlg.ShowDialog(this) != true || !Directory.Exists(folderDlg.FolderName))
            return;

        var searchWin = new MissingHeaderSearchWindow(grouped.Values.ToList(), folderDlg.FolderName)
        {
            Owner = this
        };

        if (searchWin.ShowDialog() == true && searchWin.SelectedPathsToAdd.Count > 0)
        {
            foreach (var path in searchWin.SelectedPathsToAdd)
            {
                ViewModel.AddNonTargetPath(path);
            }

            ViewModel.StatusMessage =
                $"참조코드(비대상)에 헤더 파일 {searchWin.SelectedPathsToAdd.Count}개를 추가했습니다. 상단 [▶ 정적 분석 시작]을 다시 실행하면 누락 헤더가 반영됩니다.";
        }
    }
}
