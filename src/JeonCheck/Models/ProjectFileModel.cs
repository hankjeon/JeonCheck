using System.Text.Json.Serialization;

namespace JeonCheck.Models;

public class ProjectFileModel
{
    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "새 정적분석 프로젝트";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("targetPresetId")]
    public string TargetPresetId { get; set; } = "ti-c6678-eabi";

    [JsonPropertyName("cStandard")]
    public string CStandard { get; set; } = "c99";

    [JsonPropertyName("customDefines")]
    public string CustomDefines { get; set; } = string.Empty;

    [JsonPropertyName("forceAllIfdefConfigs")]
    public bool ForceAllIfdefConfigs { get; set; } = false;

    [JsonPropertyName("enableClangTidy")]
    public bool EnableClangTidy { get; set; } = true;

    [JsonPropertyName("targetPaths")]
    public List<string> TargetPaths { get; set; } = new();

    [JsonPropertyName("nonTargetPaths")]
    public List<string> NonTargetPaths { get; set; } = new();

    /// <summary>검사 대상 폴더 안에서 사용자가 제거하거나 비대상으로 옮긴 하위 파일/폴더 경로</summary>
    [JsonPropertyName("excludedTargetPaths")]
    public List<string> ExcludedTargetPaths { get; set; } = new();

    /// <summary>비대상(참조) 폴더 안에서 사용자가 제거하거나 검사 대상으로 옮긴 하위 파일/폴더 경로</summary>
    [JsonPropertyName("excludedNonTargetPaths")]
    public List<string> ExcludedNonTargetPaths { get; set; } = new();

    /// <summary>활성화(ON)된 규칙 ID 목록 (이전 버전 호환용으로 함께 저장)</summary>
    [JsonPropertyName("enabledRuleIds")]
    public List<string> EnabledRuleIds { get; set; } = new();

    /// <summary>
    /// 사용자가 끈(OFF) 규칙 ID 목록. 불러올 때 enabledRuleIds와 이 목록 어디에도 없는 규칙
    /// (저장 이후 rules_ko.json에 새로 추가된 규칙)은 규칙 DB의 기본값(defaultEnabled)을 따른다. null이면 이전 버전 파일.
    /// </summary>
    [JsonPropertyName("disabledRuleIds")]
    public List<string>? DisabledRuleIds { get; set; }

    [JsonPropertyName("suppressedIssues")]
    public List<SuppressedIssueEntry> SuppressedIssues { get; set; } = new();
}

public class SuppressedIssueEntry
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public string SuppressionKey => $"{RuleId}|{AnalysisIssue.NormalizePath(FilePath)}|{Line}";
}
