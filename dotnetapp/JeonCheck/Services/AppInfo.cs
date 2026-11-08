namespace JeonCheck.Services;

// 프로그램 버전과 버전별 히스토리. 코드에 하드코딩한다.
// 버전 규칙: 3자리 표기. UI·문구 수정은 셋째 자리, 기능 추가는 둘째 자리, 첫째 자리는 별도 결정 시 올린다.
public static class AppInfo
{
    public const string Version = "v1.1.0";

    public sealed record HistoryEntry(string Version, string[] Added, string[] Changed);

    public static readonly IReadOnlyList<HistoryEntry> History = new List<HistoryEntry>
    {
        new("v1.1.0",
            new[]
            {
                "코드 뷰어 호버 툴팁: 함수 원형, 매크로 정의, 변수 타입 표시 (정보 없으면 팝업 없음)",
                "도움말 메뉴에 히스토리 창 추가",
            },
            new[]
            {
                "주석을 이탤릭(기울임글꼴)으로 표시",
                "미검사(Skipped) 줄의 줄 전체 이탤릭 제거 (회색 색상은 유지)",
                "버전을 3자리(v1.1.0)로 표기",
            }),
        new("v1.0",
            new[]
            {
                "Cppcheck + AST 기반 MISRA C 2012 및 암묵적 축소 형변환 검출",
                "Clang-Tidy (CERT-C / bugprone) 통합 분석 및 #include 자동 매칭",
                "코드 뷰어 라인 상태 표시, 규칙 설정, 예외 처리, 프로젝트 저장·불러오기",
            },
            Array.Empty<string>()),
    };
}
