"""타겟 프리셋·한글 규칙 DB 로드와 프로젝트 파일(*.jcproj) 저장/로드. WPF 앱과 같은 JSON 스키마를 사용한다."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from datetime import datetime

from . import paths
from .models import RuleInfo, SuppressedIssueEntry, TargetPreset


def load_target_presets() -> list[TargetPreset]:
    p = paths.targets_json_path()
    if not p.is_file():
        return []
    data = json.loads(p.read_text(encoding="utf-8-sig"))
    return [TargetPreset.from_json(t) for t in data.get("targets", [])]


def load_rules() -> list[RuleInfo]:
    p = paths.rules_ko_json_path()
    if not p.is_file():
        return []
    data = json.loads(p.read_text(encoding="utf-8-sig"))
    return [RuleInfo.from_json(r) for r in data.get("rules", [])]


@dataclass
class ProjectFile:
    project_name: str = "새 정적분석 프로젝트"
    version: str = "1.0"
    target_preset_id: str = "ti-c6678-eabi"
    c_standard: str = "c99"
    custom_defines: str = ""
    force_all_ifdef_configs: bool = False
    enable_clang_tidy: bool = True
    target_paths: list[str] = field(default_factory=list)
    non_target_paths: list[str] = field(default_factory=list)
    excluded_target_paths: list[str] = field(default_factory=list)
    excluded_non_target_paths: list[str] = field(default_factory=list)
    enabled_rule_ids: list[str] = field(default_factory=list)
    # None이면 이전 버전 파일 (enabledRuleIds만 있음)
    disabled_rule_ids: list[str] | None = None
    suppressed_issues: list[SuppressedIssueEntry] = field(default_factory=list)


def _to_stored_path(full_path: str, base_dir: str) -> str:
    """프로젝트 파일 기준 상대 경로로 저장한다(세 단계 이상 위로 올라가면 절대 경로 유지)."""
    if not full_path or not base_dir:
        return full_path
    try:
        rel = os.path.relpath(full_path, base_dir)
    except ValueError:
        return full_path
    if rel.startswith(os.path.join("..", "..", "..")) or os.path.isabs(rel):
        return full_path
    return rel


def _to_resolved_path(stored: str, base_dir: str) -> str:
    if not stored or not stored.strip():
        return stored
    # Windows에서 저장한 프로젝트의 '\' 구분자도 읽을 수 있게 한다.
    if os.sep == "/":
        stored = stored.replace("\\", "/")
    if os.path.isabs(stored) or not base_dir:
        return os.path.abspath(stored)
    return os.path.abspath(os.path.join(base_dir, stored))


def save_project(file_path: str, project: ProjectFile) -> None:
    base = os.path.dirname(os.path.abspath(file_path))
    rel = lambda p: _to_stored_path(p, base)  # noqa: E731
    doc = {
        "projectName": project.project_name,
        "version": project.version,
        "savedAt": datetime.now().astimezone().isoformat(timespec="seconds"),
        "targetPresetId": project.target_preset_id,
        "cStandard": project.c_standard,
        "customDefines": project.custom_defines,
        "forceAllIfdefConfigs": project.force_all_ifdef_configs,
        "enableClangTidy": project.enable_clang_tidy,
        "targetPaths": [rel(p) for p in project.target_paths],
        "nonTargetPaths": [rel(p) for p in project.non_target_paths],
        "excludedTargetPaths": [rel(p) for p in project.excluded_target_paths],
        "excludedNonTargetPaths": [rel(p) for p in project.excluded_non_target_paths],
        "enabledRuleIds": list(project.enabled_rule_ids),
        "disabledRuleIds": None if project.disabled_rule_ids is None else list(project.disabled_rule_ids),
        "suppressedIssues": [s.to_json(rel(s.file_path)) for s in project.suppressed_issues],
    }
    with open(file_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)


def load_project(file_path: str) -> ProjectFile | None:
    if not os.path.isfile(file_path):
        return None
    base = os.path.dirname(os.path.abspath(file_path))
    with open(file_path, encoding="utf-8-sig") as f:
        d = json.load(f)
    # WPF 앱은 대소문자 무시로 읽으므로 키 이름도 대소문자 무시로 찾는다.
    lower = {k.lower(): v for k, v in d.items()}

    def get(key: str, default=None):
        return lower.get(key.lower(), default)

    res = lambda p: _to_resolved_path(p, base)  # noqa: E731
    suppressed = []
    for s in get("suppressedIssues") or []:
        entry = SuppressedIssueEntry.from_json({k[0].lower() + k[1:]: v for k, v in s.items()})
        entry.file_path = res(entry.file_path)
        suppressed.append(entry)
    disabled = get("disabledRuleIds")
    return ProjectFile(
        project_name=get("projectName", "새 정적분석 프로젝트"),
        version=get("version", "1.0"),
        target_preset_id=get("targetPresetId", "ti-c6678-eabi"),
        c_standard=get("cStandard", "c99"),
        custom_defines=get("customDefines", "") or "",
        force_all_ifdef_configs=bool(get("forceAllIfdefConfigs", False)),
        enable_clang_tidy=bool(get("enableClangTidy", True)),
        target_paths=[res(p) for p in get("targetPaths") or []],
        non_target_paths=[res(p) for p in get("nonTargetPaths") or []],
        excluded_target_paths=[res(p) for p in get("excludedTargetPaths") or []],
        excluded_non_target_paths=[res(p) for p in get("excludedNonTargetPaths") or []],
        enabled_rule_ids=list(get("enabledRuleIds") or []),
        disabled_rule_ids=None if disabled is None else list(disabled),
        suppressed_issues=suppressed,
    )
