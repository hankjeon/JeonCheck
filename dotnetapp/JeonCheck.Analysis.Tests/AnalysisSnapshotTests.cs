using JeonCheck.Models;
using JeonCheck.Services;

static class AnalysisSnapshotTests
{
    public static void Run(Action<string, Action> test)
    {
        test("줄 구간 압축과 복원", () =>
        {
            var ranges = AnalysisSnapshotStore.ToRanges(new[] { 1, 2, 3, 5, 7, 8, 9 });
            Require(ranges.Count == 3, "구간 개수 오류");
            Require(ranges[0].Start == 1 && ranges[0].End == 3, "첫 구간 오류");
            Require(ranges[1].Start == 5 && ranges[1].End == 5, "단일 구간 오류");
            Require(ranges[2].Start == 7 && ranges[2].End == 9, "마지막 구간 오류");
            var restored = AnalysisSnapshotStore.FromRanges(ranges);
            Require(restored.SetEquals(new[] { 1, 2, 3, 5, 7, 8, 9 }), "구간 복원 오류");
            Require(AnalysisSnapshotStore.FromRanges(null).Count == 0, "null 구간 처리 오류");
            Require(AnalysisSnapshotStore.ToRanges(Array.Empty<int>()).Count == 0, "빈 입력 처리 오류");
        });

        test("설정 해시는 입력 변화에만 반응", () =>
        {
            string baseHash = Hash(new[] { "a" }, new[] { "-D1" });
            Require(Hash(new[] { "a" }, new[] { "-D1" }) == baseHash, "동일 입력 해시 불일치");
            Require(Hash(new[] { "b" }, new[] { "-D1" }) != baseHash, "룰셋 변경 미감지");
            Require(Hash(new[] { "a" }, new[] { "-D2" }) != baseHash, "매크로 변경 미감지");
            Require(Hash(new[] { "a" }, new[] { " -D1 " }) == baseHash, "매크로 공백 정규화 오류");
        });

        test("파일 해시는 내용 변화 감지", () =>
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "int a;");
                string? h1 = AnalysisSnapshotStore.ComputeFileHash(path);
                Require(!string.IsNullOrEmpty(h1), "해시 생성 실패");
                File.WriteAllText(path, "int ab;");
                Require(AnalysisSnapshotStore.ComputeFileHash(path) != h1, "내용 변경 미감지");
                Require(AnalysisSnapshotStore.ComputeFileHash(path + ".none") == null, "없는 파일 처리 오류");
            }
            finally { File.Delete(path); }
        });

        test("예외 항목 코드 기준 재고정", () =>
        {
            var entries = new List<SuppressedIssueEntry>
            {
                new() { RuleId = "r1", FilePath = "f.c", Line = 2, CodeText = "int a;" },
                new() { RuleId = "r2", FilePath = "f.c", Line = 3, CodeText = "return 0;" },
                new() { RuleId = "r3", FilePath = "gone.c", Line = 1, CodeText = "int x;" },
            };
            string[] lines = new[] { "// 추가", "int a;", "int b;", "int c;", "return 0;" };
            var summary = AnalysisSnapshotStore.ReanchorSuppressions(entries, p => p == "f.c" ? lines : null);
            Require(entries[0].Line == 2 && !entries[0].NeedsReview, "그대로인 항목 오류");
            Require(entries[1].Line == 5 && !entries[1].NeedsReview, "이동 항목 재고정 오류");
            Require(entries[2].NeedsReview, "없는 파일 미표시");
            Require(summary.Reanchored == 1 && summary.NeedsReview == 1, "재고정 집계 오류");

            var old = new List<SuppressedIssueEntry> { new() { RuleId = "r", FilePath = "f.c", Line = 9 } };
            AnalysisSnapshotStore.ReanchorSuppressions(old, _ => lines);
            Require(!old[0].NeedsReview && old[0].Line == 9, "코드 없는 구 항목 변경");
        });

        test("증분 병합은 해당 파일만 교체", () =>
        {
            var defects = new List<AnalysisIssue>
            {
                new() { RuleId = "r1", FilePath = "a.c", Line = 1 },
                new() { RuleId = "r2", FilePath = "b.c", Line = 2 },
            };
            string keyA = AnalysisIssue.NormalizePath("a.c");
            string keyB = AnalysisIssue.NormalizePath("b.c");
            var coverage = new Dictionary<string, FileLineCoverage>
            {
                [keyA] = new() { AnalyzedLines = new() { 1, 2 } },
                [keyB] = new() { AnalyzedLines = new() { 1 } },
            };
            var notes = new List<AnalysisIssue>
            {
                new() { RuleId = "n1", FilePath = "a.c", Line = 1 },
                new() { RuleId = "g1", FilePath = string.Empty },
            };
            var fresh = new AnalysisRunResult
            {
                Defects = new() { new() { RuleId = "r3", FilePath = "A.C", Line = 10 } },
                CoverageByFile = new() { [keyA] = new() { AnalyzedLines = new() { 10 } } },
                NotesAndInfos = new() { new() { RuleId = "n2", FilePath = "a.c" } },
            };
            AnalysisSnapshotStore.ReplaceFileResults(defects, coverage, notes,
                new HashSet<string> { keyA }, fresh, replaceGlobalNotes: false);
            Require(defects.Count == 2 && defects.Exists(d => d.RuleId == "r2") && defects.Exists(d => d.RuleId == "r3"), "결함 병합 오류");
            Require(coverage[keyA].AnalyzedLines.SetEquals(new[] { 10 }) && coverage.ContainsKey(keyB), "커버리지 병합 오류");
            Require(notes.Exists(n => n.RuleId == "g1") && notes.Exists(n => n.RuleId == "n2") && !notes.Exists(n => n.RuleId == "n1"), "노트 병합 오류");
        });

        test("프로젝트 저장·불러오기 스냅샷 왕복", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = new DataRepository();
                string proj = Path.Combine(dir, "t.jcproj");
                string src = Path.Combine(dir, "a.c");
                var model = new ProjectFileModel
                {
                    ProjectName = "t",
                    TargetPaths = new() { src },
                    EnabledRuleIds = new() { "r1" },
                    SuppressedIssues = new() { new() { RuleId = "r1", FilePath = src, Line = 3, CodeText = "x;" } },
                    SavedAnalysis = new AnalysisSnapshot
                    {
                        SettingsHash = "h",
                        Files = new() { new SnapshotFileResult
                        {
                            FilePath = src, Hash = "hh", Length = 10,
                            Defects = new() { new SnapshotDefect { RuleId = "r1", FilePath = src, Line = 3 } },
                            Coverage = new SnapshotCoverage
                            {
                                FilePath = src,
                                Analyzed = new() { new SnapshotLineRange { Start = 1, End = 5 } }
                            },
                            Notes = new() { new SnapshotDefect { RuleId = "n", FilePath = src } },
                            Includes = new() { Path.Combine(dir, "b.h") }
                        } },
                        GlobalNotes = new() { new SnapshotDefect { RuleId = "g" } }
                    }
                };
                repo.SaveProject(proj, model);
                string raw = File.ReadAllText(proj);
                Require(!raw.Contains(dir), "절대 경로가 저장됨");
                var loaded = repo.LoadProject(proj)!;
                Require(loaded.SavedAnalysis != null, "스냅샷 유실");
                var f = loaded.SavedAnalysis!.Files[0];
                Require(f.FilePath == src, "파일 경로 복원 오류");
                Require(f.Defects.Count == 1 && f.Defects[0].Line == 3, "결함 유실");
                Require(f.Coverage != null && f.Coverage.Analyzed.Count == 1 && f.Coverage.Analyzed[0].End == 5, "커버리지 유실");
                Require(f.Notes.Count == 1 && f.Includes.Count == 1 && f.Includes[0] == Path.Combine(dir, "b.h"), "노트·포함 경로 유실");
                Require(loaded.SavedAnalysis.GlobalNotes.Count == 1, "전역 노트 유실");
                Require(loaded.SuppressedIssues.Count == 1 && loaded.SuppressedIssues[0].CodeText == "x;", "예외 항목 유실");
            }
            finally { Directory.Delete(dir, true); }
        });

        static string Hash(string[] rules, string[] defines) => AnalysisSnapshotStore.ComputeSettingsHash(
            "ti-c6678-eabi", "platform.xml", "c99", defines, false, true, rules, "db");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
