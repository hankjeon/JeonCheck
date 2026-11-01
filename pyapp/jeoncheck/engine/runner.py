"""분석 통합 실행기: Cppcheck(XML + dump) → misra.py → 자체 검사·커버리지 → 선택적 Clang-Tidy → 병합.

WPF 앱의 CppcheckRunner와 같은 흐름이며, MISRA 검사는 설치된 Cppcheck의 원본 misra.py를 사용한다.
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
import threading
import uuid
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass, field

from .. import paths
from ..models import (AnalysisIssue, FileLineCoverage, RuleInfo, SourceTreeNode, TargetPreset,
                      make_position_key, normalize_path)
from .clang_tidy import run_clang_tidy, split_defines
from .include_resolver import IncludePathResolver
from .process import AnalysisCancelled, run_process, summarize_error_lines

DUMP_WORKER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dump_worker.py")
NOTE_CATEGORY = "포함 파일 / 환경 안내"

# MISRA C 2012 Rule 9.1(초기화 전 값 사용)에 해당하는 Cppcheck 결함 ID (misra.py도 Cppcheck 검사에 위임)
MISRA_91_SOURCE_IDS = {"uninitvar", "uninitdata", "uninitStructMember"}
IGNORED_CPPCHECK_IDS = {"checkersReport", "unusedFunction", "unmatchedSuppression", "purgedConfiguration"}
ANALYSIS_LIMIT_NOTES = {
    "toomanyconfigs": ("#ifdef 설정 조합 일부만 검사됨",
                       "#ifdef 조합이 너무 많아 Cppcheck가 일부 설정만 검사했습니다. 모든 분기를 검사하려면 상단의 "
                       "[모든 #ifdef 분기 강제 검사(--force)] 옵션을 켜십시오."),
    "normalCheckLevelMaxBranches": ("분기 분석이 제한됨", "함수가 복잡하여 일부 분기의 값 흐름 분석이 제한되었습니다."),
    "normalCheckLevelConditionExpressions": ("조건식 분석이 제한됨", "복잡한 조건식의 분석이 제한되었습니다."),
    "checkLevelNormal": ("분석 깊이 제한", "분석이 너무 복잡하여 일부 검사가 제한되었습니다."),
}
_LIMIT_NOTES_LOWER = {k.lower(): v for k, v in ANALYSIS_LIMIT_NOTES.items()}


class ToolNotFoundError(RuntimeError):
    pass


@dataclass
class AnalysisRunResult:
    defects: list[AnalysisIssue] = field(default_factory=list)
    notes: list[AnalysisIssue] = field(default_factory=list)
    coverage_by_file: dict[str, FileLineCoverage] = field(default_factory=dict)
    command_line_summary: str = ""


def normalize_severity(sev: str) -> str:
    s = (sev or "").lower()
    if s == "error":
        return "Error"
    if s in ("warning", "performance", "portability"):
        return "Warning"
    if s == "style":
        return "Style"
    return "Info"


def create_note(rule_id: str, title: str, message: str, file_path: str = "", line: int = 0,
                column: int = 0) -> AnalysisIssue:
    return AnalysisIssue(rule_id=rule_id, category=NOTE_CATEGORY, severity="Info", obligation="참고(Note)",
                         title=title, message=message, file_path=file_path, line=line, column=column,
                         is_info_or_note=True)


def issue_from_rule(rule: RuleInfo, rule_id: str, message: str, file: str, line: int, column: int,
                    severity: str | None = None) -> AnalysisIssue:
    return AnalysisIssue(rule_id=rule_id, category=rule.category, severity=severity or rule.severity,
                         obligation=rule.obligation, title=rule.title, message=message,
                         file_path=os.path.abspath(file), line=line, column=column, rule_details=rule)


class _Keys:
    """분석 전체에서 공유하는 중복 제거 키."""

    def __init__(self):
        self.position: set[str] = set()
        self.notes: set[str] = set()

    def try_add(self, rule_id: str, file: str, line: int, column: int) -> bool:
        key = make_position_key(rule_id, file, line, column)
        if key in self.position:
            return False
        self.position.add(key)
        return True


class _DumpFileGuard:
    """Cppcheck가 소스 옆에 만드는 .dump/.ctu-info 보호: 기존 파일은 백업했다가 끝나면 복원한다."""

    def __init__(self):
        self._entries: list[tuple[str, str | None]] = []
        self._unprotected: set[str] = set()

    def protect(self, path: str) -> None:
        if path in self._unprotected or any(p == path for p, _ in self._entries):
            return
        backup = None
        if os.path.exists(path):
            backup = f"{path}.jeoncheck-backup-{uuid.uuid4().hex}"
            try:
                os.replace(path, backup)
            except OSError:
                # 옮기지 못하면 기존 파일을 이번 결과로 사용하지도, 삭제하지도 않는다.
                self._unprotected.add(path)
                return
        self._entries.append((path, backup))

    def generated_by_this_run(self, path: str) -> bool:
        return path not in self._unprotected and os.path.exists(path)

    def close(self) -> None:
        for path, backup in self._entries:
            try:
                if os.path.exists(path):
                    os.remove(path)
                if backup and os.path.exists(backup):
                    os.replace(backup, path)
            except OSError:
                pass  # 백업 파일('*.jeoncheck-backup-*')은 남아 수동 복구 가능
        self._entries.clear()


def _is_header(path: str) -> bool:
    return path.lower().endswith((".h", ".hpp"))


def run_analysis(
    preset: TargetPreset,
    c_standard: str,
    custom_defines: str,
    force_all_ifdef_configs: bool,
    enable_clang_tidy: bool,
    target_roots: list[SourceTreeNode],
    non_target_roots: list[SourceTreeNode],
    all_rules: list[RuleInfo],
    cancel: threading.Event | None = None,
) -> AnalysisRunResult:
    result = AnalysisRunResult()
    cppcheck = paths.cppcheck_executable()
    if not cppcheck:
        raise ToolNotFoundError("Cppcheck 실행 파일을 찾을 수 없습니다. " + paths.install_hint("cppcheck"))
    addon_dir = paths.cppcheck_addon_directory(cppcheck)
    if addon_dir is None:
        raise ToolNotFoundError("Cppcheck addon(misra.py, cppcheckdata.py) 폴더를 찾을 수 없습니다. "
                                "JEONCHECK_ADDON_DIR 환경변수로 경로를 지정하십시오.")

    # 1. 검사 대상 파일 수집
    target_files = list(dict.fromkeys(f.full_path for r in target_roots for f in r.enumerate_files()))
    if not target_files:
        return result
    target_norm = {normalize_path(f) for f in target_files}

    # 실제 입력 파일: .c/.cpp 우선, 없으면 대상 파일 직접 검사
    input_files = [f for f in target_files if f.lower().endswith((".c", ".cpp"))]
    headers_excluded = bool(input_files)
    if not input_files:
        input_files = list(target_files)

    rules_by_id = {r.id: r for r in all_rules}
    keys = _Keys()
    base_dir = str(paths.root_directory())

    with IncludePathResolver(target_roots, non_target_roots) as include_resolver:
        include_dirs = include_resolver.include_directories

        # 2. Cppcheck 공통 인자
        base_args = ["--enable=all", "--inconclusive", "--check-level=exhaustive", "--xml", "--dump",
                     f"--std={c_standard}"]
        if force_all_ifdef_configs:
            base_args.append("--force")
        platform_xml = paths.platform_xml_path(preset.platform_xml) if preset.platform_xml else None
        if platform_xml and platform_xml.is_file():
            base_args.append(f"--platform={platform_xml}")
        base_args += [f"-D{d.strip()}" for d in preset.defines if d.strip()]
        base_args += [f"-D{d}" for d in split_defines(custom_defines)]
        for inc in include_dirs:
            base_args += ["-I", inc]
        result.command_line_summary = "cppcheck " + " ".join(
            f'"{a}"' if " " in a else a for a in base_args + input_files)

        ctx = _PassContext(cppcheck, str(addon_dir), base_args, base_dir, target_norm, rules_by_id, result, keys,
                           cancel)
        clang_inputs = list(input_files)
        guard = _DumpFileGuard()
        targets_json = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        try:
            json.dump(sorted(target_norm), targets_json)
            targets_json.close()
            ctx.targets_json = targets_json.name

            # 3~5. 1차 분석: 소스 파일 기준
            ctx.run_pass(input_files, guard)

            # 5-1. 2차 분석: 어느 소스에도 포함되지 않은 대상 헤더를 직접 검사
            if headers_excluded:
                uncovered = [h for h in target_files
                             if _is_header(h) and normalize_path(h) not in result.coverage_by_file]
                if uncovered:
                    names = ", ".join(os.path.basename(h) for h in uncovered)
                    result.notes.append(create_note(
                        "standaloneHeader", "단독 헤더 직접 분석 안내",
                        f"[분석 범위 안내] 어느 소스 파일(.c)에도 포함되지 않은 검사 대상 헤더 {len(uncovered)}개({names})를 "
                        "단독으로 직접 분석했습니다. 소스 문맥(앞서 포함되는 헤더의 타입·매크로 정의) 없이 분석되므로 일부 결과가 "
                        "실제와 다를 수 있습니다."))
                    ctx.run_pass(uncovered, guard)
                    clang_inputs += uncovered

            # 6. Clang-Tidy (옵션이 켜져 있고 실행 파일이 있을 때)
            clang_tidy = paths.clang_tidy_executable() if enable_clang_tidy else None
            if clang_tidy:
                result.defects += run_clang_tidy(clang_tidy, preset, c_standard, custom_defines, clang_inputs,
                                                 include_dirs, target_norm, rules_by_id, keys.position, result.notes,
                                                 cancel)
            elif enable_clang_tidy:
                result.notes.append(create_note(
                    "clangTidyMissing", "Clang-Tidy 미설치 안내",
                    "[Clang-Tidy 안내] clang-tidy 실행 파일을 찾지 못해 Clang-Tidy 분석을 건너뛰었습니다. "
                    + paths.install_hint("clang-tidy")))
        finally:
            guard.close()
            os.unlink(targets_json.name)

    # 정렬: 파일명 -> 줄 -> 열 -> 규칙
    result.defects.sort(key=lambda d: (d.file_name.lower(), d.line, d.column, d.rule_id))
    return result


class _PassContext:
    def __init__(self, cppcheck, addon_dir, base_args, base_dir, target_norm, rules_by_id, result, keys, cancel):
        self.cppcheck = cppcheck
        self.addon_dir = addon_dir
        self.base_args = base_args
        self.base_dir = base_dir
        self.target_norm = target_norm
        self.rules_by_id = rules_by_id
        self.result = result
        self.keys = keys
        self.cancel = cancel
        self.targets_json = ""

    def abs_path(self, path: str) -> str:
        if not path:
            return ""
        return os.path.abspath(path if os.path.isabs(path) else os.path.join(self.base_dir, path))

    def run_pass(self, input_files: list[str], guard: _DumpFileGuard) -> None:
        """Cppcheck 1회 실행: 프로세스 실행 → XML 결과 해석 → dump별 misra.py·자체 검사·커버리지."""
        for f in input_files:
            guard.protect(f + ".dump")
            guard.protect(f + ".ctu-info")

        code, out, err = run_process([self.cppcheck, *self.base_args, *input_files], self.base_dir, self.cancel)
        if not parse_cppcheck_xml(err, self.target_norm, self.rules_by_id, self.result, self.keys, self.abs_path):
            detail = summarize_error_lines(out + err)
            if not detail:
                first = next((l.strip() for l in (out + err).splitlines() if l.strip()), "")
                detail = "원문: " + first if first else ""
            raise RuntimeError(f"Cppcheck 실행 결과(XML)를 해석할 수 없습니다 (종료 코드 {code}). {detail}".strip())
        if code != 0:
            self.result.notes.append(create_note(
                "cppcheckExit", "Cppcheck 비정상 종료 안내",
                f"[Cppcheck 안내] Cppcheck가 종료 코드 {code}(으)로 끝났습니다. 일부 파일의 분석 결과가 누락되었을 수 있습니다. "
                f"{summarize_error_lines(out)}".strip()))

        for src in input_files:
            if self.cancel is not None and self.cancel.is_set():
                raise AnalysisCancelled()
            dump = src + ".dump"
            name = os.path.basename(src)
            if not guard.generated_by_this_run(dump):
                self.result.notes.append(create_note(
                    "dumpMissing", "AST 덤프 파일 미생성",
                    f"[분석 범위 안내] '{name}'의 AST 덤프(.dump) 파일이 생성되지 않아 라인별 검사 상태 표시와 MISRA·축소 변환 "
                    "검사를 수행하지 못했습니다. 소스 폴더에 쓰기 권한이 있는지, 구문 오류가 없는지 확인하세요.", src))
                continue
            self._run_misra(dump, name, src)
            self._run_worker(dump, name, src)

    def _run_misra(self, dump: str, name: str, src: str) -> None:
        misra_py = os.path.join(self.addon_dir, "misra.py")
        code, out, err = run_process([sys.executable, misra_py, "--cli", dump], self.base_dir, self.cancel)
        violations = parse_misra_cli_output(out)
        if code != 0 and not violations:
            self.result.notes.append(create_note(
                "misraAddonError", "MISRA addon 실행 실패",
                f"[분석 범위 안내] '{name}'에 대한 misra.py 실행이 종료 코드 {code}(으)로 실패하여 MISRA 검사 결과가 없습니다. "
                f"{summarize_error_lines(err) or err.strip()[-300:]}".strip(), src))
            return
        for v in violations:
            file = self.abs_path(v["file"])
            if normalize_path(file) not in self.target_norm:
                continue
            rule_id = v["ruleId"]
            rule = self.rules_by_id.get(rule_id)
            if rule is not None and not rule.is_enabled:
                continue
            if rule is None:
                rule = RuleInfo.create_unregistered(rule_id, normalize_severity(v["severity"]), "MISRA addon")
                rule.title = f"MISRA C 2012 Rule {rule_id.removeprefix('misra-c2012-')}"
            if not self.keys.try_add(rule_id, file, v["line"], v["column"]):
                continue
            raw = f"misra.py 검출: {rule.title if rule.is_unregistered else 'MISRA C 2012 Rule ' + rule_id.removeprefix('misra-c2012-')}"
            msg = f"{rule.summary} ({raw})" if rule.summary else raw
            sev = normalize_severity(v["severity"]) if rule.is_unregistered else None
            self.result.defects.append(issue_from_rule(rule, rule_id, msg, file, v["line"], v["column"], sev))

    def _run_worker(self, dump: str, name: str, src: str) -> None:
        code, out, err = run_process(
            [sys.executable, DUMP_WORKER, dump, "--addon-dir", self.addon_dir, "--targets", self.targets_json,
             "--base-dir", self.base_dir], self.base_dir, self.cancel)
        try:
            data = json.loads(out)
        except json.JSONDecodeError:
            self.result.notes.append(create_note(
                "dumpParseError", "AST 덤프 파일 해석 실패",
                f"[분석 범위 안내] '{name}'의 AST 덤프 파일을 해석하지 못해 라인별 검사 상태 표시와 축소 변환 검사를 건너뛰었습니다. "
                f"(종료 코드 {code}: {err.strip()[-300:]})", src))
            return

        for file, cov in data.get("coverage", {}).items():
            new = FileLineCoverage(set(cov["analyzed"]), set(cov["skipped"]), set(cov["directive"]))
            key = normalize_path(file)
            if key in self.result.coverage_by_file:
                self.result.coverage_by_file[key].merge(new)
            else:
                new.skipped_lines -= new.analyzed_lines
                self.result.coverage_by_file[key] = new

        for i in data.get("issues", []):
            rule = self.rules_by_id.get(i["ruleId"])
            if rule is None:
                rule = RuleInfo.create_unregistered(i["ruleId"])
            if not rule.is_enabled or not self.keys.try_add(i["ruleId"], i["file"], i["line"], i["column"]):
                continue
            self.result.defects.append(issue_from_rule(rule, i["ruleId"], i["message"], i["file"], i["line"],
                                                       i["column"]))


def parse_misra_cli_output(output: str) -> list[dict]:
    """misra.py --cli의 JSON 줄에서 위반만 추출한다 (logChecker·summary 줄 제외)."""
    result = []
    for line in (output or "").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            d = json.loads(line)
        except json.JSONDecodeError:
            continue
        if "summary" in d or d.get("errorId") == "logChecker" or d.get("severity") == "none":
            continue
        error_id = d.get("errorId") or ""
        if not error_id or not d.get("file"):
            continue
        result.append({"ruleId": f"{d.get('addon', 'misra')}-{error_id}", "file": d["file"],
                       "line": int(d.get("linenr") or 0), "column": int(d.get("column") or 0),
                       "severity": d.get("severity", "style")})
    return result


def parse_cppcheck_xml(xml_text: str, target_norm: set[str], rules_by_id: dict[str, RuleInfo],
                       result: AnalysisRunResult, keys: _Keys, abs_path=os.path.abspath) -> bool:
    """Cppcheck XML 결과 해석. XML이 없거나 손상되었으면 False (결함 0건으로 오인하지 않도록)."""
    if not xml_text or not xml_text.strip():
        return False
    start = xml_text.find("<?xml")
    if start < 0:
        return False
    try:
        root = ElementTree.fromstring(xml_text[start:].encode("utf-8"))
    except ElementTree.ParseError:
        return False
    errors = root.find("errors")
    if errors is None:
        return False

    for e in errors.findall("error"):
        eid = e.get("id", "")
        severity = e.get("severity", "warning")
        raw_msg = e.get("msg", "")
        if not eid or eid in IGNORED_CPPCHECK_IDS:
            continue
        loc = e.find("location")
        file = loc.get("file", "") if loc is not None else ""
        line = int(loc.get("line", 0)) if loc is not None else 0
        col = int(loc.get("column", 0)) if loc is not None else 0
        full = abs_path(file) if file else ""

        # 0) 분석 범위 제한 안내는 노트로 분리
        limit = _LIMIT_NOTES_LOWER.get(eid.lower())
        if limit:
            if f"{eid}|{normalize_path(full)}|{line}" not in keys.notes:
                keys.notes.add(f"{eid}|{normalize_path(full)}|{line}")
                result.notes.append(create_note(eid, limit[0], f"[분석 범위 안내] {limit[1]} (Cppcheck: {raw_msg})",
                                                full, line, col))
            continue

        # 1) 누락된 포함 파일은 노트로 분리
        if eid in ("missingInclude", "missingIncludeSystem"):
            note_key = f"{eid}|{normalize_path(full)}|{line}|{raw_msg}"
            if note_key not in keys.notes:
                keys.notes.add(note_key)
                if eid == "missingInclude":
                    title = "사용자 포함 파일(#include) 미발견"
                    msg = (f"[참조 헤더 누락 안내] {raw_msg} (해당 헤더 없이 가능한 범위까지 분석을 계속 수행했습니다. "
                           "필요 시 비대상/참조 코드에 헤더 폴더를 추가하세요.)")
                else:
                    title = "시스템 표준 헤더 참조 안내"
                    msg = (f"[표준 헤더 누락 안내] {raw_msg} (내장 라이브러리 모델을 활용해 가능한 범위를 분석했습니다. "
                           "모델은 실제 헤더의 타입·선언·매크로를 완전히 대체하지 않으므로 결과가 제한될 수 있습니다. "
                           "필요 시 참조 코드에 해당 타겟의 헤더 폴더를 추가하세요.)")
                result.notes.append(create_note(eid, title, msg, full, line, col))
            continue

        # 2) 검사 대상 파일에 속하지 않은 결함은 제외
        if not file or normalize_path(full) not in target_norm:
            continue

        # 2-1) 초기화되지 않은 값 사용은 MISRA 9.1로도 보고 (9.1 규칙의 ON/OFF를 따름)
        misra91 = rules_by_id.get("misra-c2012-9.1")
        if eid in MISRA_91_SOURCE_IDS and misra91 is not None and misra91.is_enabled \
                and keys.try_add(misra91.id, full, line, col):
            result.defects.append(issue_from_rule(misra91, misra91.id, f"{misra91.summary} (Cppcheck {eid}: {raw_msg})",
                                                  full, line, col))

        # 3) 등록된 규칙의 ON/OFF 확인, 미등록 ID는 공통 안내로 감싼다
        rule = rules_by_id.get(eid)
        if rule is not None and not rule.is_enabled:
            continue
        if rule is None:
            rule = RuleInfo.create_unregistered(eid, normalize_severity(severity), "Cppcheck")
        if not keys.try_add(eid, full, line, col):
            continue
        msg = f"{rule.summary} ({raw_msg})" if rule.summary else raw_msg
        sev = normalize_severity(severity) if rule.is_unregistered else None
        result.defects.append(issue_from_rule(rule, eid, msg, full, line, col, sev))
    return True
