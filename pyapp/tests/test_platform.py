"""OS별 도구 탐색·외부 도구 실행 분기 테스트. Windows 분기는 paths.IS_WINDOWS를 바꿔 확인한다.

Linux에서 Windows 분기의 선택 결과와 명령 구성만 확인하며, Windows에서의 실제 실행 검증을 대신하지 않는다.
"""

import os
import shutil

import pytest

from jeoncheck import external, paths
from jeoncheck.engine import process


@pytest.fixture
def clean_env(monkeypatch):
    for name in ("JEONCHECK_CPPCHECK", "JEONCHECK_CLANG_TIDY", "JEONCHECK_ADDON_DIR"):
        monkeypatch.delenv(name, raising=False)


def test_windows_prefers_bundled_tools_and_same_version_addons(monkeypatch, clean_env):
    monkeypatch.setattr(paths, "IS_WINDOWS", True)
    monkeypatch.setattr(shutil, "which", lambda name: f"/elsewhere/{name}")
    root = paths.root_directory()
    assert paths.cppcheck_executable() == str(root / "tools" / "cppcheck" / "cppcheck.exe")
    assert paths.clang_tidy_executable() == str(root / "tools" / "clang-tidy" / "clang-tidy.exe")
    assert paths.cppcheck_addon_directory() == (root / "tools" / "cppcheck" / "addons").resolve()


def test_linux_does_not_use_windows_executables(monkeypatch, clean_env):
    monkeypatch.setattr(paths, "IS_WINDOWS", False)
    monkeypatch.setattr(shutil, "which", lambda name: None)
    assert paths.cppcheck_executable() is None
    assert paths.clang_tidy_executable() is None


def test_environment_variable_overrides_bundled_tool(monkeypatch, tmp_path, clean_env):
    exe = tmp_path / "cppcheck.exe"
    exe.write_text("")
    monkeypatch.setattr(paths, "IS_WINDOWS", True)
    monkeypatch.setenv("JEONCHECK_CPPCHECK", str(exe))
    assert paths.cppcheck_executable() == str(exe)


def test_install_hint_is_os_specific(monkeypatch):
    monkeypatch.setattr(paths, "IS_WINDOWS", True)
    assert "tools" in paths.install_hint("cppcheck") and "apt" not in paths.install_hint("cppcheck")
    monkeypatch.setattr(paths, "IS_WINDOWS", False)
    assert "sudo apt install clang-tidy" in paths.install_hint("clang-tidy")


def test_process_options(monkeypatch):
    monkeypatch.setattr(paths, "IS_WINDOWS", True)
    assert set(process.background_process_options()) == {"creationflags"}
    monkeypatch.setattr(paths, "IS_WINDOWS", False)
    assert process.background_process_options() == {"start_new_session": True}


@pytest.fixture
def windows_editor_env(monkeypatch, tmp_path):
    monkeypatch.setattr(paths, "IS_WINDOWS", True)
    monkeypatch.setenv("PATH", "")
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "local"))
    monkeypatch.setenv("ProgramFiles", str(tmp_path / "pf"))
    monkeypatch.setenv("ProgramFiles(x86)", str(tmp_path / "pf86"))
    spawned = []
    monkeypatch.setattr(external, "_spawn", spawned.append)
    return tmp_path, spawned


def test_windows_editor_falls_back_to_notepad(windows_editor_env):
    _tmp, spawned = windows_editor_env
    assert external.open_in_editor("C:/src/a.c", 12) == "메모장"
    assert spawned == [["notepad.exe", "C:/src/a.c"]]


def test_windows_editor_uses_vscode_with_line(windows_editor_env):
    tmp, spawned = windows_editor_env
    code = tmp / "local" / "Programs" / "Microsoft VS Code" / "Code.exe"
    code.parent.mkdir(parents=True)
    code.write_text("")
    assert external.open_in_editor("C:/src/a.c", 12) == "VS Code"
    assert spawned == [[str(code), "-g", "C:/src/a.c:12"]]


def test_windows_vscode_found_from_code_cmd_on_path(windows_editor_env, monkeypatch):
    tmp, _spawned = windows_editor_env
    install = tmp / "custom" / "Microsoft VS Code"
    (install / "bin").mkdir(parents=True)
    (install / "bin" / "code.cmd").write_text("")
    (install / "Code.exe").write_text("")
    monkeypatch.setenv("PATH", str(install / "bin"))
    assert external.find_vscode() == os.path.join(str(install), "Code.exe")


def test_windows_folder_opens_explorer_with_selection(windows_editor_env):
    _tmp, spawned = windows_editor_env
    external.open_folder("/proj/src/a.c")
    assert spawned == [f'explorer.exe /select,"{os.path.abspath("/proj/src/a.c")}"']
