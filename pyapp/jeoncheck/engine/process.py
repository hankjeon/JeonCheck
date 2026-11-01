"""외부 도구 프로세스 실행 보조: 취소 이벤트를 감시하며 실행하고, 취소되면 프로세스 그룹 전체를 종료한다."""

from __future__ import annotations

import os
import signal
import subprocess
import threading
from concurrent.futures import FIRST_EXCEPTION, ThreadPoolExecutor, wait
from typing import Callable, TypeVar

from .. import paths

T = TypeVar("T")


class AnalysisCancelled(Exception):
    """사용자가 분석을 취소함."""


def background_process_options() -> dict:
    """Linux: 취소 시 함께 종료할 수 있도록 새 프로세스 그룹. Windows: 실행할 때마다 콘솔 창이 뜨지 않게 한다."""
    if paths.IS_WINDOWS:
        return {"creationflags": getattr(subprocess, "CREATE_NO_WINDOW", 0)}
    return {"start_new_session": True}


def run_process(args: list[str], cwd: str, cancel: threading.Event | None = None,
                on_stdout_line: Callable[[str], None] | None = None) -> tuple[int, str, str]:
    """도구를 실행하고 (종료 코드, 표준 출력, 표준 오류)를 반환한다.

    on_stdout_line을 주면 표준 출력을 줄 단위로 바로 전달한다 (Cppcheck 진행률 표시용).
    """
    if cancel is not None and cancel.is_set():
        raise AnalysisCancelled()
    proc = subprocess.Popen(
        args, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", **background_process_options(),
    )
    if on_stdout_line is None:
        while True:
            try:
                out, err = proc.communicate(timeout=0.2)
                return proc.returncode, out, err
            except subprocess.TimeoutExpired:
                if cancel is not None and cancel.is_set():
                    kill(proc)
                    raise AnalysisCancelled()

    # 표준 출력·표준 오류를 각각 별도 스레드에서 읽어 파이프가 가득 차 멈추는 일을 막는다.
    out_lines: list[str] = []
    err_chunks: list[str] = []

    def read_stdout() -> None:
        for line in proc.stdout:
            out_lines.append(line)
            try:
                on_stdout_line(line.rstrip("\r\n"))
            except Exception:  # noqa: BLE001  (진행률 표시 오류가 분석을 멈추지 않게 함)
                pass

    readers = [threading.Thread(target=read_stdout, daemon=True),
               threading.Thread(target=lambda: err_chunks.append(proc.stderr.read()), daemon=True)]
    for r in readers:
        r.start()
    while True:
        try:
            proc.wait(timeout=0.2)
            break
        except subprocess.TimeoutExpired:
            if cancel is not None and cancel.is_set():
                kill(proc)
                raise AnalysisCancelled()
    for r in readers:
        r.join()
    return proc.returncode, "".join(out_lines), "".join(err_chunks)


def default_parallelism() -> int:
    """동시에 실행할 외부 도구 프로세스 수 (CPU 코어 수)."""
    return max(1, os.cpu_count() or 4)


def run_parallel(tasks: list[Callable[[], T]], cancel: threading.Event | None = None,
                 on_task_done: Callable[[int, int], None] | None = None,
                 max_workers: int | None = None) -> list[T]:
    """작업을 CPU 코어 수만큼 동시에 실행하고, 결과를 작업 순서대로 반환한다.

    on_task_done(완료 개수, 방금 끝난 작업의 순번)은 작업이 하나 끝날 때마다 호출된다.
    한 작업이 실패하거나 취소되면 남은 작업은 시작하지 않고 그 예외를 그대로 전달한다.
    """
    if not tasks:
        return []
    workers = max(1, min(max_workers or default_parallelism(), len(tasks)))
    done_count = 0
    lock = threading.Lock()

    def run(index: int, task: Callable[[], T]) -> T:
        nonlocal done_count
        if cancel is not None and cancel.is_set():
            raise AnalysisCancelled()
        value = task()
        with lock:
            done_count += 1
            count = done_count
        if on_task_done is not None:
            on_task_done(count, index)
        return value

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(run, i, t) for i, t in enumerate(tasks)]
        wait(futures, return_when=FIRST_EXCEPTION)
        failed = next((f for f in futures if f.done() and not f.cancelled() and f.exception() is not None), None)
        if failed is not None:
            for f in futures:
                f.cancel()
            # 실행 중인 다른 도구도 바로 멈추도록 취소 신호를 보낸다.
            if cancel is not None and isinstance(failed.exception(), AnalysisCancelled):
                cancel.set()
            raise failed.exception()
        return [f.result() for f in futures]


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
