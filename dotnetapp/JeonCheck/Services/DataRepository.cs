using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class DataRepository
{
    private const int MaxRecentProjects = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string UserSettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JeonCheck",
            "user_settings.json");

    public List<TargetPreset> LoadTargetPresets()
    {
        string path = PathResolver.TargetsJsonPath;
        if (!File.Exists(path))
            return new List<TargetPreset>();

        string json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<TargetPresetList>(json, JsonOptions);
        return list?.Targets ?? new List<TargetPreset>();
    }

    public List<RuleInfo> LoadRules()
    {
        string path = PathResolver.RulesKoJsonPath;
        if (!File.Exists(path))
            return new List<RuleInfo>();

        string json = File.ReadAllText(path);
        var db = JsonSerializer.Deserialize<RuleDatabase>(json, JsonOptions);
        var rules = db?.Rules ?? new List<RuleInfo>();

        foreach (var r in rules)
        {
            r.IsEnabled = r.DefaultEnabled;
        }

        return rules;
    }

    public void SaveProject(string filePath, ProjectFileModel project)
    {
        string? projectDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var clone = new ProjectFileModel
        {
            ProjectName = project.ProjectName,
            Version = project.Version,
            SavedAt = DateTimeOffset.Now,
            TargetPresetId = project.TargetPresetId,
            CStandard = project.CStandard,
            CustomDefines = project.CustomDefines,
            ForceAllIfdefConfigs = project.ForceAllIfdefConfigs,
            EnableClangTidy = project.EnableClangTidy,
            TargetPaths = project.TargetPaths.Select(p => ToStoredPath(p, projectDir)).ToList(),
            NonTargetPaths = project.NonTargetPaths.Select(p => ToStoredPath(p, projectDir)).ToList(),
            ExcludedTargetPaths = project.ExcludedTargetPaths.Select(p => ToStoredPath(p, projectDir)).ToList(),
            ExcludedNonTargetPaths = project.ExcludedNonTargetPaths.Select(p => ToStoredPath(p, projectDir)).ToList(),
            EnabledRuleIds = new List<string>(project.EnabledRuleIds),
            DisabledRuleIds = project.DisabledRuleIds == null ? null : new List<string>(project.DisabledRuleIds),
            SuppressedIssues = project.SuppressedIssues.Select(s => new SuppressedIssueEntry
            {
                RuleId = s.RuleId,
                FilePath = ToStoredPath(s.FilePath, projectDir),
                Line = s.Line,
                Message = s.Message
            }).ToList()
        };

        string json = JsonSerializer.Serialize(clone, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public ProjectFileModel? LoadProject(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        string? projectDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        string json = File.ReadAllText(filePath);
        var project = JsonSerializer.Deserialize<ProjectFileModel>(json, JsonOptions);
        if (project == null)
            return null;

        project.TargetPaths = project.TargetPaths.Select(p => ToResolvedPath(p, projectDir)).ToList();
        project.NonTargetPaths = project.NonTargetPaths.Select(p => ToResolvedPath(p, projectDir)).ToList();
        project.ExcludedTargetPaths = (project.ExcludedTargetPaths ?? new()).Select(p => ToResolvedPath(p, projectDir)).ToList();
        project.ExcludedNonTargetPaths = (project.ExcludedNonTargetPaths ?? new()).Select(p => ToResolvedPath(p, projectDir)).ToList();
        foreach (var s in project.SuppressedIssues)
        {
            s.FilePath = ToResolvedPath(s.FilePath, projectDir);
        }

        return project;
    }

    public List<string> LoadRecentProjects()
    {
        try
        {
            string path = UserSettingsFilePath;
            if (!File.Exists(path))
                return new List<string>();

            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<UserSettingsModel>(json, JsonOptions);
            if (settings?.RecentProjects == null)
                return new List<string>();

            return settings.RecentProjects
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentProjects)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public void SaveRecentProjects(IEnumerable<string> recentPaths)
    {
        try
        {
            string path = UserSettingsFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var model = new UserSettingsModel
            {
                RecentProjects = recentPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => Path.GetFullPath(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRecentProjects)
                    .ToList()
            };

            string json = JsonSerializer.Serialize(model, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 사용자 AppData 저장 실패 시 무시
        }
    }

    private sealed class UserSettingsModel
    {
        public List<string> RecentProjects { get; set; } = new();
    }

    private static string ToStoredPath(string fullPath, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(fullPath))
            return fullPath;

        try
        {
            string rel = Path.GetRelativePath(baseDir, fullPath);
            if (!rel.StartsWith("..\\..\\..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                return rel;
        }
        catch
        {
            // 다른 드라이브 등 상대 경로 변환 불가 시 절대 경로 유지
        }

        return fullPath;
    }

    private static string ToResolvedPath(string storedPath, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return storedPath;

        if (Path.IsPathRooted(storedPath) || string.IsNullOrWhiteSpace(baseDir))
            return Path.GetFullPath(storedPath);

        return Path.GetFullPath(Path.Combine(baseDir, storedPath));
    }
}
