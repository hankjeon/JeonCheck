"""저장소 데이터 폴더와 외부 분석 도구(Cppcheck·Clang-Tidy·Cppcheck addon) 경로 탐색."""

from __future__ import annotations

import os
import shutil
from functools import lru_cache
from pathlib import Path

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


def platform_xml_path(xml_file_name: str) -> Path:
    return data_directory() / "targets" / xml_file_name


def clang_tidy_include_directory() -> Path:
    """Clang-Tidy용 간이 표준 헤더(-isystem). Windows 앱과 공유한다."""
    return root_directory() / "tools" / "clang-tidy" / "include"


def _from_env_or_path(env_name: str, *names: str) -> str | None:
    override = os.environ.get(env_name)
    if override:
        return override if Path(override).is_file() else None
    for name in names:
        found = shutil.which(name)
        if found:
            return found
    return None


def cppcheck_executable() -> str | None:
    return _from_env_or_path("JEONCHECK_CPPCHECK", "cppcheck")


def clang_tidy_executable() -> str | None:
    names = ["clang-tidy"] + [f"clang-tidy-{v}" for v in range(30, 13, -1)]
    return _from_env_or_path("JEONCHECK_CLANG_TIDY", *names)


def cppcheck_addon_directory(cppcheck_exe: str | None = None) -> Path | None:
    """설치된 Cppcheck와 같은 버전의 addon 폴더(misra.py, cppcheckdata.py)를 찾는다."""
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
