using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace JeonCheck.Models;

public class SourceTreeNode : INotifyPropertyChanged
{
    private int _defectCount;
    private bool _isExpanded = true;
    private bool _isSelected;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool IsTarget { get; set; }
    public SourceTreeNode? Parent { get; set; }

    public ObservableCollection<SourceTreeNode> Children { get; } = new();

    public string IconText => IsDirectory ? "📁" : (FullPath.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ? "📘" : "📄");

    public int DefectCount
    {
        get => _defectCount;
        set
        {
            if (_defectCount != value)
            {
                _defectCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDefects));
                OnPropertyChanged(nameof(DefectBadgeText));
            }
        }
    }

    public bool HasDefects => _defectCount > 0;
    public string DefectBadgeText => _defectCount > 0 ? $"🔴 {_defectCount}" : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

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

    public IEnumerable<SourceTreeNode> EnumerateFiles()
    {
        if (!IsDirectory)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var file in child.EnumerateFiles())
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// 백그라운드 분석에 넘기기 위한 트리 스냅샷 (분석 중 UI에서 트리를 편집해도 영향 없음)
    /// </summary>
    public SourceTreeNode CloneTree(SourceTreeNode? parent = null)
    {
        var clone = new SourceTreeNode
        {
            Name = Name,
            FullPath = FullPath,
            IsDirectory = IsDirectory,
            IsTarget = IsTarget,
            Parent = parent
        };
        foreach (var child in Children)
            clone.Children.Add(child.CloneTree(clone));
        return clone;
    }

    public static SourceTreeNode CreateFromPath(string path, bool isTarget, HashSet<string>? excludedPaths = null)
    {
        string fullPath = Path.GetFullPath(path);
        bool isDir = Directory.Exists(fullPath);

        var node = new SourceTreeNode
        {
            Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            FullPath = fullPath,
            IsDirectory = isDir,
            IsTarget = isTarget
        };

        if (isDir)
        {
            try
            {
                foreach (var subDir in Directory.GetDirectories(fullPath).OrderBy(d => d))
                {
                    string normalizedSubDir = Path.GetFullPath(subDir);
                    if (excludedPaths != null && excludedPaths.Contains(normalizedSubDir))
                        continue;

                    var childDir = CreateFromPath(normalizedSubDir, isTarget, excludedPaths);
                    if (childDir.Children.Count > 0)
                    {
                        childDir.Parent = node;
                        node.Children.Add(childDir);
                    }
                }

                var allowedExts = new[] { ".c", ".h", ".cpp", ".hpp" };
                foreach (var file in Directory.GetFiles(fullPath).OrderBy(f => f))
                {
                    string ext = Path.GetExtension(file);
                    if (!allowedExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        continue;

                    string normalizedFile = Path.GetFullPath(file);
                    if (excludedPaths != null && excludedPaths.Contains(normalizedFile))
                        continue;

                    node.Children.Add(new SourceTreeNode
                    {
                        Name = Path.GetFileName(normalizedFile),
                        FullPath = normalizedFile,
                        IsDirectory = false,
                        IsTarget = isTarget,
                        Parent = node
                    });
                }
            }
            catch
            {
                // 접근 권한 없는 하위 폴더 무시
            }
        }

        return node;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
