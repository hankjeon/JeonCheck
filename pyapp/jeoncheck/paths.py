"""저장소 데이터 폴더와 외부 분석 도구(Cppcheck·Clang-Tidy·Cppcheck addon) 경로 탐색.

도구 탐색 순서: 환경변수 → (Windows) 저장소 tools/의 내장 도구 → PATH.
Linux에서는 tools/의 Windows 실행 파일을 쓰지 않고 apt 등으로 설치한 도구를 PATH에서 찾는다.
"""

from __future__ import annotations

import os
import shutil
from functools import lru_cache
from pathlib import Path

IS_WINDOWS = os.name == "nt"
PACKAGE_DIR = Path(__file__).resolve().parent
RESOURCES_DIR = PACKAGE_DIR / "resources"


@lru_cache(maxsize=1)
def root_directory() -> Path:
    """`data/`와 `samples/` 폴더가 있는 저장소 루트를 패키지 위치에서 위로 올라가며 찾는다."""
    for parent in PACKAGE_DIR.parents:
        if (parent / "data").is_dir() and (parent / "samples").is_dir():
            return parent
    return Path.cwd()


def data_directory() -> Path:
    return root_directory() / "data"


def targets_json_path() -> Path:
    return data_directory() / "targets" / "targets.json"


def rules_ko_json_path() -> Path:
    return data_directory() / "rules_ko.json"


def samples_directory() -> Path:
    return root_directory() / "samples"


def user_settings_path() -> Path:
    """사용자별 설정 파일(최근 프로젝트 목록).

    Windows는 WPF 앱과 같은 %LOCALAPPDATA%\\JeonCheck\\user_settings.json을 공유하고,
    Linux는 $XDG_CONFIG_HOME(기본 ~/.config)/JeonCheck/user_settings.json을 사용한다.
    """
    if IS_WINDOWS:
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    else:
        base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "JeonCheck" / "user_settings.json"


def platform_xml_path(xml_file_name: str) -> Path:
    return data_directory() / "targets" / xml_file_name


def clang_tidy_include_directory() -> Path:
    """Clang-Tidy용 간이 표준 헤더(-isystem). Windows 앱과 공유한다."""
    return root_directory() / "tools" / "clang-tidy" / "include"


def _find_tool(env_name: str, bundled: Path, *names: str) -> str | None:
    override = os.environ.get(env_name)
    if override:
        return override if Path(override).is_file() else None
    # Windows: WPF 앱과 같은 저장소 내장 도구(tools/)를 우선 사용한다.
    if IS_WINDOWS and bundled.is_file():
        return str(bundled)
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    return None


def bundled_cppcheck_path() -> Path:
    return root_directory() / "tools" / "cppcheck" / "cppcheck.exe"


def bundled_clang_tidy_path() -> Path:
    return root_directory() / "tools" / "clang-tidy" / "clang-tidy.exe"


def cppcheck_executable() -> str | None:
    return _find_tool("JEONCHECK_CPPCHECK", bundled_cppcheck_path(), "cppcheck")


def clang_tidy_executable() -> str | None:
    names = ["clang-tidy"] + [f"clang-tidy-{v}" for v in range(30, 13, -1)]
    return _find_tool("JEONCHECK_CLANG_TIDY", bundled_clang_tidy_path(), *names)


def cppcheck_addon_directory(cppcheck_exe: str | None = None) -> Path | None:
    """사용하는 Cppcheck와 같은 버전의 addon 폴더(misra.py, cppcheckdata.py)를 찾는다.

    Windows 내장 도구는 실행 파일 옆의 tools/cppcheck/addons(같은 버전)를 사용한다.
    """
    candidates: list[Path] = []
    override = os.environ.get("JEONCHECK_ADDON_DIR")
    if override:
        candidates.append(Path(override))
    exe = cppcheck_exe or cppcheck_executable()
    if exe:
        exe_dir = Path(exe).resolve().parent
        candidates += [exe_dir / "addons", exe_dir.parent / "share" / "cppcheck" / "addons"]
    candidates += [
        Path("/usr/share/cppcheck/addons"),
        Path("/usr/local/share/cppcheck/addons"),
        Path("/usr/lib/x86_64-linux-gnu/cppcheck/addons"),
        Path("/usr/lib/cppcheck/addons"),
    ]
    for c in candidates:
        if (c / "misra.py").is_file() and (c / "cppcheckdata.py").is_file():
            return c
    return None


def install_hint(tool: str) -> str:
    """도구를 찾지 못했을 때 OS에 맞는 안내 문구."""
    env = {"cppcheck": "JEONCHECK_CPPCHECK", "clang-tidy": "JEONCHECK_CLANG_TIDY"}[tool]
    if IS_WINDOWS:
        bundled = bundled_cppcheck_path() if tool == "cppcheck" else bundled_clang_tidy_path()
        return f"저장소의 내장 도구({bundled})가 있는지 확인하거나 {env} 환경변수로 경로를 지정하십시오."
    return f"'sudo apt install {tool}'로 설치하거나 {env} 환경변수로 경로를 지정하십시오."
