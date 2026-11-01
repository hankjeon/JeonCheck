"""외부 편집기·파일 관리자 열기 (Linux 데스크톱).

VS Code(`code`)가 PATH에 있으면 해당 줄로 이동하여 열고(-g 파일:줄), 없으면 xdg-open으로 기본 편집기를 연다.
"""

from __future__ import annotations

import os
import shutil
import subprocess


def _spawn(args: list[str]) -> None:
    subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     start_new_session=True)


def open_in_editor(file_path: str, line: int) -> str:
    """편집기로 파일을 열고 실행한 편집기 이름을 반환한다."""
    code = shutil.which("code")
    if code:
        try:
            _spawn([code, "-g", f"{file_path}:{max(1, line)}"])
            return "VS Code"
        except OSError:
            pass  # VS Code 실행 실패 시 기본 편집기로 대체
    opener = shutil.which("xdg-open")
    if not opener:
        raise OSError("xdg-open을 찾을 수 없습니다.")
    _spawn([opener, file_path])
    return "기본 편집기"


def open_folder(file_path: str) -> None:
    """파일이 있는 폴더를 파일 관리자로 연다."""
    opener = shutil.which("xdg-open")
    if not opener:
        raise OSError("xdg-open을 찾을 수 없습니다.")
    _spawn([opener, os.path.dirname(os.path.abspath(file_path))])
