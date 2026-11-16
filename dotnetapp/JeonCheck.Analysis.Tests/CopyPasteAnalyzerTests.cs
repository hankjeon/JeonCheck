using System.Reflection;
using JeonCheck.Models;
using JeonCheck.Services;
using JeonCheck.ViewModels;

static class CopyPasteAnalyzerTests
{
    public static void Run(Action<string, Action> test)
    {
        test("copyPasteMacroCommentMismatch: 실제 Cppcheck + rawtokens로 주석/매크로 불일치 검출 및 uint32_t 오탐 방지", () =>
        {
            const string code = """
                #include <stdint.h>
                #define UART_CH1_BASE 0x40001000U
                #define UART_CH2_BASE 0x40002000U
                void uart_set_baud(uint32_t base, uint32_t baud);
                void uart_write_sync(uint32_t base, uint32_t data);

                void init_uarts(uint32_t tx_buf1, uint32_t tx_buf2) {
                    /* UART1 채널 초기화 및 출력 */
                    uart_set_baud(UART_CH1_BASE, 115200U);
                    uart_write_sync(UART_CH1_BASE, tx_buf1);

                    /* UART2 채널 초기화 및 출력 */
                    uart_set_baud(UART_CH1_BASE, 115200U);
                    uart_write_sync(UART_CH1_BASE, tx_buf2);
                }

                void init_ok(uint32_t rx1, uint32_t rx2) {
                    /* CH1 정상 초기화 */
                    uart_set_baud(UART_CH1_BASE, rx1);
                    /* CH2 정상 초기화 */
                    uart_set_baud(UART_CH2_BASE, rx2);
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleMacroCommentMismatch).ToList();
            Require(issues.Count >= 1, "주석(UART1->UART2) 대비 UART_CH1_BASE 미수정 미검출");
            Require(issues.All(i => i.Line is >= 12 and <= 15), $"정상 블록(init_ok) 또는 잘못된 위치 오탐: {string.Join(",", issues.Select(i => i.Line))}");
            Require(issues.Any(i => i.Title.Contains("주석·매크로") && i.Message.Contains("UART_CH1_BASE")), "한글 설명 또는 매크로명 누락");
        });

        test("copyPasteMacroCommentMismatch: 인라인 주석/한글 장치 번호 및 표준 타입명 제외", () =>
        {
            const string code = """
                #include <stdint.h>
                #define DEV1_ADDR 0x1000U
                #define DEV2_ADDR 0x2000U
                void dev_cfg(uint32_t addr, uint32_t val);

                void test_inline(void) {
                    uint32_t v1 = (uint32_t)10U;
                    uint32_t v2 = (uint32_t)20U;
                    dev_cfg(DEV1_ADDR, v1); // 장치 1 설정
                    dev_cfg(DEV1_ADDR, v2); // 장치 2 설정
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleMacroCommentMismatch).ToList();
            Require(issues.Count == 1 && issues[0].Line == 10, $"장치 1->2 인라인 주석 불일치 검출 실패 (count={issues.Count})");
        });

        test("copyPasteSequenceMismatch: 배열 인덱스 및 매크로 짝 불일치 검출 + 고정 상수 초기화 오탐 방지", () =>
        {
            const string code = """
                #define ADC_CH0 0
                #define ADC_CH1 1
                #define PIN1 1
                #define PIN2 2
                #define MODE1 10
                int read_adc(int ch);
                void set_pin(int pin, int mode);

                void test_seq(int *b, int *def) {
                    int arr[4];
                    arr[0] = b[0];
                    arr[1] = b[1];
                    arr[2] = b[1];

                    int ch0 = read_adc(ADC_CH0);
                    int ch1 = read_adc(ADC_CH0);

                    /* 음성(정상) 사례: 고정 상수 0 초기화, 기본값 def[0] 복사, 공유 모드 상수 MODE1 */
                    int z[2];
                    z[0] = 0;
                    z[1] = 0;

                    int d[2];
                    d[0] = def[0];
                    d[1] = def[0];

                    set_pin(PIN1, MODE1);
                    set_pin(PIN2, MODE1);
                    (void)arr; (void)ch0; (void)ch1; (void)z; (void)d;
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleSequenceMismatch).ToList();
            Require(issues.Any(i => i.Line == 13), "arr[2] = b[1] 연속 인덱스 불일치 미검출");
            Require(issues.Any(i => i.Line == 16), "ch1 = read_adc(ADC_CH0) 매크로 짝 불일치 미검출");
            Require(!issues.Any(i => i.Line >= 19), $"고정 상수 초기화/MODE1 오탐 발생: {string.Join(",", issues.Select(i => i.Line))}");
        });

        test("consecutiveTargetOverwrite: 구조체·포인터·배열 연속 덮어쓰기 검출 + volatile/읽기/Cppcheck 중복 억제", () =>
        {
            const string code = """
                typedef struct { int ch1_pwm; int ch2_pwm; volatile int hw_reg; } Ctrl;
                void use_ctrl(Ctrl *c);
                Ctrl g_ctrl;
                int g_buf[4];

                void test_overwrite(Ctrl *p) {
                    Ctrl c;
                    int buf[4];
                    int x;

                    g_ctrl.ch1_pwm = 10;
                    g_ctrl.ch1_pwm = 20;

                    p->ch2_pwm = 1;
                    p->ch2_pwm = 2;

                    g_buf[0] = 5;
                    g_buf[0] = 6;

                    /* Cppcheck redundantAssignment가 보고하는 단순 변수는 중복 보고하지 않아야 함 */
                    x = 1;
                    x = 2;

                    /* 음성 사례 1: volatile 멤버 연속 쓰기 */
                    c.hw_reg = 1;
                    c.hw_reg = 2;

                    /* 음성 사례 2: 우변에서 좌변 읽기 */
                    buf[1] = 1;
                    buf[1] = buf[1] + 1;

                    /* 음성 사례 3: 중간 함수에서 주소 전달 */
                    c.ch2_pwm = 100;
                    use_ctrl(&c);
                    c.ch2_pwm = 200;
                    (void)x;
                }
                """;

            var result = RunWithCppcheck(code);
            var overwrites = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleConsecutiveOverwrite).ToList();
            var redundant = result.Defects.Where(d => d.RuleId == "redundantAssignment").ToList();

            Require(result.Defects.Any(i => i.Line == 12 && i.RuleId is CopyPasteAnalyzer.RuleConsecutiveOverwrite or "redundantAssignment"),
                "전역 구조체 멤버 g_ctrl.ch1_pwm 연속 덮어쓰기 미검출");
            Require(result.Defects.Any(i => i.Line == 15 && i.RuleId is CopyPasteAnalyzer.RuleConsecutiveOverwrite or "redundantAssignment"),
                "포인터 멤버 p->ch2_pwm 연속 덮어쓰기 미검출");
            Require(result.Defects.Any(i => i.Line == 18 && i.RuleId is CopyPasteAnalyzer.RuleConsecutiveOverwrite or "redundantAssignment"),
                "전역 배열 상수 인덱스 g_buf[0] 연속 덮어쓰기 미검출");
            Require(!overwrites.Any(i => i.Line == 26), "volatile 멤버 연속 쓰기 오탐");
            Require(!overwrites.Any(i => i.Line == 30), "우변에서 좌변 읽기(buf[1] = buf[1] + 1) 오탐");
            Require(!overwrites.Any(i => i.Line == 35), "중간 use_ctrl(&c) 호출 후 대입 오탐");

            foreach (var r in redundant)
            {
                Require(!overwrites.Any(o => o.Line == r.Line), $"Cppcheck redundantAssignment와 {r.Line}행 중복 보고됨");
            }

            // redundantAssignment 규칙을 비활성화하면 구조체·포인터·배열·변수 모두 consecutiveTargetOverwrite로 검출되고 음성 사례는 제외됨을 확인
            var repo = new DataRepository();
            var rulesNoRedundant = repo.LoadRules();
            var ra = rulesNoRedundant.FirstOrDefault(r => r.Id == "redundantAssignment");
            if (ra != null) ra.IsEnabled = false;
            var resultNoRedundant = RunWithCppcheck(code, rulesNoRedundant);
            var directOverwrites = resultNoRedundant.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleConsecutiveOverwrite).ToList();
            Require(directOverwrites.Any(d => d.Line == 12), "g_ctrl.ch1_pwm consecutiveTargetOverwrite 미검출");
            Require(directOverwrites.Any(d => d.Line == 15), "p->ch2_pwm consecutiveTargetOverwrite 미검출");
            Require(directOverwrites.Any(d => d.Line == 18), "g_buf[0] consecutiveTargetOverwrite 미검출");
            Require(directOverwrites.Any(d => d.Line == 22), "x consecutiveTargetOverwrite 미검출");
            Require(!directOverwrites.Any(d => d.Line is 26 or 30 or 35),
                $"음성 사례 오탐: {string.Join(",", directOverwrites.Select(d => d.Line))}");
        });

        test("conditionBodyMismatch: NULL 가드 불일치, 중복 else if, 범위 비교 변수 혼동", () =>
        {
            const string code = """
                #define MIN_X 0
                #define MAX_X 100
                #define MIN_Y 0
                #define MAX_Y 200
                typedef struct { int len; } Pkt;

                int test_cond(Pkt *rx_pkt, Pkt *tx_pkt, Pkt *global_cfg, int cmd, int x, int y) {
                    int out = 0;

                    if (rx_pkt != 0) {
                        tx_pkt->len = 0;
                    }

                    if (rx_pkt) {
                        *tx_pkt = *tx_pkt;
                    }

                    /* 음성: 이름 유사하지 않은 별도 전역 설정 변경 */
                    if (rx_pkt != 0) {
                        global_cfg->len = 1;
                    }

                    /* 음성: 정상 가드 */
                    if (tx_pkt != 0) {
                        tx_pkt->len = 2;
                    }

                    if (cmd == 1) {
                        out = 10;
                    } else if (cmd == 2) {
                        out = 20;
                    } else if (cmd == 1) {
                        out = 30;
                    }

                    if (x >= MIN_X && x <= MAX_X && y >= MIN_Y && x <= MAX_Y) {
                        out += 1;
                    }

                    /* 음성: 정상 범위 검사 */
                    if (x >= MIN_X && x <= MAX_X && y >= MIN_Y && y <= MAX_Y) {
                        out += 2;
                    }
                    return out;
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleConditionBodyMismatch).ToList();
            Require(issues.Any(i => i.Line == 11), "NULL 가드 포인터 불일치(rx_pkt vs tx_pkt) 미검출");
            Require(issues.Any(i => i.Line == 15), "단일 포인터 가드 if(rx_pkt) 본문 역참조 불일치 미검출");
            Require(issues.Any(i => i.Line == 32), "중복 else if 조건(cmd == 1) 미검출");
            Require(issues.Any(i => i.Line == 36), "범위 비교 변수 혼동(x <= MAX_Y) 미검출");
            Require(!issues.Any(i => i.Line is 20 or 25 or 41), $"정상 조건문 오탐 발생: {string.Join(",", issues.Select(i => i.Line))}");
        });

        test("loopIndexMismatch: for 헤더 혼동 및 본문 인덱스 혼동 + 중첩 2차원 인덱싱 오탐 방지", () =>
        {
            const string code = """
                void test_loops(int N, int M) {
                    int i, j, k;
                    int rx_buf[16];
                    int tx_buf[16];
                    int mat[8][8];

                    for (i = 0; i < N; i++) {
                        tx_buf[i] = 0;
                    }

                    for (j = 0; i < N; j++) {
                        rx_buf[j] = 1;
                    }

                    for (j = 0; j < N; i++) {
                        rx_buf[j] = 2;
                    }

                    for (j = 0; j < N; j++) {
                        rx_buf[i] = 3;
                    }

                    /* 음성: 중첩 루프에서 둘 다 사용 */
                    for (i = 0; i < M; i++) {
                        for (j = 0; j < N; j++) {
                            mat[i][j] = 0;
                        }
                    }

                    /* 음성: 내부 루프가 자체 루프 변수 k를 사용 */
                    for (j = 0; j < N; j++) {
                        for (k = 0; k < M; k++) {
                            tx_buf[k] = 1;
                        }
                    }
                    (void)rx_buf; (void)tx_buf; (void)mat;
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleLoopIndexMismatch).ToList();
            Require(issues.Any(i => i.Line == 11), "for (j = 0; i < N; j++) 헤더 조건 변수 불일치 미검출");
            Require(issues.Any(i => i.Line == 15), "for (j = 0; j < N; i++) 헤더 증감 변수 불일치 미검출");
            Require(issues.Any(i => i.Line == 20), "루프 본문 인덱스 혼동(rx_buf[i]) 미검출");
            Require(!issues.Any(i => i.Line >= 24), $"중첩 루프 정상 코드 오탐: {string.Join(",", issues.Select(i => i.Line))}");
        });

        test("suspiciousCodeClone: 3문장 이상 인접 블록 및 switch case 클론 내 1곳 미수정 검출", () =>
        {
            const string code = """
                #define DEV_A 1
                #define DEV_B 2
                #define MODE_NORMAL 0
                void dev_reset(int d);
                void dev_set_mode(int d, int m);
                void dev_clear_err(int d);
                void dev_enable(int d);

                void test_clone(int sel) {
                    dev_reset(DEV_A);
                    dev_set_mode(DEV_A, MODE_NORMAL);
                    dev_clear_err(DEV_A);
                    dev_enable(DEV_A);

                    dev_reset(DEV_B);
                    dev_set_mode(DEV_B, MODE_NORMAL);
                    dev_clear_err(DEV_A);
                    dev_enable(DEV_B);

                    switch (sel) {
                        case 1:
                            dev_reset(DEV_A);
                            dev_set_mode(DEV_A, MODE_NORMAL);
                            dev_clear_err(DEV_A);
                            break;
                        case 2:
                            dev_reset(DEV_B);
                            dev_set_mode(DEV_B, MODE_NORMAL);
                            dev_clear_err(DEV_B);
                            break;
                    }

                    if (sel > 0) {
                        dev_reset(DEV_A);
                        dev_set_mode(DEV_A, MODE_NORMAL);
                        dev_clear_err(DEV_A);
                    } else {
                        dev_reset(DEV_B);
                        dev_set_mode(DEV_B, MODE_NORMAL);
                        dev_clear_err(DEV_A);
                    }
                }
                """;

            var result = RunWithCppcheck(code);
            var issues = result.Defects.Where(d => d.RuleId == CopyPasteAnalyzer.RuleSuspiciousClone).ToList();
            Require(issues.Count == 2 && issues.Any(i => i.Line == 17) && issues.Any(i => i.Line == 40),
                $"클론 미수정 1곳(17행, 40행) 검출 실패: {string.Join(",", issues.Select(i => i.Line))}");
        });

        test("규칙 비활성화(OFF) 시 복사/붙여넣기 6종 미보고", () =>
        {
            const string code = """
                #define CH1_BASE 0x100
                #define CH2_BASE 0x200
                void cfg(int a, int b);
                void f(int *pa, int *pb, int N) {
                    int arr[4]; int b[4] = {0}; int i, j;
                    /* CH1 */
                    cfg(CH1_BASE, 1);
                    /* CH2 */
                    cfg(CH1_BASE, 1);
                    arr[0] = b[0]; arr[1] = b[1]; arr[2] = b[1];
                    arr[3] = 1; arr[3] = 2;
                    if (pa != 0) { pb[0] = 1; }
                    for (j = 0; i < N; j++) { arr[j] = 0; }
                }
                """;

            var repo = new DataRepository();
            var rules = repo.LoadRules();
            string[] newRuleIds =
            {
                CopyPasteAnalyzer.RuleMacroCommentMismatch,
                CopyPasteAnalyzer.RuleSequenceMismatch,
                CopyPasteAnalyzer.RuleConsecutiveOverwrite,
                CopyPasteAnalyzer.RuleConditionBodyMismatch,
                CopyPasteAnalyzer.RuleLoopIndexMismatch,
                CopyPasteAnalyzer.RuleSuspiciousClone
            };
            foreach (var id in newRuleIds)
            {
                var r = rules.FirstOrDefault(x => x.Id == id) ?? throw new Exception($"rules_ko.json에 {id} 미등록");
                r.IsEnabled = false;
            }

            var result = RunWithCppcheck(code, rules);
            foreach (var id in newRuleIds)
            {
                Require(!result.Defects.Any(d => d.RuleId == id), $"비활성화한 규칙 {id}가 보고됨");
            }
        });

        test("복사/붙여넣기 6종 규칙 분류 분리 및 MainViewModel 카테고리 필터/요약 연동", () =>
        {
            const string expectedCategory = "복사·붙여넣기 / 문맥";
            string[] copyPasteRuleIds =
            {
                CopyPasteAnalyzer.RuleMacroCommentMismatch,
                CopyPasteAnalyzer.RuleSequenceMismatch,
                CopyPasteAnalyzer.RuleConsecutiveOverwrite,
                CopyPasteAnalyzer.RuleConditionBodyMismatch,
                CopyPasteAnalyzer.RuleLoopIndexMismatch,
                CopyPasteAnalyzer.RuleSuspiciousClone
            };

            var repo = new DataRepository();
            var rules = repo.LoadRules();
            foreach (var id in copyPasteRuleIds)
            {
                var rule = rules.FirstOrDefault(r => r.Id == id)
                    ?? throw new Exception($"rules_ko.json에 {id} 미등록");
                Require(rule.Category == expectedCategory,
                    $"{id} 카테고리가 '{expectedCategory}'가 아님: '{rule.Category}'");
            }

            var vm = new MainViewModel();
            vm.ClearWorkspace();
            Require(vm.DefectCategoryFilters.Contains(expectedCategory),
                $"DefectCategoryFilters 초기 목록에 '{expectedCategory}' 누락");

            // 로컬 최근 프로젝트(.jcproj)의 룰셋 상태에 영향을 받지 않도록 DB 규칙으로 초기화
            vm.AllRules.Clear();
            foreach (var r in rules)
                vm.AllRules.Add(r);
            vm.SelectedDefectCategoryFilter = "전체";
            vm.DefectSearchText = string.Empty;
            vm.ShowErrors = true;
            vm.ShowWarnings = true;
            vm.ShowAdvisories = true;

            var copyPasteRule = rules.First(r => r.Id == CopyPasteAnalyzer.RuleSequenceMismatch);
            var controlFlowRule = rules.First(r => r.Id == "redundantAssignment");
            var conversionRule = rules.First(r => r.Id == "implicitNarrowing");

            var copyPasteIssue = new AnalysisIssue
            {
                RuleId = copyPasteRule.Id,
                Category = copyPasteRule.Category,
                Severity = copyPasteRule.Severity,
                Obligation = copyPasteRule.Obligation,
                Title = copyPasteRule.Title,
                Message = "인덱스 짝 불일치",
                FilePath = "sample.c",
                Line = 10,
                Column = 5,
                RuleDetails = copyPasteRule
            };
            var controlFlowIssue = new AnalysisIssue
            {
                RuleId = controlFlowRule.Id,
                Category = controlFlowRule.Category,
                Severity = controlFlowRule.Severity,
                Obligation = controlFlowRule.Obligation,
                Title = controlFlowRule.Title,
                Message = "불필요한 재대입",
                FilePath = "sample.c",
                Line = 20,
                Column = 5,
                RuleDetails = controlFlowRule
            };
            var conversionIssue = new AnalysisIssue
            {
                RuleId = conversionRule.Id,
                Category = conversionRule.Category,
                Severity = conversionRule.Severity,
                Obligation = conversionRule.Obligation,
                Title = conversionRule.Title,
                Message = "암묵적 축소 변환",
                FilePath = "sample.c",
                Line = 30,
                Column = 5,
                RuleDetails = conversionRule
            };

            typeof(MainViewModel)
                .GetField("_allRawDefects", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, new List<AnalysisIssue> { copyPasteIssue, controlFlowIssue, conversionIssue });

            vm.RefreshFilteredDefects();
            Require(vm.FilteredDefects.Count == 3, $"전체 필터 결함 수 불일치: {vm.FilteredDefects.Count}");

            vm.SelectedDefectCategoryFilter = expectedCategory;
            Require(vm.FilteredDefects.Count == 1 && vm.FilteredDefects[0].RuleId == copyPasteRule.Id,
                $"'{expectedCategory}' 필터 후 FilteredDefects 불일치: {string.Join(",", vm.FilteredDefects.Select(d => d.RuleId))}");
            Require(vm.RuleIssueGroups.Count == 1 && vm.RuleIssueGroups[0].RuleId == copyPasteRule.Id && vm.RuleIssueGroups[0].Count == 1,
                $"'{expectedCategory}' 필터 후 RuleIssueGroups 불일치: {string.Join(",", vm.RuleIssueGroups.Select(g => g.RuleId))}");

            vm.SelectedDefectCategoryFilter = "제어 흐름 및 품질";
            Require(vm.FilteredDefects.Count == 1 && vm.FilteredDefects[0].RuleId == controlFlowRule.Id,
                $"'제어 흐름 및 품질' 필터에 복사/붙여넣기 규칙이 섞이거나 누락됨: {string.Join(",", vm.FilteredDefects.Select(d => d.RuleId))}");
        });
    }

    private static AnalysisRunResult RunWithCppcheck(string cCode, List<RuleInfo>? customRules = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jc_cp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "sample.c");
            File.WriteAllText(file, cCode);
            var repo = new DataRepository();
            var preset = repo.LoadTargetPresets().First();
            var rules = customRules ?? repo.LoadRules();
            var nodes = new List<SourceTreeNode>
            {
                new() { Name = "sample.c", FullPath = file, IsDirectory = false }
            };
            return new CppcheckRunner()
                .RunAnalysisAsync(preset, "c99", "", false, false, nodes, Array.Empty<SourceTreeNode>(), rules)
                .GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void Require(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }
}
