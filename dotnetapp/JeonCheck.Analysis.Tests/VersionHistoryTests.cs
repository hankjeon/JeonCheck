using JeonCheck.Services;

static class VersionHistoryTests
{
    public static void Run(Action<string, Action> test)
    {
        test("버전 이력: 기존 세 버전의 이관·현재 버전 독립성", () =>
        {
            var result = VersionHistory.Load();
            Require(result.Message == "", result.Message);
            Require(result.Entries.Select(e => e.Version).SequenceEqual(new[] { "v1.2.0", "v1.1.0", "v1.0" }), "버전 누락/순서 변경");
            string[][] expectedAdded =
            {
                new[]
                {
                    "코드 한 줄 안에서 문자 드래그·더블 클릭 단어 선택·더블 클릭 후 단어 단위 드래그 선택",
                    "선택 영역 우클릭으로 검색 탭에 검색어 입력 (검색 버튼으로 실행)",
                    "프로젝트 문구 검색: 범위 선택, 정규식·대소문자·단어 전체 일치, 취소와 결과 위치 이동·강조"
                },
                new[]
                {
                    "코드 뷰어 호버 툴팁: 함수 원형, 매크로 정의, 변수 타입 표시 (정보 없으면 팝업 없음)",
                    "도움말 메뉴에 히스토리 창 추가"
                },
                new[]
                {
                    "Cppcheck + AST 기반 MISRA C 2012 및 암묵적 축소 형변환 검출",
                    "Clang-Tidy (CERT-C / bugprone) 통합 분석 및 #include 자동 매칭",
                    "코드 뷰어 라인 상태 표시, 규칙 설정, 예외 처리, 프로젝트 저장·불러오기"
                }
            };
            for (int i = 0; i < expectedAdded.Length; i++)
                Require(result.Entries[i].Added.SequenceEqual(expectedAdded[i]), $"{result.Entries[i].Version} 추가 이력 유실");
            Require(result.Entries[1].Changed.SequenceEqual(new[]
            {
                "주석을 이탤릭(기울임글꼴)으로 표시",
                "미검사(Skipped) 줄의 줄 전체 이탤릭 제거 (회색 색상은 유지)",
                "버전을 3자리(v1.1.0)로 표기"
            }), "v1.1.0 수정 이력 유실");
            Require(result.Entries[2].Changed.Length == 0, "v1.0 수정 이력 변경");
            Require(result.Entries.All(e => e.ReleaseDate == null && e.ReleaseDateDisplay == "출시일 미등록"), "출시일 임의 지정");
            Require(AppInfo.Version == "v1.2.0", "앱 버전 유지");
        });
        test("버전 이력: 파일 순서·출시일 표시와 창 재오픈 시 재로드", () => WithPath(path =>
        {
            File.WriteAllText(path, """
                {"versions":[
                  {"version":"v0.1","releaseDate":"2024-02-29","added":[],"changed":["첫 수정"]},
                  {"version":"v9.0","releaseDate":null,"added":["다음 추가"],"changed":[]}
                ]}
                """);
            var first = VersionHistory.Load(path);
            Require(first.Message == "" && first.Entries[0].Version == "v0.1", "파일 순서 미보존");
            Require(first.Entries[0].ReleaseDateDisplay == "출시일: 2024-02-29", "날짜 표시");
            File.WriteAllText(path, """{"versions":[{"version":"v99.0","releaseDate":null,"added":["수정 반영"],"changed":[]}]}""");
            var second = VersionHistory.Load(path);
            Require(second.Entries.Single().Added.Single() == "수정 반영", "캐시로 인해 편집 미반영");
            Require(AppInfo.Version == "v1.2.0", "JSON이 앱 버전을 바꿈");
        }));
        test("버전 이력: 누락 파일·디렉터리 경로도 오류 안내로 반환", () => WithPath(path =>
        {
            foreach (string missing in new[] { path, Path.GetDirectoryName(path)! })
            {
                var result = VersionHistory.Load(missing);
                Require(result.Entries.Count == 0 && result.Message.Contains("불러오지 못했습니다") && result.Message.Contains(missing), "누락/읽기 실패 안내");
            }
        }));
        test("버전 이력: 잘못된 JSON·필수 필드·날짜 오류는 전체 안내", () => WithPath(path =>
        {
            foreach (string json in new[]
            {
                "{", "null", "{}", "[]", "{\"versions\":null}", "{\"versions\":[null]}",
                """{"versions":[{"version":" ","added":[],"changed":[]}]}""",
                """{"versions":[{"version":"v1","added":null,"changed":[]}]}""",
                """{"versions":[{"version":"v1","added":[],"changed":[null]}]}""",
                """{"versions":[{"version":"v1","added":[" "],"changed":[]}]}""",
                """{"versions":[{"version":"v1","added":[],"changed":[],"releaseDate":"2025-02-29"}]}""",
                """{"versions":[{"version":"v1","added":[],"changed":[],"releaseDate":"2026/11/09"}]}""",
                """{"versions":[{"version":"v1","added":[],"changed":[],"releaseDate":42}]}"""
            })
            {
                File.WriteAllText(path, json);
                var result = VersionHistory.Load(path);
                Require(result.Entries.Count == 0 && result.Message.Contains("불러오지 못했습니다"), "오류 JSON 허용: " + json);
            }
        }));
        test("버전 이력: 빈 목록과 생략된 출시일", () => WithPath(path =>
        {
            File.WriteAllText(path, "{\"versions\":[]}");
            var empty = VersionHistory.Load(path);
            Require(empty.Entries.Count == 0 && empty.Message.Contains("등록된 버전 이력이 없습니다"), "빈 목록 안내");
            File.WriteAllText(path, """{"versions":[{"version":"v1","added":[],"changed":[]}]}""");
            var result = VersionHistory.Load(path);
            Require(result.Message == "" && result.Entries.Single().ReleaseDateDisplay == "출시일 미등록", "출시일 선택 필드");
        }));
        test("버전 이력: 실행파일 기준 외부 data 경로", () =>
        {
            Require(VersionHistory.DataFilePath == Path.Combine(AppContext.BaseDirectory, "data", "version_history.json"), "작업 디렉터리 의존");
            Require(File.Exists(VersionHistory.DataFilePath), "출력 폴더에 JSON 누락");
        });
    }

    private static void WithPath(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jc_history_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "history.json");
        try { action(path); }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(dir);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
