using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace JeonCheck.Models;

public class RuleDatabase
{
    [JsonPropertyName("rules")]
    public List<RuleInfo> Rules { get; set; } = new();
}

public class RuleInfo : INotifyPropertyChanged
{
    private bool _isEnabled = true;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Warning";

    [JsonPropertyName("obligation")]
    public string Obligation { get; set; } = "필수(Required)";

    [JsonPropertyName("defaultEnabled")]
    public bool DefaultEnabled { get; set; } = true;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("badExample")]
    public string BadExample { get; set; } = string.Empty;

    [JsonPropertyName("goodExample")]
    public string GoodExample { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsUnregistered { get; init; }

    public static RuleInfo CreateUnregistered(string id, string severity = "Warning", string engine = "분석 도구") => new()
    {
        Id = id,
        Category = engine == "Cppcheck" ? "Cppcheck 기본 검사" : engine == "Clang-Tidy" ? "Clang-Tidy / CERT-C" : "미등록 진단",
        Severity = severity,
        Obligation = "미분류",
        Title = $"{engine} 검출 ({id})",
        Summary = "개별 한글 해설이 아직 등록되지 않은 진단입니다. 원문과 해당 코드의 타입·값·실행 경로를 확인하십시오.",
        // 발생 위치마다 다른 원문은 AnalysisIssue.Message에 보존한다.
        Description = $"이 {engine} 세부 규칙의 개별 한글 해설과 코드 예시는 아직 등록되지 않았습니다. 선택한 항목의 검출 메시지에 표시된 원문에서 문제가 된 연산과 발생 조건을 확인한 뒤 원인에 맞게 수정하십시오.",
        IsUnregistered = true,
        DefaultEnabled = true,
        IsEnabled = true
    };

    [JsonIgnore]
    public string SeverityDisplay => Severity switch
    {
        "Error" => "오류",
        "Warning" => "경고",
        "Style" => "권고",
        "Info" or "Information" => "안내",
        _ => Severity
    };

    [JsonIgnore]
    public string ObligationDisplay => FormatObligation(Id, Obligation);

    [JsonIgnore]
    public string BadExampleDisplay => string.IsNullOrWhiteSpace(BadExample)
        ? "이 규칙의 위반 코드 예시는 아직 등록되지 않았습니다. 상세 설명과 원문 진단을 확인하십시오."
        : BadExample;

    [JsonIgnore]
    public string GoodExampleDisplay => string.IsNullOrWhiteSpace(GoodExample)
        ? "이 규칙의 개선 코드 예시는 아직 등록되지 않았습니다. 실제 코드의 원인에 맞는 수정이 필요합니다."
        : GoodExample;

    public static string FormatObligation(string ruleId, string obligation)
    {
        if (obligation == "참고(Note)")
            return "참고 안내";
        if (ruleId.StartsWith("misra-c2012-", StringComparison.OrdinalIgnoreCase))
            return $"MISRA 의무: {obligation}";

        // Cppcheck/CERT/자체 규칙의 프로젝트 검토 정책을 MISRA 의무 등급과 구분한다.
        string policy = obligation switch
        {
            "치명(Critical)" => "우선 검토",
            "필수(Required)" or "강제(Mandatory)" => "필수 검토",
            "권고(Advisory)" => "권고 검토",
            _ => obligation
        };
        return $"프로젝트 정책: {policy}";
    }

    [JsonIgnore]
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
