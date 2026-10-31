using System.IO;

namespace JeonCheck.Models;

public class AnalysisIssue
{
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning"; // Error, Warning, Style, Info
    public string Obligation { get; set; } = "필수(Required)";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? "-" : Path.GetFileName(FilePath);
    public int Line { get; set; }
    public int Column { get; set; }
    public bool IsInfoOrNote { get; set; }
    public RuleInfo? RuleDetails { get; set; }

    public string SeverityDisplay => Severity switch
    {
        "Error" => "오류",
        "Warning" => "경고",
        "Style" => "권고",
        "Information" or "Info" => "안내",
        _ => Severity
    };

    public string SuppressionKey => $"{RuleId}|{ NormalizePath(FilePath) }|{Line}";

    /// <summary>
    /// 결함 중복 제거 키 (규칙 + 파일 + 줄 + 열). 같은 위치의 결함만 하나로 합치고,
    /// 같은 줄이라도 열이 다르면(예: 한 호출의 두 인자) 별개의 결함으로 유지한다.
    /// </summary>
    public static string MakePositionKey(string ruleId, string filePath, int line, int column)
        => $"{ruleId}|{NormalizePath(filePath)}|{line}|{column}";

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
