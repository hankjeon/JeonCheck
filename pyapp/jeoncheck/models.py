"""분석 결과·규칙·타겟·소스 트리 데이터 모델 (WPF 앱의 Models 폴더와 같은 의미)."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from enum import Enum
from typing import Iterator

SOURCE_EXTENSIONS = (".c", ".h", ".cpp", ".hpp")


def normalize_path(path: str) -> str:
    """경로 비교 키. Linux에서는 대소문자를 구분하므로 소문자로 바꾸지 않는다(Windows에서만 normcase)."""
    if not path or not path.strip():
        return ""
    return os.path.normcase(os.path.abspath(path)).replace("\\", "/")


def is_under(path: str, root: str) -> bool:
    """path가 root 폴더의 하위 경로인지 (root 자신은 제외)."""
    p, r = normalize_path(path), normalize_path(root).rstrip("/")
    return p.startswith(r + "/")


def make_position_key(rule_id: str, file_path: str, line: int, column: int) -> str:
    """결함 중복 제거 키 (규칙 + 파일 + 줄 + 열)."""
    return f"{rule_id}|{normalize_path(file_path)}|{line}|{column}"


SEVERITY_DISPLAY = {"Error": "오류", "Warning": "경고", "Style": "권고", "Info": "안내", "Information": "안내"}


def severity_display(severity: str) -> str:
    return SEVERITY_DISPLAY.get(severity, severity)


def format_obligation(rule_id: str, obligation: str) -> str:
    if obligation == "참고(Note)":
        return "참고 안내"
    if rule_id.lower().startswith("misra-c2012-"):
        return f"MISRA 의무: {obligation}"
    # Cppcheck/CERT/자체 규칙의 프로젝트 검토 정책을 MISRA 의무 등급과 구분한다.
    policy = {
        "치명(Critical)": "우선 검토",
        "필수(Required)": "필수 검토",
        "강제(Mandatory)": "필수 검토",
        "권고(Advisory)": "권고 검토",
    }.get(obligation, obligation)
    return f"프로젝트 정책: {policy}"


@dataclass
class TargetPreset:
    id: str = ""
    name: str = ""
    vendor: str = ""
    architecture: str = ""
    endian: str = "little"
    pointer_bits: int = 32
    int_bits: int = 32
    long_bits: int = 32
    platform_xml: str = ""
    clang_target: str = "armv7r-none-eabi"
    default_std: str = "c99"
    defines: list[str] = field(default_factory=list)
    description: str = ""

    @classmethod
    def from_json(cls, d: dict) -> "TargetPreset":
        return cls(
            id=d.get("id", ""),
            name=d.get("name", ""),
            vendor=d.get("vendor", ""),
            architecture=d.get("architecture", ""),
            endian=d.get("endian", "little"),
            pointer_bits=int(d.get("pointerBits", 32)),
            int_bits=int(d.get("intBits", 32)),
            long_bits=int(d.get("longBits", 32)),
            platform_xml=d.get("platformXml", ""),
            clang_target=d.get("clangTarget", "armv7r-none-eabi"),
            default_std=d.get("defaultStd", "c99"),
            defines=list(d.get("defines") or []),
            description=d.get("description", ""),
        )


@dataclass(eq=False)
class RuleInfo:
    id: str = ""
    category: str = ""
    severity: str = "Warning"
    obligation: str = "필수(Required)"
    default_enabled: bool = True
    title: str = ""
    summary: str = ""
    description: str = ""
    bad_example: str = ""
    good_example: str = ""
    is_unregistered: bool = False
    is_enabled: bool = True

    @classmethod
    def from_json(cls, d: dict) -> "RuleInfo":
        default_enabled = bool(d.get("defaultEnabled", True))
        return cls(
            id=d.get("id", ""),
            category=d.get("category", ""),
            severity=d.get("severity", "Warning"),
            obligation=d.get("obligation", "필수(Required)"),
            default_enabled=default_enabled,
            title=d.get("title", ""),
            summary=d.get("summary", ""),
            description=d.get("description", ""),
            bad_example=d.get("badExample", ""),
            good_example=d.get("goodExample", ""),
            is_enabled=default_enabled,
        )

    @classmethod
    def create_unregistered(cls, rule_id: str, severity: str = "Warning", engine: str = "분석 도구") -> "RuleInfo":
        category = {"Cppcheck": "Cppcheck 기본 검사", "Clang-Tidy": "Clang-Tidy / CERT-C",
                    "MISRA addon": "MISRA C 2012"}.get(engine, "미등록 진단")
        return cls(
            id=rule_id,
            category=category,
            severity=severity,
            obligation="미분류",
            title=f"{engine} 검출 ({rule_id})",
            summary="개별 한글 해설이 아직 등록되지 않은 진단입니다. 원문과 해당 코드의 타입·값·실행 경로를 확인하십시오.",
            # 발생 위치마다 다른 원문은 AnalysisIssue.message에 보존한다.
            description=(f"이 {engine} 세부 규칙의 개별 한글 해설과 코드 예시는 아직 등록되지 않았습니다. "
                         "선택한 항목의 검출 메시지에 표시된 원문에서 문제가 된 연산과 발생 조건을 확인한 뒤 원인에 맞게 수정하십시오."),
            is_unregistered=True,
            default_enabled=True,
            is_enabled=True,
        )

    @property
    def severity_display(self) -> str:
        return severity_display(self.severity)

    @property
    def obligation_display(self) -> str:
        return format_obligation(self.id, self.obligation)

    @property
    def bad_example_display(self) -> str:
        return self.bad_example if self.bad_example.strip() else \
            "이 규칙의 위반 코드 예시는 아직 등록되지 않았습니다. 상세 설명과 원문 진단을 확인하십시오."

    @property
    def good_example_display(self) -> str:
        return self.good_example if self.good_example.strip() else \
            "이 규칙의 개선 코드 예시는 아직 등록되지 않았습니다. 실제 코드의 원인에 맞는 수정이 필요합니다."


@dataclass(eq=False)
class AnalysisIssue:
    rule_id: str = ""
    category: str = ""
    severity: str = "Warning"  # Error, Warning, Style, Info
    obligation: str = "필수(Required)"
    title: str = ""
    message: str = ""
    file_path: str = ""
    line: int = 0
    column: int = 0
    is_info_or_note: bool = False
    rule_details: RuleInfo | None = None

    @property
    def file_name(self) -> str:
        return os.path.basename(self.file_path) if self.file_path else "-"

    @property
    def severity_display(self) -> str:
        return severity_display(self.severity)

    @property
    def review_severity(self) -> str:
        if self.severity.lower() == "error":
            return "Error"
        if self.severity.lower() == "warning" and "권고" not in self.obligation.lower() \
                and "advisory" not in self.obligation.lower():
            return "Warning"
        return "Advisory"

    @property
    def review_severity_display(self) -> str:
        return {"Error": "오류", "Warning": "경고", "Advisory": "권고"}[self.review_severity]

    @property
    def obligation_display(self) -> str:
        return format_obligation(self.rule_id, self.obligation)

    @property
    def suppression_key(self) -> str:
        return f"{self.rule_id}|{normalize_path(self.file_path)}|{self.line}"


@dataclass(eq=False)
class SuppressedIssueEntry:
    rule_id: str = ""
    file_path: str = ""
    line: int = 0
    message: str = ""

    @property
    def suppression_key(self) -> str:
        return f"{self.rule_id}|{normalize_path(self.file_path)}|{self.line}"

    def to_json(self, file_path: str | None = None) -> dict:
        return {"ruleId": self.rule_id, "filePath": self.file_path if file_path is None else file_path,
                "line": self.line, "message": self.message}

    @classmethod
    def from_json(cls, d: dict) -> "SuppressedIssueEntry":
        return cls(d.get("ruleId", ""), d.get("filePath", ""), int(d.get("line", 0)), d.get("message", ""))


@dataclass
class FileLineCoverage:
    analyzed_lines: set[int] = field(default_factory=set)
    skipped_lines: set[int] = field(default_factory=set)
    directive_lines: set[int] = field(default_factory=set)

    def merge(self, other: "FileLineCoverage") -> None:
        """여러 설정·번역 단위의 분석 줄은 합집합으로 병합한다."""
        self.analyzed_lines |= other.analyzed_lines
        self.directive_lines |= other.directive_lines
        self.skipped_lines |= other.skipped_lines
        self.skipped_lines -= self.analyzed_lines


@dataclass(eq=False)
class SourceTreeNode:
    name: str = ""
    full_path: str = ""
    is_directory: bool = False
    is_target: bool = False
    parent: "SourceTreeNode | None" = None
    children: list["SourceTreeNode"] = field(default_factory=list)
    defect_count: int = 0

    def enumerate_files(self) -> Iterator["SourceTreeNode"]:
        if not self.is_directory:
            yield self
            return
        for child in self.children:
            yield from child.enumerate_files()

    def clone_tree(self, parent: "SourceTreeNode | None" = None) -> "SourceTreeNode":
        """백그라운드 분석에 넘기기 위한 트리 스냅샷."""
        clone = SourceTreeNode(self.name, self.full_path, self.is_directory, self.is_target, parent)
        clone.children = [c.clone_tree(clone) for c in self.children]
        return clone

    @classmethod
    def create_from_path(cls, path: str, is_target: bool, excluded: set[str] | None = None) -> "SourceTreeNode":
        full = os.path.abspath(path)
        is_dir = os.path.isdir(full)
        node = cls(os.path.basename(full.rstrip("/\\")), full, is_dir, is_target)
        if not is_dir:
            return node
        excluded_keys = {normalize_path(p) for p in (excluded or ())}
        try:
            entries = sorted(os.listdir(full))
        except OSError:
            return node  # 접근 권한 없는 폴더 무시
        dirs = [e for e in entries if os.path.isdir(os.path.join(full, e))]
        files = [e for e in entries if os.path.isfile(os.path.join(full, e))]
        for d in dirs:
            sub = os.path.join(full, d)
            if normalize_path(sub) in excluded_keys:
                continue
            child = cls.create_from_path(sub, is_target, excluded)
            if child.children:
                child.parent = node
                node.children.append(child)
        for f in files:
            if os.path.splitext(f)[1].lower() not in SOURCE_EXTENSIONS:
                continue
            fp = os.path.join(full, f)
            if normalize_path(fp) in excluded_keys:
                continue
            node.children.append(cls(f, fp, False, is_target, node))
        return node


@dataclass(eq=False)
class RuleIssueOccurrence:
    # 원본 진단을 유지하여 같은 줄의 여러 항목도 정확한 상세 설명으로 연결한다.
    issue: AnalysisIssue
    code_text: str = ""

    @property
    def location(self) -> str:
        i = self.issue
        return f"{i.file_path}:{i.line}:{i.column}" if i.column > 0 else f"{i.file_path}:{i.line}"


@dataclass(eq=False)
class RuleIssueGroup:
    rule_id: str = ""
    title: str = ""
    occurrences: list[RuleIssueOccurrence] = field(default_factory=list)
    is_expanded: bool = False

    @property
    def count(self) -> int:
        return len(self.occurrences)

    @property
    def header(self) -> str:
        return f"{self.rule_id} · {self.title} · {self.count}건"


class CodeLineStatus(Enum):
    NEUTRAL = "neutral"        # 공백·주석·분석 정보 없는 줄
    ANALYZED = "analyzed"      # 분석 토큰이 있는 줄
    SKIPPED = "skipped"        # 원본 코드 토큰은 있으나 분석 토큰이 없는 줄
    WARNING = "warning"        # 경고·규칙 위반 줄
    ERROR = "error"            # Error 진단이 있는 줄
    ADVISORY = "advisory"      # 권고(Style/Advisory) 규칙 위반 줄
    SUPPRESSED = "suppressed"  # 활성 진단 없이 예외 항목만 있는 줄


@dataclass(eq=False)
class CodeLine:
    line_number: int
    code_text: str
    status: CodeLineStatus = CodeLineStatus.NEUTRAL
    inline_annotation: str = ""
    tooltip_text: str = ""
    issues: list[AnalysisIssue] = field(default_factory=list)
    suppressed_entries: list[SuppressedIssueEntry] = field(default_factory=list)

    @property
    def can_suppress_line(self) -> bool:
        return self.status in (CodeLineStatus.ERROR, CodeLineStatus.WARNING, CodeLineStatus.ADVISORY) and bool(self.issues)

    @property
    def can_restore_line(self) -> bool:
        return self.status == CodeLineStatus.SUPPRESSED and bool(self.suppressed_entries)
