import os
import sys

import pytest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from jeoncheck import paths  # noqa: E402


@pytest.fixture(autouse=True)
def isolated_user_settings(tmp_path, monkeypatch):
    """테스트가 실제 사용자 설정(최근 프로젝트 목록)을 읽거나 덮어쓰지 않도록 임시 경로로 바꾼다."""
    settings = tmp_path / "user_settings" / "user_settings.json"
    monkeypatch.setattr(paths, "user_settings_path", lambda: settings)
    return settings
