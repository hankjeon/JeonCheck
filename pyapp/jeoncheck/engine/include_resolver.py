"""#include 경로와 실제 파일 위치가 어긋나도 상위 폴더 역추적과 임시 포워딩 헤더로 맞춰 주는 해석기."""

from __future__ import annotations

import os
import re
import shutil
import tempfile

from ..models import SourceTreeNode

INCLUDE_RE = re.compile(r'^\s*#\s*include\s*[<"]([^>"]+)[>"]', re.MULTILINE)


def _split(path: str) -> list[str]:
    return [p for p in re.split(r"[/\\]", path) if p]


def _native(rel: str) -> str:
    return rel.replace("/", os.sep).replace("\\", os.sep)


class IncludePathResolver:
    def __init__(self, target_roots: list[SourceTreeNode], non_target_roots: list[SourceTreeNode]):
        self._root = tempfile.mkdtemp(prefix="jeoncheck_vinc_")
        # 상대 경로(../../ 등)로 올라가는 #include도 대응할 수 있도록 3단계 하위 폴더를 기준점으로 사용
        self._base = os.path.join(self._root, "d0", "d1", "d2")
        self.include_directories: list[str] = []
        self._build(target_roots, non_target_roots)

    def __enter__(self) -> "IncludePathResolver":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def close(self) -> None:
        shutil.rmtree(self._root, ignore_errors=True)

    def _build(self, target_roots, non_target_roots) -> None:
        dirs: set[str] = set()
        files: list[str] = []

        def collect(node: SourceTreeNode) -> None:
            if node.is_directory:
                if os.path.isdir(node.full_path):
                    dirs.add(os.path.abspath(node.full_path))
                for c in node.children:
                    collect(c)
            elif os.path.isfile(node.full_path):
                f = os.path.abspath(node.full_path)
                files.append(f)
                dirs.add(os.path.dirname(f))

        for r in list(target_roots) + list(non_target_roots):
            collect(r)

        files = list(dict.fromkeys(files))
        by_name: dict[str, list[str]] = {}
        for f in files:
            by_name.setdefault(os.path.normcase(os.path.basename(f)), []).append(f)

        need_virtual = False
        for src in files:
            try:
                with open(src, encoding="utf-8", errors="replace") as fh:
                    content = fh.read()
            except OSError:
                continue
            src_dir = os.path.dirname(src)
            for m in INCLUDE_RE.finditer(content):
                raw = m.group(1).strip()
                if not raw:
                    continue
                # 1) 현재 파일 폴더나 등록된 -I 폴더에서 바로 찾아지는지 확인
                if self._resolvable(raw, src_dir, dirs):
                    continue
                # 2) 사용자가 추가한 파일 중 파일명이 일치하는 후보 검색
                candidates = by_name.get(os.path.normcase(os.path.basename(_native(raw))))
                if not candidates:
                    continue
                best = self._best_match(raw, candidates)
                # 2-A) #include "hal/spi_reg.h" → 실제 .../inc/hal/spi_reg.h 이면 .../inc를 -I에 추가
                ancestor = self._ancestor_dir(raw, best)
                if ancestor:
                    dirs.add(ancestor)
                    if self._resolvable(raw, src_dir, dirs):
                        continue
                # 2-B) 폴더 구조가 다르면 임시 폴더에 같은 상대 경로로 포워딩 헤더 생성
                if self._forwarding_header(raw, best):
                    need_virtual = True

        self.include_directories = sorted(dirs)
        if need_virtual and os.path.isdir(self._base):
            self.include_directories.append(self._base)

    @staticmethod
    def _resolvable(raw: str, src_dir: str, dirs: set[str]) -> bool:
        rel = _native(raw)
        return any(os.path.isfile(os.path.normpath(os.path.join(d, rel))) for d in [src_dir, *dirs] if d)

    @staticmethod
    def _best_match(raw: str, candidates: list[str]) -> str:
        if len(candidates) == 1:
            return candidates[0]
        inc = [p for p in _split(raw) if p not in (".", "..")]
        best, best_score = candidates[0], -1
        for cand in candidates:
            parts = _split(cand)
            score, i, j = 0, len(inc) - 1, len(parts) - 1
            while i >= 0 and j >= 0 and os.path.normcase(inc[i]) == os.path.normcase(parts[j]):
                score, i, j = score + 1, i - 1, j - 1
            if score > best_score:
                best, best_score = cand, score
        return best

    @staticmethod
    def _ancestor_dir(raw: str, actual: str) -> str | None:
        parts = _split(raw)
        if any(p in (".", "..") for p in parts) or not parts:
            return None
        suffix = os.path.join(*parts)
        # Windows는 경로 대소문자를 구분하지 않으므로 normcase로 비교한다 (Linux에서는 그대로)
        if os.path.normcase(actual).endswith(os.path.normcase(os.sep + suffix)):
            ancestor = actual[: len(actual) - len(suffix) - 1]
            if ancestor and os.path.isdir(ancestor):
                return os.path.abspath(ancestor)
        return None

    def _forwarding_header(self, raw: str, actual: str) -> bool:
        try:
            os.makedirs(self._base, exist_ok=True)
            stub = os.path.normpath(os.path.join(self._base, _native(raw)))
            # 임시 루트 밖으로 벗어나는 경로는 만들지 않는다.
            if not stub.startswith(self._root + os.sep):
                return False
            os.makedirs(os.path.dirname(stub), exist_ok=True)
            if not os.path.exists(stub):
                # 실제 헤더를 절대 경로로 포워딩하여 내부 상대 #include도 원래 폴더 기준으로 동작하게 한다.
                with open(stub, "w", encoding="utf-8") as fh:
                    fh.write(f"/* JeonCheck auto-mapped header for '{raw}' */\n"
                             f"#include \"{actual.replace(os.sep, '/')}\"\n")
            return True
        except OSError:
            return False
