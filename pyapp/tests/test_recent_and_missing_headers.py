"""최근 프로젝트 목록·마지막 프로젝트 자동 열기와 누락 헤더 검색 로직 테스트."""

import json
import os

from jeoncheck import repository
from jeoncheck.missing_headers import (MatchStatus, extract_missing_header_name, group_missing_headers,
                                       rank_candidates, search_folder, selected_paths_to_add)
from jeoncheck.models import AnalysisIssue
from jeoncheck.state import AppState


def make_project(tmp_path, name):
    src = tmp_path / name / "src"
    src.mkdir(parents=True)
    (src / "a.c").write_text("int a;\n", encoding="utf-8")
    s = AppState(load_sample=False)
    s.add_target_path(str(src))
    proj = tmp_path / name / f"{name}.jcproj"
    s.save_project(str(proj))
    return proj


# ---------------------------------------------------------------- 최근 프로젝트
def test_save_and_load_update_recent_list_most_recent_first(tmp_path, isolated_user_settings):
    p1, p2 = make_project(tmp_path, "one"), make_project(tmp_path, "two")
    s = AppState(load_sample=False)
    assert s.recent_projects == [str(p2), str(p1)]
    s.load_project(str(p1))
    assert s.recent_projects == [str(p1), str(p2)]
    # WPF 앱과 같은 키 이름으로 저장
    saved = json.loads(isolated_user_settings.read_text(encoding="utf-8"))
    assert saved == {"RecentProjects": [str(p1), str(p2)]}


def test_recent_list_is_limited_and_deduplicated(tmp_path):
    s = AppState(load_sample=False)
    for i in range(12):
        s.add_recent_project(str(tmp_path / f"p{i}.jcproj"))
    s.add_recent_project(str(tmp_path / "p5.jcproj"))
    assert len(s.recent_projects) == repository.MAX_RECENT_PROJECTS
    assert s.recent_projects[0] == str(tmp_path / "p5.jcproj")
    assert s.recent_projects.count(str(tmp_path / "p5.jcproj")) == 1


def test_remove_and_clear_recent(tmp_path):
    s = AppState(load_sample=False)
    s.add_recent_project(str(tmp_path / "a.jcproj"))
    s.add_recent_project(str(tmp_path / "b.jcproj"))
    s.remove_recent_project(str(tmp_path / "a.jcproj"))
    assert s.recent_projects == [str(tmp_path / "b.jcproj")]
    s.clear_recent_projects()
    assert s.recent_projects == [] and repository.load_recent_projects() == []


def test_wpf_settings_file_is_read_case_insensitively(tmp_path, isolated_user_settings):
    isolated_user_settings.parent.mkdir(parents=True)
    isolated_user_settings.write_text('{"recentProjects": ["x.jcproj", ""]}', encoding="utf-8-sig")
    assert repository.load_recent_projects() == [os.path.abspath("x.jcproj")]
    isolated_user_settings.write_text("깨진 파일", encoding="utf-8")
    assert repository.load_recent_projects() == []


def test_startup_opens_last_existing_project(tmp_path):
    p1 = make_project(tmp_path, "one")
    s = AppState(load_sample=False)
    s.add_recent_project(str(tmp_path / "deleted.jcproj"))  # 없는 파일은 건너뛴다
    started = AppState(load_sample=True)
    assert started.current_project_file_path == str(p1)
    assert started.status_message.startswith("최근 프로젝트 자동 불러오기 완료")


def test_startup_falls_back_to_sample_without_recent():
    s = AppState(load_sample=True)
    assert s.current_project_file_path == ""
    assert [os.path.basename(r.full_path) for r in s.target_roots] == ["src"]


# ---------------------------------------------------------------- 누락 헤더 검색
def note(rule_id, message, file_path="", line=0):
    return AnalysisIssue(rule_id=rule_id, message=message, file_path=file_path, line=line, is_info_or_note=True)


def test_extract_header_name_from_message_and_source_line(tmp_path):
    msg = '[참조 헤더 누락 안내] Include file: "ti/csl/cslr.h" not found. (안내)'
    assert extract_missing_header_name(note("missingInclude", msg)) == "ti/csl/cslr.h"
    assert extract_missing_header_name(note("missingIncludeSystem", "Include file: <stdint.h> not found.")) \
        == "stdint.h"
    src = tmp_path / "m.c"
    src.write_text('int a;\n#include "vendor.h"\n', encoding="utf-8")
    assert extract_missing_header_name(note("missingInclude", "형식이 다른 메시지", str(src), 2)) == "vendor.h"
    assert extract_missing_header_name(note("checkersReport", 'Include file: "x.h"')) is None


def test_group_by_header_and_referencing_files(tmp_path):
    a, b = str(tmp_path / "a.c"), str(tmp_path / "b.c")
    items = group_missing_headers([
        note("missingInclude", 'Include file: "x.h" not found.', a, 1),
        note("missingInclude", 'Include file: "X.h" not found.', b, 1),
        note("missingInclude", 'Include file: "x.h" not found.', a, 5),
        note("unknownMacro", "무관한 노트", a, 1),
    ])
    assert [i.raw_include for i in items] == ["x.h"]
    assert items[0].referenced_by_files == [a, b]
    assert items[0].referenced_by_display == "a.c 외 1개"


def test_search_folder_statuses_and_paths_to_add(tmp_path):
    root = tmp_path / "sdk"
    (root / "one").mkdir(parents=True)
    (root / "two" / "ti" / "csl").mkdir(parents=True)
    (root / "one" / "single.h").write_text("", encoding="utf-8")
    (root / "one" / "cslr.h").write_text("", encoding="utf-8")
    (root / "two" / "ti" / "csl" / "cslr.h").write_text("", encoding="utf-8")
    items = group_missing_headers([
        note("missingInclude", 'Include file: "single.h" not found.'),
        note("missingInclude", 'Include file: "ti/csl/cslr.h" not found.'),
        note("missingInclude", 'Include file: "nowhere.h" not found.'),
    ])
    search_folder(items, str(root))
    single, multi, missing = items
    assert single.status == MatchStatus.FOUND_SINGLE and single.is_selected
    assert multi.status == MatchStatus.FOUND_MULTIPLE and len(multi.candidate_paths) == 2
    # #include 상대 경로 접미사가 일치하는 후보를 1순위로 추천
    assert multi.selected_candidate_path == str(root / "two" / "ti" / "csl" / "cslr.h")
    assert missing.status == MatchStatus.NOT_FOUND and not missing.is_selected

    single.is_selected = False
    assert selected_paths_to_add(items) == [str(root / "two" / "ti" / "csl" / "cslr.h")]


def test_search_another_folder_keeps_previous_candidates(tmp_path):
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    (tmp_path / "b" / "late.h").write_text("", encoding="utf-8")
    items = group_missing_headers([note("missingInclude", 'Include file: "late.h" not found.')])
    search_folder(items, str(tmp_path / "a"))
    assert items[0].status == MatchStatus.NOT_FOUND
    search_folder(items, str(tmp_path / "b"))
    assert items[0].status == MatchStatus.FOUND_SINGLE
    search_folder(items, str(tmp_path / "b"))  # 같은 폴더를 다시 검색해도 후보가 중복되지 않음
    assert len(items[0].candidate_paths) == 1


def test_rank_prefers_folder_near_referencing_source():
    near, far = "/proj/app/inc/cfg.h", "/other/lib/cfg.h"
    assert rank_candidates("cfg.h", ["/proj/app/src/main.c"], [far, near])[0] == near
