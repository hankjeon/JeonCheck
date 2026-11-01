"""상태 로직 테스트: 필터·예외·OFF·미등록 규칙·규칙별 요약·프로젝트 저장/불러오기 (UnknownDiagnosticTests·RuleSummaryTests 대응)."""

import os

import pytest

from jeoncheck import paths, repository
from jeoncheck.models import AnalysisIssue, CodeLineStatus, RuleInfo
from jeoncheck.state import AppState, build_rule_summary


@pytest.fixture
def src(tmp_path):
    d = tmp_path / "src"
    d.mkdir()
    f = d / "a.c"
    f.write_text("int a;\nint b;\n\nint c;\n", encoding="utf-8")
    (d / "b.c").write_text("int x;\n", encoding="utf-8")
    return d


@pytest.fixture
def state(src):
    s = AppState(load_sample=False)
    s.add_target_path(str(src))
    return s


def issue(rule_id, file, line, column=1, severity="Warning", message="msg", category="산술 및 연산", title=None):
    return AnalysisIssue(rule_id=rule_id, category=category, severity=severity, title=title or rule_id,
                         message=message, file_path=str(file), line=line, column=column)


def set_results(state, *issues):
    state._all_raw_defects = list(issues)
    state.refresh_filtered_defects()


def test_unregistered_rule_is_registered_on_and_filterable(state, src):
    a = src / "a.c"
    set_results(state, issue("futureId", a, 1, message="원문 1"), issue("futureId", a, 2, message="원문 2"))
    rule = state.find_rule("futureId")
    assert rule is not None and rule.is_enabled and rule.is_unregistered
    assert len(state.filtered_defects) == 2
    # 개별 원문은 발생 위치마다 유지
    assert [d.message for d in state.filtered_defects] == ["원문 1", "원문 2"]

    state.disable_rule_entirely("futureId")
    assert state.filtered_defects == [] and state.rule_issue_groups == []


def test_search_category_and_suppression(state, src):
    a = src / "a.c"
    i1 = issue("r1", a, 1, category="산술 및 연산")
    i2 = issue("r2", a, 2, category="메모리 및 포인터", message="needle")
    set_results(state, i1, i2)

    state.set_search_text("NEEDLE")
    assert state.filtered_defects == [i2]
    state.set_search_text("")
    state.set_category_filter("산술 및 연산")
    assert state.filtered_defects == [i1]
    state.set_category_filter("전체")

    state.suppress_single_issue(i1)
    assert state.filtered_defects == [i2] and len(state.suppressed) == 1
    state.restore_suppressed_issue(state.suppressed[0])
    assert len(state.filtered_defects) == 2


def test_suppression_key_ignores_column(state, src):
    a = src / "a.c"
    c1, c2 = issue("r1", a, 1, column=1), issue("r1", a, 1, column=9)
    set_results(state, c1, c2)
    state.suppress_single_issue(c1)
    assert state.filtered_defects == []


def test_code_viewer_line_states_and_badge(state, src):
    a = src / "a.c"
    set_results(state, issue("r1", a, 1, severity="Error"), issue("r2", a, 1), issue("r3", a, 2))
    state._coverage = {}
    state.load_file_into_code_viewer(str(a))
    lines = state.current_file_lines
    assert lines[0].status == CodeLineStatus.ERROR and lines[0].inline_annotation == "[r1 +1]"
    assert lines[1].status == CodeLineStatus.WARNING
    state.suppress_code_line(lines[1])
    assert state.current_file_lines[1].status == CodeLineStatus.SUPPRESSED
    state.restore_code_line(state.current_file_lines[1])
    assert state.current_file_lines[1].status == CodeLineStatus.WARNING


def test_rule_summary_order_and_preview(src):
    a, b = src / "a.c", src / "b.c"
    groups = build_rule_summary([issue("zz", a, 1), issue("aa", b, 1), issue("zz", a, 4), issue("mm", a, 99)])
    assert [g.rule_id for g in groups] == ["zz", "aa", "mm"]
    assert [o.code_text for o in groups[0].occurrences] == ["int a;", "int c;"]
    assert groups[2].occurrences[0].code_text == "해당 줄이 현재 파일에 없습니다."
    assert groups[0].header == "zz · zz · 2건"
    missing = build_rule_summary([issue("x", src / "gone.c", 1)])
    assert "읽을 수 없습니다" in missing[0].occurrences[0].code_text


def test_rule_group_expansion_is_kept(state, src):
    a = src / "a.c"
    set_results(state, issue("r1", a, 1), issue("r2", a, 2))
    state.rule_issue_groups[0].is_expanded = False
    state.refresh_filtered_defects()
    assert state.rule_issue_groups[0].is_expanded is False


def test_project_round_trip_restores_options_rules_and_suppression(state, src, tmp_path):
    a = src / "a.c"
    set_results(state, issue("futureId", a, 1), issue("r2", a, 2))
    state.disable_rule_entirely("futureId")
    state.suppress_single_issue(state.filtered_defects[0])
    state.c_standard = "c11"
    state.custom_defines = "FOO=1"
    state.remove_node(next(n for n in state.target_roots[0].children if n.name == "b.c"))
    proj = tmp_path / "p.jcproj"
    state.save_project(str(proj))

    loaded = AppState(load_sample=False)
    loaded.load_project(str(proj))
    assert loaded.c_standard == "c11" and loaded.custom_defines == "FOO=1"
    assert loaded.find_rule("futureId") is not None and not loaded.find_rule("futureId").is_enabled
    assert [s.rule_id for s in loaded.suppressed] == ["r2"]
    assert [f.name for f in loaded.target_roots[0].enumerate_files()] == ["a.c"]
    # 분석 결과는 저장하지 않는다.
    assert loaded.filtered_defects == []


def test_windows_saved_sample_project_loads(tmp_path):
    s = AppState(load_sample=False)
    s.load_project(str(paths.samples_directory() / "dsp_motor_sample.jcproj"))
    assert s.selected_preset.id == "ti-c6678-eabi"
    assert [os.path.basename(r.full_path) for r in s.target_roots] == ["src"]
    assert sorted(os.path.basename(r.full_path) for r in s.non_target_roots) == ["drivers", "include"]


def test_backslash_paths_from_windows_project(tmp_path):
    (tmp_path / "src" / "sub").mkdir(parents=True)
    (tmp_path / "src" / "sub" / "x.c").write_text("int x;\n")
    proj = tmp_path / "w.jcproj"
    proj.write_text('{"targetPaths": ["src\\\\sub"], "nonTargetPaths": []}', encoding="utf-8")
    model = repository.load_project(str(proj))
    assert model.target_paths == [str(tmp_path / "src" / "sub")]


def test_unregistered_saved_rule_selection_restored_before_analysis(tmp_path):
    proj = tmp_path / "r.jcproj"
    proj.write_text('{"enabledRuleIds": [], "disabledRuleIds": ["brandNewId"]}', encoding="utf-8")
    s = AppState(load_sample=False)
    s.load_project(str(proj))
    rule = s.find_rule("brandNewId")
    assert isinstance(rule, RuleInfo) and rule.is_unregistered and not rule.is_enabled


def test_move_between_target_and_reference(state, src):
    b = next(n for n in state.target_roots[0].children if n.name == "b.c")
    state.move_node_to_non_target(b)
    assert [f.name for f in state.target_roots[0].enumerate_files()] == ["a.c"]
    assert [r.name for r in state.non_target_roots] == ["b.c"]
    state.move_node_to_target(state.non_target_roots[0])
    assert sorted(f.name for f in state.target_roots[0].enumerate_files()) == ["a.c", "b.c"]
    assert state.non_target_roots == []
