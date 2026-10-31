using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;
using JeonCheck.Views;

namespace JeonCheck;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        DarkTitleBarHelper.Apply(this);
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
        var dlg = new SaveFileDialog
        {
            Title = "전검사 프로젝트 파일 저장",
            Filter = "전검사 프로젝트 (*.jcproj)|*.jcproj",
            DefaultExt = ".jcproj",
            FileName = "embedded_analysis.jcproj"
        };

        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.SaveProjectToFile(dlg.FileName);
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
}
