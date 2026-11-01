"""분석 진행률 표시와 외부 도구 병렬 실행 테스트."""

import sys
import threading
import time

import pytest

from jeoncheck.engine import runner
from jeoncheck.engine.process import AnalysisCancelled, run_parallel, run_process
from jeoncheck.state import AppState


def test_run_parallel_keeps_task_order_and_reports_each_completion():
    done = []

    def task(i):
        def run():
            time.sleep(0.05 * (5 - i))  # 뒤 작업이 먼저 끝나도 결과는 입력 순서
            return i
        return run

    results = run_parallel([task(i) for i in range(5)], on_task_done=lambda c, idx: done.append((c, idx)),
                           max_workers=5)
    assert results == [0, 1, 2, 3, 4]
    assert [c for c, _ in done] == [1, 2, 3, 4, 5]
    assert sorted(idx for _, idx in done) == [0, 1, 2, 3, 4]


def test_run_parallel_runs_tasks_at_the_same_time():
    active, peak, lock = 0, 0, threading.Lock()

    def task():
        nonlocal active, peak
        with lock:
            active += 1
            peak = max(peak, active)
        time.sleep(0.1)
        with lock:
            active -= 1

    run_parallel([task] * 4, max_workers=4)
    assert peak > 1


def test_run_parallel_cancel_stops_remaining_tasks():
    cancel = threading.Event()
    started = []

    def task(i):
        def run():
            started.append(i)
            if i == 0:
                cancel.set()
                raise AnalysisCancelled()
            time.sleep(0.05)
        return run

    with pytest.raises(AnalysisCancelled):
        run_parallel([task(i) for i in range(20)], cancel, max_workers=1)
    assert started == [0]


def test_run_process_streams_stdout_lines():
    lines = []
    code, out, err = run_process([sys.executable, "-c", "import sys; print('a'); print('b'); "
                                  "sys.stderr.write('e' * 100000)"], ".", on_stdout_line=lines.append)
    assert code == 0 and lines == ["a", "b"] and out.splitlines() == ["a", "b"] and len(err) == 100000


def test_cppcheck_progress_lines_are_recognized():
    m = runner._CPPCHECK_PROGRESS_RE.match("3/35 files checked 8% done")
    assert (m.group("done"), m.group("total"), m.group("pct")) == ("3", "35", "8")
    m = runner._CPPCHECK_CHECKING_RE.match(r"Checking C:\src\lapi.c: LUA_CORE=1... ...")
    assert m.group("file") == r"C:\src\lapi.c"
    assert runner._CPPCHECK_CHECKING_RE.match("Checking /src/a.c ...").group("file") == "/src/a.c"


def test_progress_never_goes_backwards_and_reaches_100():
    events = []
    p = runner._Progress(lambda pct, msg: events.append(pct))
    for v in (10, 40, 30, 100, 120):
        p.report(v, "x")
    assert events == [10, 40, 40, 100, 100]


def test_state_progress_and_button_text(tmp_path):
    (tmp_path / "a.c").write_text("int a;\n", encoding="utf-8")
    s = AppState(load_sample=False)
    s.add_target_path(str(tmp_path))
    assert s.run_button_text == "▶ 정적 분석 시작"
    req = s.begin_analysis()
    s.report_progress(req, 42.4, "lapi.c 분석 중")
    assert s.analysis_progress_percent == 42.4
    assert s.run_button_text == "⏳ 정적 분석 중 (42%)"
    assert s.status_message == "[42%] lapi.c 분석 중"
    s.report_progress(req, 10, "뒤로 가지 않음")
    assert s.analysis_progress_percent == 42.4

    s.cancel_analysis()
    s.report_progress(req, 90, "취소된 분석의 늦은 알림")
    assert s.analysis_progress_percent == 0 and s.run_button_text == "▶ 정적 분석 시작"
