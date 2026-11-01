"""외부 도구 프로세스 실행 보조: 취소 이벤트를 감시하며 실행하고, 취소되면 프로세스 그룹 전체를 종료한다."""

from __future__ import annotations

import os
import signal
import subprocess
import threading

from .. import paths


class AnalysisCancelled(Exception):
    """사용자가 분석을 취소함."""


def background_process_options() -> dict:
    """Linux: 취소 시 함께 종료할 수 있도록 새 프로세스 그룹. Windows: 실행할 때마다 콘솔 창이 뜨지 않게 한다."""
    if paths.IS_WINDOWS:
        return {"creationflags": getattr(subprocess, "CREATE_NO_WINDOW", 0)}
    return {"start_new_session": True}


def run_process(args: list[str], cwd: str, cancel: threading.Event | None = None) -> tuple[int, str, str]:
    if cancel is not None and cancel.is_set():
        raise AnalysisCancelled()
    proc = subprocess.Popen(
        args, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", **background_process_options(),
    )
    while True:
        try:
            out, err = proc.communicate(timeout=0.2)
            return proc.returncode, out, err
        except subprocess.TimeoutExpired:
            if cancel is not None and cancel.is_set():
                kill(proc)
                raise AnalysisCancelled()


def kill(proc: subprocess.Popen) -> None:
    try:
        if paths.IS_WINDOWS:
            proc.kill()  # 실행하는 도구는 하위 프로세스를 만들지 않으므로 해당 프로세스만 종료
        else:
            os.killpg(proc.pid, signal.SIGKILL)
        proc.communicate(timeout=5)
    except Exception:
        pass  # 이미 종료되었거나 접근 불가


def summarize_error_lines(output: str, max_lines: int = 3) -> str:
    """도구 출력에서 오류로 보이는 줄을 앞에서부터 최대 몇 줄만 뽑아 안내 메시지로 요약한다."""
    if not output or not output.strip():
        return ""
    lines = []
    for line in output.splitlines():
        t = line.strip()
        if not t or t.startswith("<"):
            continue
        if "error" in t.lower():
            lines.append(t[:300] + "..." if len(t) > 300 else t)
            if len(lines) >= max_lines:
                break
    return "원문: " + " / ".join(lines) if lines else ""
