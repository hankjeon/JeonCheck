using System.Text.Json.Serialization;

namespace JeonCheck.Models;

// 프로젝트 파일에 함께 저장하는 분석 결과 스냅샷.
// 결함·노트·줄 커버리지를 파일별로 보관하고, 파일별 해시로 변경 여부를 판별한다.
// 경로는 저장 시점에 상대 경로로 바꾸고 불러올 때 절대 경로로 되돌린다(DataRepository).
public class SnapshotLineRange
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }
}

public class SnapshotDefect
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Warning";

    [JsonPropertyName("obligation")]
    public string Obligation { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("isInfoOrNote")]
    public bool IsInfoOrNote { get; set; }
}

public class SnapshotCoverage
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("analyzed")]
    public List<SnapshotLineRange> Analyzed { get; set; } = new();

    [JsonPropertyName("skipped")]
    public List<SnapshotLineRange> Skipped { get; set; } = new();

    [JsonPropertyName("directives")]
    public List<SnapshotLineRange> Directives { get; set; } = new();
}

public class SnapshotSymbol
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty; // function, variable, macro

    [JsonPropertyName("tooltip")]
    public string Tooltip { get; set; } = string.Empty;
}

public class SnapshotFileResult
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("defects")]
    public List<SnapshotDefect> Defects { get; set; } = new();

    [JsonPropertyName("coverage")]
    public SnapshotCoverage? Coverage { get; set; }

    [JsonPropertyName("notes")]
    public List<SnapshotDefect> Notes { get; set; } = new();

    // 코드 뷰어 호버 팝업용 심볼(함수·변수·매크로). 없으면 해당 줄·이름에 팝업을 표시하지 않는다.
    [JsonPropertyName("symbols")]
    public List<SnapshotSymbol> Symbols { get; set; } = new();

    // 이 소스를 해석할 때 함께 읽은 헤더(덤프 토큰에 등장한 다른 파일). 헤더 변경 시 영향 범위 계산용.
    [JsonPropertyName("includes")]
    public List<string> Includes { get; set; } = new();
}

public class AnalysisSnapshot
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; set; }

    // 프리셋·C 표준·매크로·--force·Clang-Tidy·활성 룰셋·규칙 DB의 해시. 달라지면 전체 재분석.
    [JsonPropertyName("settingsHash")]
    public string SettingsHash { get; set; } = string.Empty;

    // 파일에 귀속되지 않는 전역 안내 노트
    [JsonPropertyName("globalNotes")]
    public List<SnapshotDefect> GlobalNotes { get; set; } = new();

    [JsonPropertyName("files")]
    public List<SnapshotFileResult> Files { get; set; } = new();

    // 검사 대상은 아니지만 분석 중 실제로 읽은 참조 헤더(지문만 보관, 결과 없음)
    [JsonPropertyName("extras")]
    public List<SnapshotFileResult> Extras { get; set; } = new();
}
