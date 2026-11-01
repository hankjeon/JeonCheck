"""실제 Cppcheck·misra.py로 샘플을 분석하는 통합 테스트 (도구가 없으면 건너뜀)."""

import shutil

import pytest

from jeoncheck import paths
from jeoncheck.state import AppState

pytestmark = pytest.mark.skipif(
    paths.cppcheck_executable() is None or paths.cppcheck_addon_directory() is None,
    reason="cppcheck 또는 addon(misra.py)이 설치되어 있지 않음")


@pytest.fixture
def sample_copy(tmp_path):
    dst = tmp_path / "samples"
    shutil.copytree(paths.samples_directory(), dst)
    return dst


def run_sample(sample_copy, clang_tidy=False):
    s = AppState(load_sample=False)
    s.enable_clang_tidy = clang_tidy
    s.add_target_path(str(sample_copy / "src"))
    s.add_non_target_path(str(sample_copy / "include"))
    s.add_non_target_path(str(sample_copy / "drivers"))
    s.run_analysis_sync()
    return s


def test_sample_analysis_detects_custom_misra_and_cppcheck(sample_copy):
    s = run_sample(sample_copy)
    assert s.status_message.startswith("분석 완료"), s.status_message
    ids = {d.rule_id for d in s.filtered_defects}
    # 자체 검사 (Python 포팅)
    assert {"implicitNarrowing", "implicitSignConversion", "explicitNarrowingCast"} <= ids
    # 원본 misra.py
    assert {"misra-c2012-15.6", "misra-c2012-16.4", "misra-c2012-17.7"} <= ids
    # Cppcheck 기본 검사와 9.1 매핑
    assert {"arrayIndexOutOfBounds", "uninitvar", "misra-c2012-9.1"} <= ids
    # 노트: 누락 헤더
    assert any(n.rule_id == "missingInclude" for n in s.notes)
    # 샘플에서 misra.py가 보고하는 규칙은 모두 한글 해설 DB에 등록되어 있음
    unregistered = sorted({d.rule_id for d in s.filtered_defects
                           if d.rule_id.startswith("misra-") and d.rule_details.is_unregistered})
    assert unregistered == []

    # #ifdef로 제외된 줄은 미검사로 표시
    s.load_file_into_code_viewer(str(sample_copy / "src" / "dsp_motor_ctrl.c"))
    assert s.current_file_lines[29].status.value == "skipped"

    # 소스 옆에 dump 산출물이 남지 않음
    leftovers = [p for p in sample_copy.rglob("*") if p.suffix in (".dump", ".ctu-info") or "jeoncheck-backup" in p.name]
    assert leftovers == []


def test_existing_dump_file_is_restored(sample_copy):
    existing = sample_copy / "src" / "dsp_motor_ctrl.c.dump"
    existing.write_text("user file", encoding="utf-8")
    run_sample(sample_copy)
    assert existing.read_text(encoding="utf-8") == "user file"


@pytest.mark.skipif(paths.clang_tidy_executable() is None, reason="clang-tidy 미설치")
def test_sample_analysis_with_clang_tidy(sample_copy):
    s = run_sample(sample_copy, clang_tidy=True)
    ids = {d.rule_id for d in s.filtered_defects}
    assert "bugprone-branch-clone" in ids


@pytest.mark.parametrize("clang_tidy", [False, True])
def test_real_analysis_reports_progress_up_to_100(sample_copy, clang_tidy):
    if clang_tidy and paths.clang_tidy_executable() is None:
        pytest.skip("clang-tidy 미설치")
    s = AppState(load_sample=False)
    s.enable_clang_tidy = clang_tidy
    s.add_target_path(str(sample_copy / "src"))
    req = s.begin_analysis()
    events = []
    req.progress = lambda pct, msg: events.append((pct, msg))
    s.finish_analysis(req, result=req.run())
    percents = [p for p, _ in events]
    assert percents == sorted(percents) and percents[-1] == 100.0
    messages = " ".join(m for _, m in events)
    assert "Cppcheck 분석 중" in messages and "AST 및 MISRA 정밀 분석 중 (1/1): dsp_motor_ctrl.c" in messages
    assert ("Clang-Tidy 심층 분석 중 (1/1)" in messages) == clang_tidy


def test_cancel_after_dump_creation_cleans_up(sample_copy, monkeypatch):
    from jeoncheck.engine import runner
    from jeoncheck.engine.process import AnalysisCancelled

    s = AppState(load_sample=False)
    s.enable_clang_tidy = False
    s.add_target_path(str(sample_copy / "src"))
    req = s.begin_analysis()
    original = runner._PassContext._run_misra

    def cancel_then_run(self, *args):
        # dump가 생성된 뒤 취소: 다음 외부 프로세스 실행에서 취소 예외가 발생해야 한다.
        assert list(sample_copy.rglob("*.dump"))
        req.cancel.set()
        original(self, *args)

    monkeypatch.setattr(runner._PassContext, "_run_misra", cancel_then_run)
    with pytest.raises(AnalysisCancelled):
        req.run()
    s.finish_analysis(req, cancelled=True)
    assert s.status_message.startswith("분석이 취소되었습니다")
    assert not s.is_analyzing
    assert list(sample_copy.rglob("*.dump")) == []
