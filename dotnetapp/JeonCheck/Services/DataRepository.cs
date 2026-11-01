using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using JeonCheck.Models;

namespace JeonCheck.Services;

public class DataRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
