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
