"""외부 편집기·파일 관리자 열기.

- VS Code가 있으면 해당 줄로 이동하여 연다(-g 파일:줄).
- VS Code가 없으면 Windows는 메모장, Linux는 xdg-open(기본 편집기)으로 연다.
- 폴더 열기는 Windows 탐색기(파일 선택 상태) 또는 Linux xdg-open을 사용한다.
"""

from __future__ import annotations

import os
import shutil
import subprocess

from . import paths


def _spawn(args: list[str] | str) -> None:
    options = {"start_new_session": True} if not paths.IS_WINDOWS else {}
    subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **options)


def find_vscode() -> str | None:
    """VS Code 실행 파일 위치. Windows는 WPF 앱과 같이 code.cmd 위치와 기본 설치 경로에서 Code.exe를 찾는다."""
    if not paths.IS_WINDOWS:
        return shutil.which("code")
    candidates = []
    for d in os.environ.get("PATH", "").split(os.pathsep):
        d = d.strip()
        if d and os.path.isfile(os.path.join(d, "code.cmd")):
            # ...\Microsoft VS Code\bin\code.cmd → ...\Microsoft VS Code\Code.exe
            candidates.append(os.path.join(os.path.dirname(d.rstrip("\\/")), "Code.exe"))
    for base in (os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs"),
                 os.environ.get("ProgramFiles", ""), os.environ.get("ProgramFiles(x86)", "")):
        if base:
            candidates.append(os.path.join(base, "Microsoft VS Code", "Code.exe"))
    return next((c for c in candidates if os.path.isfile(c)), None)


def open_in_editor(file_path: str, line: int) -> str:
    """편집기로 파일을 열고 실행한 편집기 이름을 반환한다."""
    code = find_vscode()
    if code:
        try:
            _spawn([code, "-g", f"{file_path}:{max(1, line)}"])
            return "VS Code"
        except OSError:
            pass  # VS Code 실행 실패 시 메모장/기본 편집기로 대체
    if paths.IS_WINDOWS:
        _spawn(["notepad.exe", file_path])
        return "메모장"
    opener = shutil.which("xdg-open")
    if not opener:
        raise OSError("xdg-open을 찾을 수 없습니다.")
    _spawn([opener, file_path])
    return "기본 편집기"


def open_folder(file_path: str) -> None:
    """파일이 있는 폴더를 파일 관리자로 연다 (Windows는 해당 파일을 선택한 상태로)."""
    full = os.path.abspath(file_path)
    if paths.IS_WINDOWS:
        # explorer는 '/select,"경로"' 형식만 인식하므로 명령 문자열을 그대로 전달한다.
        _spawn(f'explorer.exe /select,"{full}"')
        return
    opener = shutil.which("xdg-open")
    if not opener:
        raise OSError("xdg-open을 찾을 수 없습니다.")
    _spawn([opener, os.path.dirname(full)])
