"""선택적 Clang-Tidy 분석 (CERT-C / bugprone / clang-analyzer). 누락 헤더는 임시 빈 헤더로 보완한다."""

from __future__ import annotations

import os
import re
import shutil
import tempfile
import threading
from typing import Callable

from .. import paths
from ..models import AnalysisIssue, RuleInfo, TargetPreset, make_position_key, normalize_path
from .process import run_parallel, run_process, summarize_error_lines

INCLUDE_QUOTE_RE = re.compile(r'^\s*#\s*include\s*["<]([^">]+)[" >]', re.MULTILINE)
# Windows 드라이브 문자(C:)가 있어도 없어도 해석한다.
DIAGNOSTIC_RE = re.compile(
    r"^(?P<file>(?:[A-Za-z]:)?[^:\r\n]+):(?P<line>\d+):(?P<col>\d+):\s+(?P<sev>warning|error):\s+"
    r"(?P<msg>.+?)\s+\[(?P<ids>[^\]]+)\]\s*$")


def split_defines(custom_defines: str) -> list[str]:
    result = []
    for d in re.split(r"[,; ]+", custom_defines or ""):
        d = d.strip()
        if d.startswith("-D"):
            d = d[2:]
        if d:
            result.append(d)
    return result


def run_clang_tidy(
    exe: str,
    preset: TargetPreset,
    c_standard: str,
    custom_defines: str,
    input_files: list[str],
    include_dirs: list[str],
    target_files: set[str],
    rules_by_id: dict[str, RuleInfo],
    position_keys: set[str],
    notes: list[AnalysisIssue],
    cancel: threading.Event | None = None,
    progress: Callable[[int, int, str], None] | None = None,
) -> list[AnalysisIssue]:
    """파일별로 Clang-Tidy를 CPU 코어 수만큼 동시에 실행한다. progress(완료 개수, 전체 개수, 파일명)."""
    if not input_files:
        return []
    stub_root = tempfile.mkdtemp(prefix="jeoncheck_stubs_")
    # 상대 경로(../ 등)로 올라가는 #include도 임시 폴더 안에서 해석되도록 3단계 하위 폴더를 기준점으로 사용
    stub_base = os.path.join(stub_root, "d0", "d1", "d2")
    try:
        os.makedirs(stub_base, exist_ok=True)
        builtin_inc = str(paths.clang_tidy_include_directory())
        _create_missing_header_stubs(input_files, include_dirs, builtin_inc, stub_root, stub_base)

        compile_args = ["-xc", f"-std={c_standard}", f"--target={preset.clang_target or 'armv7r-none-eabi'}",
                        "-nostdinc"]
        if os.path.isdir(builtin_inc):
            compile_args += ["-isystem", builtin_inc]
        for inc in include_dirs:
            compile_args += ["-I", inc]
        # 누락 헤더 더미 폴더를 마지막 검색 경로로 추가하여 fatal error 방지
        compile_args += ["-I", stub_base]
        compile_args += [f"-D{d.strip()}" for d in preset.defines if d.strip()]
        compile_args += [f"-D{d}" for d in split_defines(custom_defines)]
        compile_args.append("-Wno-everything")

        cwd = str(paths.root_directory())
        total = len(input_files)

        def task(file: str):
            return lambda: run_process([exe, "-checks=-*,bugprone-*,cert-*,clang-analyzer-*", "--quiet", file, "--",
                                        *compile_args], cwd=cwd, cancel=cancel)

        def on_done(count: int, index: int) -> None:
            if progress is not None:
                progress(count, total, os.path.basename(input_files[index]))

        outputs = run_parallel([task(f) for f in input_files], cancel, on_done)

        # 헤더의 같은 진단은 여러 파일에서 반복되므로 입력 순서대로 해석하며 위치 키로 중복을 제거한다.
        issues: list[AnalysisIssue] = []
        failed: list[tuple[str, int, str]] = []
        for file, (code, out, err) in zip(input_files, outputs):
            output = out + "\n" + err
            issues += parse_clang_tidy_output(output, target_files, rules_by_id, position_keys)
            if code != 0:
                failed.append((os.path.basename(file), code, summarize_error_lines(output)))
        if failed:
            # WPF 앱과 같은 형식: 실패한 파일 목록과 첫 실패 파일의 종료 코드·원문 요약
            names = ", ".join(name for name, _, _ in failed)
            _, first_code, first_summary = failed[0]
            notes.append(AnalysisIssue(
                rule_id="clangTidyExit", category="포함 파일 / 환경 안내", severity="Info", obligation="참고(Note)",
                title="Clang-Tidy 비정상 종료 안내",
                message=(f"[Clang-Tidy 안내] {len(failed)}개 파일({names})에서 Clang-Tidy가 종료 코드 {first_code}(으)로 "
                         f"끝났습니다. 컴파일 오류가 있는 파일은 Clang-Tidy 결과가 일부 누락되었을 수 있습니다. {first_summary}").strip(),
                is_info_or_note=True))
        return issues
    finally:
        shutil.rmtree(stub_root, ignore_errors=True)


def _find_header(inc: str, current_dir: str, include_dirs: list[str], builtin_inc: str) -> str | None:
    for d in [current_dir, *include_dirs, builtin_inc]:
        if d:
            cand = os.path.join(d, inc)
            if os.path.isfile(cand):
                return os.path.abspath(cand)
    return None


def _create_missing_header_stubs(input_files, include_dirs, builtin_inc, stub_root, stub_base) -> None:
    queue = list(input_files)
    scanned: set[str] = set()
    prefix = os.path.abspath(stub_root).rstrip(os.sep) + os.sep
    while queue:
        current = queue.pop(0)
        if not os.path.isfile(current) or current in scanned:
            continue
        scanned.add(current)
        try:
            with open(current, encoding="utf-8", errors="replace") as fh:
                content = fh.read()
        except OSError:
            continue
        for m in INCLUDE_QUOTE_RE.finditer(content):
            inc = m.group(1).strip()
            if not inc:
                continue
            found = _find_header(inc, os.path.dirname(current), include_dirs, builtin_inc)
            if found:
                queue.append(found)
                continue
            rel = inc.replace("/", os.sep).replace("\\", os.sep)
            # 절대 경로 #include는 더미 생성 대상에서 제외 (사용자 디스크에 파일을 만들지 않음)
            if os.path.isabs(rel):
                continue
            stub = os.path.normpath(os.path.join(stub_base, rel))
            if not stub.startswith(prefix):
                continue  # 임시 더미 폴더 밖으로 벗어나면 생성하지 않음
            os.makedirs(os.path.dirname(stub), exist_ok=True)
            if not os.path.exists(stub):
                with open(stub, "w", encoding="utf-8") as fh:
                    fh.write("/* Auto-generated stub header for clang-tidy analysis */\n")


def parse_clang_tidy_output(output: str, target_files: set[str], rules_by_id: dict[str, RuleInfo],
                            position_keys: set[str]) -> list[AnalysisIssue]:
    issues: list[AnalysisIssue] = []
    for line_text in (output or "").splitlines():
        m = DIAGNOSTIC_RE.match(line_text)
        if not m:
            continue
        file = m.group("file").strip()
        line, col = int(m.group("line")), int(m.group("col"))
        raw_msg = m.group("msg").strip()
        sev = "Error" if m.group("sev") == "error" else "Warning"
        if line <= 0 or normalize_path(file) not in target_files:
            continue
        ids = [x.strip() for x in m.group("ids").split(",")
               if x.strip() and not x.strip().lower().startswith("clang-diagnostic-")]
        if not ids:
            continue
        # 축소 변환은 자체 검사 규칙과 ON/OFF를 공유한다.
        if "bugprone-narrowing-conversions" in ids:
            ids = ["implicitNarrowing"]
        # 실수형 루프 카운터 진단은 CERT FLP30-C로 표시
        if any("float-loop-counter" in i.lower() or "floatloopcounter" in i.lower() or i.lower() == "cert-flp30-c"
               for i in ids):
            ids = ["cert-flp30-c"]
        # 별칭이 함께 붙은 경우 등록된 ID 중 ON인 것을 우선하고, 등록된 별칭이 모두 OFF이면 보고하지 않음
        registered = [i for i in ids if i in rules_by_id]
        if registered and all(not rules_by_id[i].is_enabled for i in registered):
            continue
        selected = next((i for i in registered if rules_by_id[i].is_enabled), None) \
            or next((i for i in ids if i.lower().startswith("cert-")), None) or ids[0]
        rule = rules_by_id.get(selected)
        if rule is not None and not rule.is_enabled:
            continue
        if rule is None:
            rule = RuleInfo.create_unregistered(selected, sev, "Clang-Tidy")
        if make_position_key(selected, file, line, col) in position_keys:
            continue
        position_keys.add(make_position_key(selected, file, line, col))
        msg = f"{rule.summary} ({raw_msg})" if rule.summary and rule.summary != raw_msg else raw_msg
        issues.append(AnalysisIssue(
            rule_id=selected, category=rule.category, severity=sev if rule.is_unregistered else rule.severity,
            obligation=rule.obligation, title=rule.title, message=msg, file_path=os.path.abspath(file),
            line=line, column=col, rule_details=rule))
    return issues
