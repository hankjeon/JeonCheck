"""누락 헤더(missingInclude) 노트에서 헤더 이름을 뽑고, 선택한 폴더 하위에서 같은 이름의 파일을 찾는다.

WPF 앱 MissingHeaderSearchItem·MissingHeaderSearchWindow의 Qt 비의존 포팅이다.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from enum import Enum

from .models import AnalysisIssue, normalize_path

_INCLUDE_MSG = re.compile(r"""Include\s+file:\s*[<"](?P<inc>[^>"]+)[>"]""", re.IGNORECASE)
_SOURCE_LINE_INCLUDE = re.compile(r"""#\s*include\s*[<"](?P<inc>[^>"]+)[>"]""")
_MISSING_INCLUDE_IDS = ("missinginclude", "missingincludesystem")


class MatchStatus(Enum):
    NOT_SEARCHED = "not_searched"
    FOUND_SINGLE = "found_single"
    FOUND_MULTIPLE = "found_multiple"
    NOT_FOUND = "not_found"


@dataclass
class MissingHeaderItem:
    raw_include: str  # 원본 #include 문자열 (예: "ti/csl/cslr_device.h")
    referenced_by_files: list[str] = field(default_factory=list)
    candidate_paths: list[str] = field(default_factory=list)
    selected_candidate_path: str = ""
    status: MatchStatus = MatchStatus.NOT_SEARCHED
    is_selected: bool = False

    @property
    def header_file_name(self) -> str:
        return os.path.basename(self.raw_include.replace("\\", "/"))

    @property
    def can_select(self) -> bool:
        return self.status in (MatchStatus.FOUND_SINGLE, MatchStatus.FOUND_MULTIPLE)

    @property
    def referenced_by_display(self) -> str:
        if not self.referenced_by_files:
            return "-"
        first = os.path.basename(self.referenced_by_files[0])
        extra = len(self.referenced_by_files) - 1
        return f"{first} 외 {extra}개" if extra else first

    @property
    def status_display(self) -> str:
        return {
            MatchStatus.FOUND_SINGLE: "✅ 발견됨 (1개)",
            MatchStatus.FOUND_MULTIPLE: f"⚠️ 중복 ({len(self.candidate_paths)}개 후보)",
            MatchStatus.NOT_FOUND: "❌ 미발견",
        }.get(self.status, "⏳ 검색 전")

    @property
    def single_or_not_found_display(self) -> str:
        if self.status == MatchStatus.FOUND_SINGLE:
            return self.selected_candidate_path
        if self.status == MatchStatus.NOT_FOUND:
            return "(선택한 폴더 하위에서 파일을 찾지 못했습니다)"
        return "(상단 [📂 다른 폴더 추가로 검색...] 버튼을 눌러 폴더를 지정하세요)"


def extract_missing_header_name(note: AnalysisIssue) -> str | None:
    """missingInclude / missingIncludeSystem 노트에서 누락된 #include 헤더 이름을 추출한다."""
    if note is None or note.rule_id.lower() not in _MISSING_INCLUDE_IDS:
        return None
    m = _INCLUDE_MSG.search(note.message or "")
    if m and m.group("inc").strip():
        return m.group("inc").strip()
    # 메시지에서 추출되지 않으면 실제 소스 파일의 해당 줄에서 #include 구문을 직접 확인
    if note.file_path and note.line > 0 and os.path.isfile(note.file_path):
        try:
            with open(note.file_path, encoding="utf-8", errors="replace") as f:
                for i, text in enumerate(f, start=1):
                    if i == note.line:
                        lm = _SOURCE_LINE_INCLUDE.search(text)
                        return lm.group("inc").strip() if lm else None
        except OSError:
            pass
    return None


def group_missing_headers(notes: list[AnalysisIssue]) -> list[MissingHeaderItem]:
    """노트 목록을 누락 헤더 이름별로 묶는다 (이름은 대소문자 무시, 처음 나온 순서 유지)."""
    grouped: dict[str, MissingHeaderItem] = {}
    for note in notes:
        inc = extract_missing_header_name(note)
        if not inc:
            continue
        item = grouped.setdefault(inc.lower(), MissingHeaderItem(raw_include=inc))
        if note.file_path and all(normalize_path(f) != normalize_path(note.file_path)
                                  for f in item.referenced_by_files):
            item.referenced_by_files.append(note.file_path)
    return list(grouped.values())


def _common_prefix_length(a: str, b: str) -> int:
    count = 0
    for x, y in zip(a, b):
        if x.upper() != y.upper():
            break
        count += 1
    return count


def rank_candidates(raw_include: str, referenced_by_files: list[str], candidates: list[str]) -> list[str]:
    """같은 이름의 헤더가 여러 곳에서 발견되었을 때 추천 순서로 정렬한다.

    1순위: #include "sub/header.h"의 상대 경로 접미사가 일치하는 정도
    2순위: 이 헤더를 #include 한 소스 파일과의 공통 상위 경로 길이
    3순위: 전체 경로가 더 짧은 쪽
    """
    inc_parts = [p for p in re.split(r"[/\\]", raw_include) if p and p not in (".", "..")]

    def score(cand: str) -> int:
        cand_parts = [p for p in re.split(r"[/\\]", cand) if p]
        suffix = 0
        for a, b in zip(reversed(inc_parts), reversed(cand_parts)):
            if a.lower() != b.lower():
                break
            suffix += 100
        proximity = max((_common_prefix_length(f, cand) for f in referenced_by_files), default=0)
        return suffix + proximity

    return sorted(candidates, key=lambda c: (-score(c), len(c), c.lower()))


def search_folder(items: list[MissingHeaderItem], root_folder: str) -> None:
    """root_folder 하위 전체에서 헤더 파일명을 찾아 각 항목의 후보·상태·선택을 갱신한다.

    이전에 다른 폴더에서 찾은 후보는 유지하고 새 후보를 덧붙인다.
    """
    root = os.path.abspath(root_folder)
    wanted = {i.header_file_name.lower() for i in items if i.header_file_name}
    found: dict[str, list[str]] = {}
    # 접근 권한이 없는 하위 폴더는 건너뛴다 (os.walk 기본 동작).
    for dir_path, _dirs, files in os.walk(root):
        for name in files:
            if name.lower() in wanted:
                found.setdefault(name.lower(), []).append(os.path.abspath(os.path.join(dir_path, name)))

    for item in items:
        known = {normalize_path(c) for c in item.candidate_paths}
        for path in found.get(item.header_file_name.lower(), []):
            if normalize_path(path) not in known:
                known.add(normalize_path(path))
                item.candidate_paths.append(path)

        if not item.candidate_paths:
            item.status = MatchStatus.NOT_FOUND
            item.is_selected = False
            item.selected_candidate_path = ""
        elif len(item.candidate_paths) == 1:
            item.selected_candidate_path = item.candidate_paths[0]
            item.status = MatchStatus.FOUND_SINGLE
            item.is_selected = True
        else:
            # 추천 점수 순으로 정렬해 가장 알맞은 후보를 자동 선택
            item.candidate_paths = rank_candidates(item.raw_include, item.referenced_by_files, item.candidate_paths)
            item.selected_candidate_path = item.candidate_paths[0]
            item.status = MatchStatus.FOUND_MULTIPLE
            item.is_selected = True


def selected_paths_to_add(items: list[MissingHeaderItem]) -> list[str]:
    """체크된 항목 중 실제로 존재하는 선택 경로만 중복 없이 반환한다."""
    result, seen = [], set()
    for item in items:
        path = item.selected_candidate_path
        if not item.is_selected or not item.can_select or not path or not os.path.isfile(path):
            continue
        if normalize_path(path) not in seen:
            seen.add(normalize_path(path))
            result.append(os.path.abspath(path))
    return result
