"""화면 상태와 동작 로직 (WPF 앱 MainViewModel의 Qt 비의존 포팅).

UI는 `listeners`에 콜백을 등록해 변경 알림(이벤트 이름)을 받고, 필요한 부분을 다시 그린다.
이벤트: "status", "options", "trees", "results", "code", "rules", "project", "selection", "analyzing"
"""

from __future__ import annotations

import os
import threading
from dataclasses import dataclass, field
from typing import Callable

from . import external, paths, repository
from .engine.runner import AnalysisRunResult, run_analysis
from .models import (AnalysisIssue, CodeLine, CodeLineStatus, FileLineCoverage, RuleInfo, RuleIssueGroup,
                     RuleIssueOccurrence, SourceTreeNode, SuppressedIssueEntry, TargetPreset, is_under,
                     normalize_path)

ALL_CATEGORIES = "전체"
DEFAULT_CATEGORY_FILTERS = [ALL_CATEGORIES, "타입 및 형변환", "MISRA C 2012", "Clang-Tidy / CERT-C", "메모리 및 포인터",
                            "산술 및 연산", "제어 흐름 및 품질", "구문 및 환경"]
C_STANDARDS = ["c89", "c99", "c11", "c17"]
DEFAULT_PRESET_ID = "ti-c6678-eabi"


def build_rule_summary(issues: list[AnalysisIssue]) -> list[RuleIssueGroup]:
    """규칙별 건수 내림차순(동률은 ID 순)으로 묶고, 발생 위치별 현재 파일의 코드 한 줄을 붙인다."""
    source_lines: dict[str, list[str] | None] = {}

    def read_code(issue: AnalysisIssue) -> str:
        if issue.line <= 0:
            return "줄 정보가 없는 진단입니다."
        key = normalize_path(issue.file_path)
        if key not in source_lines:
            try:
                with open(issue.file_path, encoding="utf-8", errors="replace") as f:
                    source_lines[key] = f.read().splitlines()
            except (OSError, ValueError):
                source_lines[key] = None
        lines = source_lines[key]
        if lines is None:
            return "코드를 읽을 수 없습니다. 파일 경로와 읽기 권한을 확인하십시오."
        if issue.line > len(lines):
            return "해당 줄이 현재 파일에 없습니다."
        code = lines[issue.line - 1].strip()
        return code if code else "(빈 줄)"

    groups: dict[str, list[AnalysisIssue]] = {}
    for issue in issues:
        groups.setdefault(issue.rule_id.lower(), []).append(issue)
    ordered = sorted(groups.values(), key=lambda g: (-len(g), g[0].rule_id.lower()))
    result = []
    for g in ordered:
        occ = sorted(g, key=lambda i: (i.file_path.lower(), i.line, i.column))
        result.append(RuleIssueGroup(rule_id=g[0].rule_id, title=g[0].title,
                                     occurrences=[RuleIssueOccurrence(i, read_code(i)) for i in occ]))
    return result


@dataclass
class AnalysisRequest:
    """백그라운드 분석 입력 스냅샷 (분석 중 UI에서 트리·설정을 바꿔도 영향 없음)."""
    preset: TargetPreset
    c_standard: str
    custom_defines: str
    force_all_ifdef_configs: bool
    enable_clang_tidy: bool
    target_roots: list[SourceTreeNode]
    non_target_roots: list[SourceTreeNode]
    rules: list[RuleInfo]
    cancel: threading.Event = field(default_factory=threading.Event)

    def run(self) -> AnalysisRunResult:
        return run_analysis(self.preset, self.c_standard, self.custom_defines, self.force_all_ifdef_configs,
                            self.enable_clang_tidy, self.target_roots, self.non_target_roots, self.rules,
                            self.cancel)


class AppState:
    def __init__(self, load_sample: bool = True):
        self.listeners: list[Callable[[str], None]] = []

        self.target_presets: list[TargetPreset] = []
        self.selected_preset: TargetPreset | None = None
        self.c_standard = "c99"
        self.custom_defines = ""
        self.force_all_ifdef_configs = False
        self.enable_clang_tidy = True

        self.all_rules: list[RuleInfo] = []
        self.category_filters: list[str] = list(DEFAULT_CATEGORY_FILTERS)
        self.search_text = ""
        self.category_filter = ALL_CATEGORIES

        self.target_roots: list[SourceTreeNode] = []
        self.non_target_roots: list[SourceTreeNode] = []
        self._excluded_target: set[str] = set()
        self._excluded_non_target: set[str] = set()

        self._all_raw_defects: list[AnalysisIssue] = []
        self._coverage: dict[str, FileLineCoverage] = {}
        self.filtered_defects: list[AnalysisIssue] = []
        self.rule_issue_groups: list[RuleIssueGroup] = []
        self.notes: list[AnalysisIssue] = []
        self.suppressed: list[SuppressedIssueEntry] = []

        self.current_code_file_path = ""
        self.current_file_lines: list[CodeLine] = []
        self.code_stats_summary = "파일을 선택하면 라인별 검사 상태가 표시됩니다."
        self.selected_issue: AnalysisIssue | None = None

        self.current_project_file_path = ""
        self.status_message = "준비 완료"
        self._analysis: AnalysisRequest | None = None

        self._initialize(load_sample)

    # ---------------------------------------------------------------- 알림
    def notify(self, *events: str) -> None:
        for event in events:
            for cb in list(self.listeners):
                cb(event)

    def set_status(self, message: str) -> None:
        self.status_message = message
        self.notify("status")

    # ---------------------------------------------------------------- 표시용 속성
    @property
    def is_analyzing(self) -> bool:
        return self._analysis is not None

    @property
    def project_title(self) -> str:
        if not self.current_project_file_path:
            return "전검사 (JeonCheck) - 임베디드 C 정적 분석 도우미 (미저장 프로젝트)"
        return f"전검사 (JeonCheck) - {os.path.basename(self.current_project_file_path)}"

    @property
    def ruleset_button_text(self) -> str:
        return f"⚙ 검사 규칙 설정 ({sum(r.is_enabled for r in self.all_rules)}/{len(self.all_rules)})"

    @property
    def defects_tab_header(self) -> str:
        return f"🚨 결함 및 규칙 위반 ({len(self.filtered_defects)})"

    @property
    def rule_summary_tab_header(self) -> str:
        return f"📊 규칙별 요약 ({len(self.rule_issue_groups)}개)"

    @property
    def rule_summary_status_text(self) -> str:
        return f"{len(self.rule_issue_groups)}개 규칙 · {len(self.filtered_defects)}건 (현재 검색·분류·예외 처리 기준)"

    @property
    def notes_tab_header(self) -> str:
        return f"ℹ️ 노트 / 누락 파일 안내 ({len(self.notes)})"

    @property
    def suppressed_tab_header(self) -> str:
        return f"🔇 예외(무시) 항목 ({len(self.suppressed)})"

    @property
    def current_code_file_name(self) -> str:
        if not self.current_code_file_path:
            return "선택된 소스 파일 없음"
        return f"{os.path.basename(self.current_code_file_path)}  ({self.current_code_file_path})"

    def is_issue_suppressed(self, issue: AnalysisIssue | None) -> bool:
        return issue is not None and any(s.suppression_key == issue.suppression_key for s in self.suppressed)

    # ---------------------------------------------------------------- 초기화
    def _initialize(self, load_sample: bool) -> None:
        errors = []
        try:
            self.target_presets = repository.load_target_presets()
        except Exception as ex:  # noqa: BLE001  (데이터 파일 오류는 상태 메시지로 안내)
            errors.append(f"타겟 프리셋(targets.json) 읽기 실패: {ex}")
        self.selected_preset = next((p for p in self.target_presets if p.id == DEFAULT_PRESET_ID),
                                    self.target_presets[0] if self.target_presets else None)
        try:
            rules = repository.load_rules()
        except Exception as ex:  # noqa: BLE001
            rules = []
            errors.append(f"규칙 DB(rules_ko.json) 읽기 실패: {ex}")
        for r in rules:
            self._add_rule(r)
        if load_sample and paths.samples_directory().is_dir():
            self.load_sample_workspace()
        if errors:
            self.set_status("데이터 파일 오류: " + " / ".join(errors))

    def _add_rule(self, rule: RuleInfo) -> None:
        self.all_rules.append(rule)
        if rule.category and rule.category not in self.category_filters:
            self.category_filters.append(rule.category)

    def find_rule(self, rule_id: str) -> RuleInfo | None:
        low = rule_id.lower()
        return next((r for r in self.all_rules if r.id.lower() == low), None)

    # ---------------------------------------------------------------- 작업 공간·트리
    def load_sample_workspace(self) -> None:
        self.clear_workspace()
        s = paths.samples_directory()
        for sub, target in (("src", True), ("include", False), ("drivers", False)):
            if (s / sub).is_dir():
                (self.add_target_path if target else self.add_non_target_path)(str(s / sub))
        first = next((f for r in self.target_roots for f in r.enumerate_files()), None)
        if first:
            self.load_file_into_code_viewer(first.full_path)
        self.set_status("샘플 프로젝트가 로드되었습니다. 상단 [▶ 정적 분석 시작]을 눌러 바로 확인해 보세요.")

    def clear_workspace(self) -> None:
        # 진행 중인 분석이 있으면 취소 (이전 작업 공간의 결과가 새 작업 공간에 반영되지 않도록)
        self.cancel_analysis()
        self.current_project_file_path = ""
        self._excluded_target.clear()
        self._excluded_non_target.clear()
        self.target_roots.clear()
        self.non_target_roots.clear()
        self._all_raw_defects = []
        self._coverage = {}
        self.filtered_defects = []
        self.rule_issue_groups = []
        self.notes = []
        self.suppressed = []
        self.current_file_lines = []
        self.current_code_file_path = ""
        self.selected_issue = None
        self.code_stats_summary = "파일을 선택하면 라인별 검사 상태가 표시됩니다."
        self.notify("trees", "results", "code", "project", "selection")
        self.set_status("새 프로젝트가 준비되었습니다.")

    def _parent_root(self, path: str) -> SourceTreeNode | None:
        return next((r for r in self.target_roots if r.is_directory and is_under(path, r.full_path)), None)

    def add_target_path(self, path: str) -> None:
        if not os.path.exists(path):
            return
        full = os.path.abspath(path)
        self._excluded_target.discard(full)
        # 상위 폴더가 이미 대상 루트에 있으면 그 트리를 다시 구성
        parent = self._parent_root(full)
        if parent is not None:
            idx = self.target_roots.index(parent)
            self.target_roots[idx] = SourceTreeNode.create_from_path(parent.full_path, True, self._excluded_target)
            self._update_tree_badges()
            self.notify("trees")
            return
        if any(normalize_path(r.full_path) == normalize_path(full) for r in self.target_roots):
            return
        node = SourceTreeNode.create_from_path(full, True, self._excluded_target)
        if node.is_directory and not node.children:
            return
        self.target_roots.append(node)
        self.notify("trees")

    def add_non_target_path(self, path: str) -> None:
        if not os.path.exists(path):
            return
        full = os.path.abspath(path)
        self._excluded_non_target.discard(full)
        if any(normalize_path(r.full_path) == normalize_path(full) for r in self.non_target_roots):
            return
        node = SourceTreeNode.create_from_path(full, False, self._excluded_non_target)
        if node.is_directory and not node.children:
            return
        self.non_target_roots.append(node)
        self.notify("trees")

    def _target_file_keys(self) -> set[str]:
        return {normalize_path(f.full_path) for r in self.target_roots for f in r.enumerate_files()}

    def remove_node(self, node: SourceTreeNode, quiet: bool = False) -> None:
        was_target = node.is_target
        if node.parent is not None:
            parent = node.parent
            parent.children.remove(node)
            # 상위 폴더를 다시 스캔해도 제거한 항목이 되살아나지 않도록 제외 목록에 기록
            (self._excluded_target if was_target else self._excluded_non_target).add(node.full_path)
            # 하위 항목이 모두 제거되어 빈 폴더가 된 상위 노드는 연쇄 정리
            while parent is not None and not parent.children:
                upper = parent.parent
                if upper is not None:
                    upper.children.remove(parent)
                else:
                    for roots in (self.target_roots, self.non_target_roots):
                        if parent in roots:
                            roots.remove(parent)
                parent = upper
        else:
            for roots in (self.target_roots, self.non_target_roots):
                if node in roots:
                    roots.remove(node)

        if was_target:
            remaining = self._target_file_keys()
            self._all_raw_defects = [d for d in self._all_raw_defects if normalize_path(d.file_path) in remaining]
            self.refresh_filtered_defects()
        self.notify("trees")
        if not quiet:
            self.set_status(f"'{node.name}' 항목(및 하위 항목)을 목록에서 제거했습니다.")

    def move_node_to_non_target(self, node: SourceTreeNode) -> None:
        if not node.is_target:
            return
        self.remove_node(node, quiet=True)
        self.add_non_target_path(node.full_path)
        self.set_status(f"'{node.name}' 항목을 비대상/참조 코드로 이동했습니다.")

    def move_node_to_target(self, node: SourceTreeNode) -> None:
        if node.is_target:
            return
        self.remove_node(node, quiet=True)
        self._excluded_target.discard(node.full_path)
        parent = self._parent_root(node.full_path)
        if parent is not None:
            idx = self.target_roots.index(parent)
            self.target_roots[idx] = SourceTreeNode.create_from_path(parent.full_path, True, self._excluded_target)
            self._update_tree_badges()
            self.notify("trees")
        else:
            self.add_target_path(node.full_path)
        self.set_status(f"'{node.name}' 항목을 검사 대상 코드로 이동했습니다.")

    # ---------------------------------------------------------------- 분석
    def begin_analysis(self) -> AnalysisRequest | None:
        """분석 입력 스냅샷을 만든다. 시작할 수 없으면 상태 메시지를 남기고 None."""
        if self.is_analyzing:
            return None
        if self.selected_preset is None:
            self.set_status("타겟 프리셋을 먼저 선택해 주세요.")
            return None
        count = sum(1 for r in self.target_roots for _ in r.enumerate_files())
        if count == 0:
            self.set_status("검사 대상 코드에 추가된 소스 파일이 없습니다. 좌측 상단에 폴더나 파일을 추가해 주세요.")
            return None
        req = AnalysisRequest(self.selected_preset, self.c_standard, self.custom_defines,
                              self.force_all_ifdef_configs, self.enable_clang_tidy,
                              [r.clone_tree() for r in self.target_roots],
                              [r.clone_tree() for r in self.non_target_roots], list(self.all_rules))
        self._analysis = req
        self.notify("analyzing")
        self.set_status(f"[{self.selected_preset.name}] 프리셋으로 {count}개 파일 정적 분석 및 AST 검사 중... "
                        "(취소하려면 [■ 분석 취소])")
        return req

    def finish_analysis(self, req: AnalysisRequest, result: AnalysisRunResult | None = None,
                        error: BaseException | None = None, cancelled: bool = False) -> None:
        # 분석 도중 작업 공간이 바뀌었으면(새 프로젝트/불러오기/취소) 이전 결과는 버림
        current = self._analysis is req
        if current:
            self._analysis = None
            self.notify("analyzing")
        # 취소 요청 직후 분석이 먼저 끝난 경우도 취소로 안내한다.
        if cancelled or (not current and req.cancel.is_set()):
            self.set_status("분석이 취소되었습니다. (실행 중이던 분석 도구 프로세스는 종료되었습니다)")
            return
        if not current:
            return
        if error is not None:
            self.set_status(f"분석 중 오류 발생: {error}")
            return
        assert result is not None
        self._all_raw_defects = result.defects
        self._coverage = result.coverage_by_file
        self.notes = list(result.notes)
        self.refresh_filtered_defects()
        if self.current_code_file_path and os.path.isfile(self.current_code_file_path):
            self.load_file_into_code_viewer(self.current_code_file_path)
        else:
            first = next((f for r in self.target_roots for f in r.enumerate_files()), None)
            if first:
                self.load_file_into_code_viewer(first.full_path)
        self.select_issue(self.filtered_defects[0] if self.filtered_defects else None)
        self.set_status(f"분석 완료: 결함 {len(self.filtered_defects)}건 검출 (참고 노트 {len(self.notes)}건, "
                        f"예외 무시 {len(self.suppressed)}건)")

    def run_analysis_sync(self) -> None:
        """테스트·스크립트용 동기 실행."""
        req = self.begin_analysis()
        if req is None:
            return
        try:
            self.finish_analysis(req, result=req.run())
        except Exception as ex:  # noqa: BLE001
            self.finish_analysis(req, error=ex)

    def cancel_analysis(self) -> None:
        req = self._analysis
        if req is None:
            return
        self._analysis = None
        req.cancel.set()
        self.notify("analyzing")
        self.set_status("분석을 취소하고 있습니다...")

    # ---------------------------------------------------------------- 결과 필터·코드 뷰어
    def refresh_filtered_defects(self) -> None:
        # 엔진이 보고한 미등록 ID도 기본 ON으로 목록에 추가해 필터에서 유실되지 않게 한다.
        known = {r.id.lower(): r for r in self.all_rules}
        rules_added = False
        for issue in self._all_raw_defects:
            rule = known.get(issue.rule_id.lower())
            if rule is None:
                rule = issue.rule_details or RuleInfo.create_unregistered(issue.rule_id, issue.severity)
                self._add_rule(rule)
                known[rule.id.lower()] = rule
                rules_added = True
            issue.rule_details = rule

        suppressed_keys = {s.suppression_key for s in self.suppressed}
        enabled = {r.id.lower() for r in self.all_rules if r.is_enabled}
        q = self.search_text.strip().lower()
        filtered = []
        for d in self._all_raw_defects:
            if d.rule_id.lower() not in enabled or d.suppression_key in suppressed_keys:
                continue
            if self.category_filter and self.category_filter != ALL_CATEGORIES \
                    and d.category.lower() != self.category_filter.lower():
                continue
            if q and not any(q in s.lower() for s in (d.rule_id, d.title, d.message, d.file_name)):
                continue
            filtered.append(d)
        self.filtered_defects = filtered

        expanded = {g.rule_id.lower(): g.is_expanded for g in self.rule_issue_groups}
        groups = build_rule_summary(filtered)
        for g in groups:
            g.is_expanded = expanded.get(g.rule_id.lower(), g.is_expanded)
        self.rule_issue_groups = groups

        self._update_tree_badges()
        if self.current_code_file_path and os.path.isfile(self.current_code_file_path):
            self.load_file_into_code_viewer(self.current_code_file_path)
        self.notify("results", "trees", *(("rules",) if rules_added else ()))

    def set_search_text(self, text: str) -> None:
        if text != self.search_text:
            self.search_text = text
            self.refresh_filtered_defects()

    def set_category_filter(self, category: str) -> None:
        if category != self.category_filter:
            self.category_filter = category
            self.refresh_filtered_defects()

    def load_file_into_code_viewer(self, file_path: str) -> None:
        if not file_path or not os.path.isfile(file_path):
            return
        self.current_code_file_path = os.path.abspath(file_path)
        try:
            with open(self.current_code_file_path, encoding="utf-8", errors="replace") as f:
                raw_lines = f.read().splitlines()
        except OSError as ex:
            self.set_status(f"파일을 읽을 수 없습니다: {ex}")
            return
        key = normalize_path(self.current_code_file_path)
        coverage = self._coverage.get(key)

        def by_line(items):
            d: dict[int, list] = {}
            for it in items:
                if normalize_path(it.file_path) == key:
                    d.setdefault(it.line, []).append(it)
            return d

        defects_by_line = by_line(self.filtered_defects)
        suppressed_by_line = by_line(self.suppressed)
        raw_by_line = by_line(self._all_raw_defects)

        analyzed = skipped = defect_lines = suppressed_lines = 0
        lines = []
        for i, text in enumerate(raw_lines, start=1):
            cl = CodeLine(i, text)
            issues = defects_by_line.get(i)
            sup = suppressed_by_line.get(i)
            if issues:
                defect_lines += 1
                analyzed += 1
                cl.issues = issues
                cl.status = CodeLineStatus.ERROR if any(x.severity.lower() == "error" for x in issues) \
                    else CodeLineStatus.WARNING
                extra = f" +{len(issues) - 1}" if len(issues) > 1 else ""
                cl.inline_annotation = f"[{issues[0].rule_id}{extra}]"
                cl.tooltip_text = "\n\n".join(f"[{x.rule_id} - {x.severity_display}]\n{x.message}" for x in issues)
            elif sup:
                suppressed_lines += 1
                analyzed += 1
                cl.status = CodeLineStatus.SUPPRESSED
                cl.suppressed_entries = sup
                cl.issues = raw_by_line.get(i, [])
                extra = f" +{len(sup) - 1}" if len(sup) > 1 else ""
                cl.inline_annotation = f"[예외: {sup[0].rule_id}{extra}]"
                cl.tooltip_text = (f"[사용자에 의해 이 줄의 결함이 예외(무시) 처리됨]\n규칙: {sup[0].rule_id}\n"
                                   f"내용: {sup[0].message}\n(줄 우클릭 메뉴의 [↩ 다시 검사(복원)]으로 복원할 수 있습니다.)")
            elif coverage is not None:
                if i in coverage.analyzed_lines:
                    analyzed += 1
                    cl.status = CodeLineStatus.ANALYZED
                    cl.tooltip_text = "검사 완료 줄 (전처리 및 AST 정적 분석 수행됨)"
                elif i in coverage.skipped_lines:
                    skipped += 1
                    cl.status = CodeLineStatus.SKIPPED
                    cl.inline_annotation = "[미검사]"
                    cl.tooltip_text = "전처리기 조건(#ifdef/#if) 불충족으로 이번 분석에서 제외된 코드 줄입니다."
            lines.append(cl)
        self.current_file_lines = lines

        if coverage is not None:
            total = analyzed + skipped
            rate = analyzed * 100.0 / total if total else 100.0
            self.code_stats_summary = (f"총 {len(raw_lines)}줄  |  🟢 검사 완료: {analyzed}줄 ({rate:.0f}%)  |  "
                                       f"⚪ 미검사: {skipped}줄  |  🔴 결함: {defect_lines}줄  |  "
                                       f"🔇 예외 제외: {suppressed_lines}줄")
        else:
            self.code_stats_summary = f"총 {len(raw_lines)}줄  |  (아직 정적 분석이 실행되지 않았거나 비대상 파일입니다)"
        self.notify("code")

    def select_issue(self, issue: AnalysisIssue | None) -> None:
        self.selected_issue = issue
        self.notify("selection")

    def _update_tree_badges(self) -> None:
        counts: dict[str, int] = {}
        for d in self.filtered_defects:
            k = normalize_path(d.file_path)
            counts[k] = counts.get(k, 0) + 1

        def update(node: SourceTreeNode) -> int:
            if not node.is_directory:
                node.defect_count = counts.get(normalize_path(node.full_path), 0)
            else:
                node.defect_count = sum(update(c) for c in node.children)
            return node.defect_count

        for r in self.target_roots:
            update(r)

    # ---------------------------------------------------------------- 예외 처리
    def _add_suppression(self, issue: AnalysisIssue) -> bool:
        if self.is_issue_suppressed(issue):
            return False
        self.suppressed.append(SuppressedIssueEntry(issue.rule_id, issue.file_path, issue.line, issue.message))
        return True

    def toggle_selected_issue_suppression(self) -> None:
        issue = self.selected_issue
        if issue is None:
            return
        existing = next((s for s in self.suppressed if s.suppression_key == issue.suppression_key), None)
        if existing is not None:
            self.restore_suppressed_issue(existing)
        else:
            self.suppress_single_issue(issue)

    def suppress_code_line(self, line: CodeLine) -> None:
        if not line.issues:
            return
        for issue in list(line.issues):
            self._add_suppression(issue)
        self.refresh_filtered_defects()
        self.notify("selection")
        self.set_status(f"{line.line_number}번째 줄의 결함이 예외(무시) 처리되었습니다. 줄 우클릭 메뉴로 언제든 복원할 수 있습니다.")

    def restore_code_line(self, line: CodeLine) -> None:
        if not line.suppressed_entries:
            return
        for entry in list(line.suppressed_entries):
            if entry in self.suppressed:
                self.suppressed.remove(entry)
        self.refresh_filtered_defects()
        self.notify("selection")
        self.set_status(f"{line.line_number}번째 줄의 예외 처리가 해제되어 다시 결함 검사 대상으로 복원되었습니다.")

    def suppress_single_issue(self, issue: AnalysisIssue) -> None:
        if not self._add_suppression(issue):
            return
        self.refresh_filtered_defects()
        self.notify("selection")
        self.set_status(f"예외(무시) 처리됨: {issue.file_name}:{issue.line} [{issue.rule_id}]")

    def restore_suppressed_issue(self, entry: SuppressedIssueEntry) -> None:
        if entry in self.suppressed:
            self.suppressed.remove(entry)
        self.refresh_filtered_defects()
        self.notify("selection")
        self.set_status(f"예외 해제(복원)됨: {os.path.basename(entry.file_path)}:{entry.line} [{entry.rule_id}]")

    def restore_all_suppressed_issues(self) -> None:
        if not self.suppressed:
            return
        self.suppressed.clear()
        self.refresh_filtered_defects()
        self.notify("selection")
        self.set_status("모든 예외(무시) 처리 항목이 다시 결함 목록으로 복원되었습니다.")

    def disable_rule_entirely(self, rule_id: str) -> None:
        rule = self.find_rule(rule_id)
        if rule is not None:
            rule.is_enabled = False
        else:
            self._all_raw_defects = [d for d in self._all_raw_defects if d.rule_id.lower() != rule_id.lower()]
        self.refresh_filtered_defects()
        self.notify("rules")
        self.set_status(f"규칙 '{rule_id}' 전체가 현재 프로젝트 검사에서 제외(OFF)되었습니다.")

    def apply_rule_selection(self) -> None:
        """룰셋 창에서 ON/OFF를 바꾼 뒤 호출."""
        self.refresh_filtered_defects()
        self.notify("rules")

    # ---------------------------------------------------------------- 프로젝트 저장·불러오기
    def save_project(self, file_path: str) -> None:
        target_paths = [r.full_path for r in self.target_roots]
        non_target_paths = [r.full_path for r in self.non_target_roots]
        model = repository.ProjectFile(
            project_name=os.path.splitext(os.path.basename(file_path))[0],
            target_preset_id=self.selected_preset.id if self.selected_preset else DEFAULT_PRESET_ID,
            c_standard=self.c_standard,
            custom_defines=self.custom_defines,
            force_all_ifdef_configs=self.force_all_ifdef_configs,
            enable_clang_tidy=self.enable_clang_tidy,
            target_paths=target_paths,
            non_target_paths=non_target_paths,
            # 현재 루트 폴더 아래에 있는 제외 항목만 저장
            excluded_target_paths=sorted(p for p in self._excluded_target
                                         if any(is_under(p, r) for r in target_paths)),
            excluded_non_target_paths=sorted(p for p in self._excluded_non_target
                                             if any(is_under(p, r) for r in non_target_paths)),
            enabled_rule_ids=[r.id for r in self.all_rules if r.is_enabled],
            disabled_rule_ids=[r.id for r in self.all_rules if not r.is_enabled],
            suppressed_issues=list(self.suppressed),
        )
        try:
            repository.save_project(file_path, model)
        except OSError as ex:
            self.set_status(f"프로젝트 저장 실패: {ex}")
            return
        self.current_project_file_path = file_path
        self.notify("project")
        self.set_status(f"프로젝트 저장 완료: {file_path}")

    def load_project(self, file_path: str) -> None:
        try:
            model = repository.load_project(file_path)
        except (OSError, ValueError) as ex:
            self.set_status(f"프로젝트 파일을 불러올 수 없습니다 (파일 형식 오류): {ex}")
            return
        if model is None:
            self.set_status("프로젝트 파일을 불러올 수 없습니다.")
            return

        self.clear_workspace()
        self.current_project_file_path = file_path
        self.selected_preset = next((t for t in self.target_presets if t.id == model.target_preset_id),
                                    self.target_presets[0] if self.target_presets else None)
        self.c_standard = model.c_standard
        self.custom_defines = model.custom_defines
        self.force_all_ifdef_configs = model.force_all_ifdef_configs
        self.enable_clang_tidy = model.enable_clang_tidy

        # DB에 없는 ID도 프로젝트에 저장된 ON/OFF 선택을 재분석 전에 복원한다.
        known = {r.id.lower() for r in self.all_rules}
        for rid in list(model.enabled_rule_ids) + list(model.disabled_rule_ids or []):
            if rid and rid.strip() and rid.lower() not in known:
                known.add(rid.lower())
                self._add_rule(RuleInfo.create_unregistered(rid))
        enabled_set = {i.lower() for i in model.enabled_rule_ids}
        if model.disabled_rule_ids is not None:
            # 어느 목록에도 없는 규칙(저장 이후 DB에 새로 추가된 규칙)은 DB 기본값을 따른다.
            disabled_set = {i.lower() for i in model.disabled_rule_ids}
            for r in self.all_rules:
                k = r.id.lower()
                r.is_enabled = False if k in disabled_set else True if k in enabled_set else r.default_enabled
        else:
            # 이전 버전 프로젝트 파일(disabledRuleIds 없음): 켜진 규칙 목록 기준으로 복원
            for r in self.all_rules:
                r.is_enabled = r.id.lower() in enabled_set

        # 제외 목록을 먼저 복원해야 루트 폴더를 다시 스캔할 때 제거·이동한 하위 항목이 되살아나지 않음
        self._excluded_target.update(os.path.abspath(p) for p in model.excluded_target_paths if p.strip())
        self._excluded_non_target.update(os.path.abspath(p) for p in model.excluded_non_target_paths if p.strip())
        for p in model.target_paths:
            self.add_target_path(p)
        for p in model.non_target_paths:
            self.add_non_target_path(p)
        self.suppressed.extend(model.suppressed_issues)

        first = next((f for r in self.target_roots for f in r.enumerate_files()), None)
        if first:
            self.load_file_into_code_viewer(first.full_path)
        self.notify("trees", "results", "rules", "project", "options")
        self.set_status(f"프로젝트 불러오기 완료: {file_path}")

    # ---------------------------------------------------------------- 외부 도구
    def open_current_file_in_external_editor(self, line: int = 1) -> None:
        path = self.current_code_file_path
        if not path or not os.path.isfile(path):
            return
        try:
            editor = external.open_in_editor(path, line)
        except OSError as ex:
            self.set_status(f"외부 편집기 실행 실패: {ex}")
            return
        name = os.path.basename(path)
        if editor == "VS Code":
            self.set_status(f"VS Code로 열었습니다: {name} ({line}번째 줄)")
        else:
            self.set_status(f"외부 편집기({editor})로 열었습니다: {name} (VS Code가 설치되어 있지 않아 줄 이동은 지원되지 않습니다)")

    def open_current_file_folder(self) -> None:
        path = self.current_code_file_path
        if not path or not os.path.isfile(path):
            return
        try:
            external.open_folder(path)
        except OSError as ex:
            self.set_status(f"폴더 열기 실패: {ex}")
