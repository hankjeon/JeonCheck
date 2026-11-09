namespace JeonCheck.Services;

// 실행 중인 앱 버전. 변경 이력 JSON의 내용과 독립적으로 유지한다.
// 버전 규칙: 3자리 표기. UI·문구 수정은 셋째 자리, 기능 추가는 둘째 자리, 첫째 자리는 별도 결정 시 올린다.
public static class AppInfo
{
    public const string Version = "v1.2.0";
}
