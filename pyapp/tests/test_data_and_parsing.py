"""규칙 DB·모델 표시·엔진 결과 해석 테스트 (WPF 앱 DescriptionTests와 같은 검사 항목)."""

import json

from jeoncheck import repository
from jeoncheck.engine.clang_tidy import parse_clang_tidy_output
from jeoncheck.engine.runner import AnalysisRunResult, _Keys, parse_cppcheck_xml, parse_misra_cli_output
from jeoncheck.models import AnalysisIssue, RuleInfo, normalize_path

TARGET = "/tmp/jeoncheck_fixture/fixture.c"


def xml(error_id, severity, msg, file=TARGET, line=3, col=5):
    return (f'<?xml version="1.0" encoding="UTF-8"?><results version="2"><cppcheck version="2.19.0"/><errors>'
            f'<error id="{error_id}" severity="{severity}" msg="{msg}"><location file="{file}" line="{line}" '
            f'column="{col}"/></error></errors></results>')


def parse(error_id, severity, msg, rules=None):
    result = AnalysisRunResult()
    rules_by_id = {r.id: r for r in (rules or repository.load_rules())}
    assert parse_cppcheck_xml(xml(error_id, severity, msg), {normalize_path(TARGET)}, rules_by_id, result, _Keys())
    return result


def test_rule_db_ids_and_required_fields():
    rules = repository.load_rules()
    assert rules
    ids = [r.id for r in rules]
    assert len(ids) == len(set(ids)), "규칙 ID 중복"
    for r in rules:
        assert r.id
        for text in (r.title, r.summary, r.description):
            assert any("가" <= c <= "힣" for c in text), f"{r.id}: 한글 설명 누락"


def test_target_presets_load():
    presets = repository.load_target_presets()
    assert len(presets) == 11
    assert all(p.platform_xml for p in presets)


def test_empty_examples_show_unregistered_notice():
    rule = RuleInfo(bad_example=" ", good_example="")
    assert "아직 등록되지" in rule.bad_example_display
    assert "아직 등록되지" in rule.good_example_display
    rule.good_example = "return 0;"
    assert rule.good_example_display == "return 0;"


def test_misra_obligation_and_project_policy_are_separate():
    assert RuleInfo(id="misra-c2012-9.1", obligation="강제(Mandatory)").obligation_display == "MISRA 의무: 강제(Mandatory)"
    custom = RuleInfo(id="nullPointer", obligation="치명(Critical)")
    assert custom.obligation_display == "프로젝트 정책: 우선 검토"
    assert AnalysisIssue(rule_id="nullPointer", obligation="치명(Critical)").obligation_display == custom.obligation_display


def test_missing_header_is_note_without_completeness_claim():
    result = parse("missingIncludeSystem", "information", "Header unavailable")
    assert not result.defects and len(result.notes) == 1
    note = result.notes[0]
    assert note.is_info_or_note and note.severity == "Info"
    assert "제한될 수" in note.message and "Header unavailable" in note.message


def test_unregistered_cppcheck_diagnostic():
    raw = "Unknown operation issue"
    result = parse("futureCheck", "warning", raw)
    assert len(result.defects) == 1
    issue = result.defects[0]
    assert issue.severity == "Warning" and issue.obligation == "미분류"
    assert "개별 한글 해설" in issue.message and issue.message.count(raw) == 1
    assert issue.rule_details.good_example == ""


def test_uninitvar_is_also_reported_as_misra_9_1():
    result = parse("uninitvar", "error", "Uninitialized variable: x")
    ids = sorted(d.rule_id for d in result.defects)
    assert ids == ["misra-c2012-9.1", "uninitvar"]


def test_disabled_rule_and_non_target_file_are_dropped():
    rules = repository.load_rules()
    rule = next(r for r in rules if r.id == "uninitvar")
    rule.is_enabled = False
    result = parse("uninitvar", "error", "x", rules)
    assert [d.rule_id for d in result.defects] == ["misra-c2012-9.1"]

    other = AnalysisRunResult()
    assert parse_cppcheck_xml(xml("futureCheck", "warning", "x", file="/tmp/other.c"), {normalize_path(TARGET)},
                              {}, other, _Keys())
    assert not other.defects


def test_invalid_xml_is_not_treated_as_zero_defects():
    assert not parse_cppcheck_xml("cppcheck: error: unknown option", set(), {}, AnalysisRunResult(), _Keys())


def test_misra_cli_output_parsing():
    lines = [
        {"addon": "misra", "severity": "none", "message": "Misra C: 1.2", "errorId": "logChecker"},
        {"summary": "MisraUsage", "data": []},
        {"file": "src/a.c", "linenr": 13, "column": 9, "severity": "style", "message": "misra violation",
         "addon": "misra", "errorId": "c2012-8.4", "extra": ""},
    ]
    out = "\n".join(json.dumps(x) for x in lines) + "\nnot json\n"
    assert parse_misra_cli_output(out) == [
        {"ruleId": "misra-c2012-8.4", "file": "src/a.c", "line": 13, "column": 9, "severity": "style"}]


def test_unregistered_clang_tidy_diagnostics_keep_engine_severity():
    for sev in ("warning", "error"):
        for rid in ("clang-analyzer-future.Check", "cert-future-c"):
            out = f"{TARGET}:7:3: {sev}: Future problem [{rid}]"
            issues = parse_clang_tidy_output(out, {normalize_path(TARGET)}, {}, set())
            assert len(issues) == 1
            assert issues[0].rule_id == rid
            assert issues[0].severity == ("Error" if sev == "error" else "Warning")
            assert issues[0].category == "Clang-Tidy / CERT-C"


def test_clang_tidy_narrowing_maps_to_implicit_narrowing_and_dedups():
    rules = {r.id: r for r in repository.load_rules()}
    out = f"{TARGET}:7:3: warning: narrowing conversion [bugprone-narrowing-conversions]"
    keys: set[str] = set()
    assert [i.rule_id for i in parse_clang_tidy_output(out, {normalize_path(TARGET)}, rules, keys)] == \
        ["implicitNarrowing"]
    assert parse_clang_tidy_output(out, {normalize_path(TARGET)}, rules, keys) == []
