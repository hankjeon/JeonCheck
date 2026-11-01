"""검사 규칙(Ruleset) 선택 창과 프로그램 정보 창."""

from __future__ import annotations

from html import escape

from PySide6.QtCore import Qt
from PySide6.QtGui import QPixmap, QStandardItem, QStandardItemModel
from PySide6.QtWidgets import (QAbstractItemView, QComboBox, QDialog, QHBoxLayout, QHeaderView, QLabel, QLineEdit,
                               QPushButton, QSplitter, QTableView, QTextBrowser, QVBoxLayout)

from ..models import RuleInfo
from ..paths import RESOURCES_DIR
from . import theme

RULE_ROLE = Qt.ItemDataRole.UserRole + 1


class RulesetDialog(QDialog):
    """규칙 ON/OFF는 RuleInfo.is_enabled를 바로 바꾼다. 창을 닫은 뒤 호출 측에서 결과 목록을 갱신한다."""

    COLUMNS = ["검사 포함", "규칙 ID", "분류", "기본 심각도", "표준 의무 / 프로젝트 정책", "규칙명", "한 줄 요약 설명"]

    def __init__(self, rules: list[RuleInfo], parent=None):
        super().__init__(parent)
        self.setWindowTitle("검사 규칙(Ruleset) 선택 및 관리")
        self.resize(1200, 760)
        self.rules = rules
        self.visible: list[RuleInfo] = []
        self._loading = False  # 프로그램에서 체크 상태를 바꾸는 동안 itemChanged 무시

        head = QLabel("⚙ 정적 분석 검사 규칙(Ruleset) 설정")
        head.setStyleSheet("font-size: 15px; font-weight: bold;")
        desc = QLabel("프로젝트 특성에 맞게 검사할 규칙을 켜거나(ON) 의도적으로 허용하는 규칙을 제외(OFF)할 수 있습니다.\n"
                      "심각도는 앱의 기본 분류입니다. MISRA 의무 등급과 그 외 규칙의 프로젝트 검토 정책을 구분해 표시합니다.")
        desc.setObjectName("secondary")

        self.category = QComboBox()
        self.category.addItems(["전체"] + list(dict.fromkeys(r.category for r in rules)))
        self.search = QLineEdit()
        self.search.setPlaceholderText("규칙 ID·이름·설명 검색")
        filters = QHBoxLayout()
        filters.addWidget(QLabel("분류:"))
        filters.addWidget(self.category)
        filters.addWidget(QLabel("검색:"))
        filters.addWidget(self.search, 1)
        for text, slot in (("목록 전체 선택", lambda: self._set_visible(True)),
                           ("목록 전체 해제", lambda: self._set_visible(False)),
                           ("필수/우선 검토 선택", self._select_required),
                           ("형변환 집중 선택", self._select_conversion)):
            b = QPushButton(text)
            b.clicked.connect(slot)
            filters.addWidget(b)

        self.model = QStandardItemModel(0, len(self.COLUMNS))
        self.model.setHorizontalHeaderLabels(self.COLUMNS)
        self.model.itemChanged.connect(self._item_changed)
        self.table = QTableView()
        self.table.setModel(self.model)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.verticalHeader().hide()
        self.table.setAlternatingRowColors(True)
        hh = self.table.horizontalHeader()
        hh.setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        hh.setStretchLastSection(True)
        for col, w in enumerate((70, 220, 140, 80, 170, 220)):
            self.table.setColumnWidth(col, w)
        self.table.selectionModel().currentRowChanged.connect(self._preview)

        self.preview = QTextBrowser()
        splitter = QSplitter(Qt.Orientation.Vertical)
        splitter.addWidget(self.table)
        splitter.addWidget(self.preview)
        splitter.setSizes([460, 260])

        self.count = QLabel()
        ok = QPushButton("확인 및 적용")
        ok.setObjectName("primary")
        ok.clicked.connect(self.accept)
        bottom = QHBoxLayout()
        bottom.addWidget(self.count, 1)
        bottom.addWidget(ok)

        layout = QVBoxLayout(self)
        layout.addWidget(head)
        layout.addWidget(desc)
        layout.addLayout(filters)
        layout.addWidget(splitter, 1)
        layout.addLayout(bottom)

        self.category.currentIndexChanged.connect(self._apply_filter)
        self.search.textChanged.connect(self._apply_filter)
        self._apply_filter()

    def _apply_filter(self) -> None:
        cat = self.category.currentText()
        q = self.search.text().strip().lower()
        self.visible = [r for r in self.rules
                        if (cat == "전체" or r.category.lower() == cat.lower())
                        and (not q or any(q in t.lower() for t in (r.id, r.title, r.summary, r.description)))]
        self._loading = True
        self.model.removeRows(0, self.model.rowCount())
        for r in self.visible:
            check = QStandardItem()
            check.setCheckable(True)
            check.setCheckState(Qt.CheckState.Checked if r.is_enabled else Qt.CheckState.Unchecked)
            check.setData(r, RULE_ROLE)
            cells = [check] + [QStandardItem(t) for t in (r.id, r.category, r.severity_display, r.obligation_display,
                                                          r.title, r.summary)]
            cells[1].setFont(theme.code_font(9))
            self.model.appendRow(cells)
        self._loading = False
        if self.visible:
            self.table.selectRow(0)
        else:
            self.preview.setHtml("<p>규칙을 선택하면 상세 설명이 표시됩니다.</p>")
        self._update_count()

    def _item_changed(self, item: QStandardItem) -> None:
        rule = item.data(RULE_ROLE)
        if rule is not None and not self._loading:
            rule.is_enabled = item.checkState() == Qt.CheckState.Checked
            self._update_count()

    def _refresh_checks(self) -> None:
        self._loading = True
        for row, r in enumerate(self.visible):
            self.model.item(row, 0).setCheckState(Qt.CheckState.Checked if r.is_enabled else Qt.CheckState.Unchecked)
        self._loading = False
        self._update_count()

    def _set_visible(self, enabled: bool) -> None:
        # 현재 분류·검색어로 걸러져 목록에 보이는 규칙에만 적용
        for r in self.visible:
            r.is_enabled = enabled
        self._refresh_checks()

    def _select_required(self) -> None:
        for r in self.rules:
            r.is_enabled = any(k in r.obligation for k in ("필수", "치명", "강제"))
        self._refresh_checks()

    def _select_conversion(self) -> None:
        for r in self.rules:
            rid = r.id.lower()
            r.is_enabled = "형변환" in r.category or rid.startswith(("misra-c2012-10.", "misra-c2012-11."))
        self._refresh_checks()

    def _update_count(self) -> None:
        self.count.setText(f"현재 선택된 활성 규칙: {sum(r.is_enabled for r in self.rules)}개 / 전체 {len(self.rules)}개")

    def _preview(self, current, _previous) -> None:
        if not current.isValid() or current.row() >= len(self.visible):
            return
        r = self.visible[current.row()]
        self.preview.setHtml(
            f"<h3>[{escape(r.id)}] {escape(r.title)}</h3>"
            f"<p style='color:{theme.TEXT_SECONDARY}'>분류: {escape(r.category)}  |  {escape(r.obligation_display)}  |  "
            f"기본 심각도: {escape(r.severity_display)}</p>"
            f"<p>{escape(r.description)}</p>"
            f"<p><b>❌ 위반 코드 예시 (Bad)</b></p><pre style='background:#2A1215; padding:6px; white-space:pre-wrap'>{escape(r.bad_example_display)}</pre>"
            f"<p><b>✅ 안전한 개선 예시 (Good)</b></p><pre style='background:#0F2418; padding:6px; white-space:pre-wrap'>{escape(r.good_example_display)}</pre>")


class AboutDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("정보")
        icon = QLabel()
        pix = QPixmap(str(RESOURCES_DIR / "app_icon.png"))
        if not pix.isNull():
            icon.setPixmap(pix.scaled(72, 72, Qt.AspectRatioMode.KeepAspectRatio,
                                      Qt.TransformationMode.SmoothTransformation))
        title = QLabel("<span style='font-size:22px; font-weight:bold'>전검사</span>  "
                       "<span style='color:#9DA7B3'>JeonCheck</span>  <span style='color:#2EA043'>v1.0</span>")
        body = QLabel(
            "C 언어 정적 분석 도구<br><br>"
            "• Cppcheck + MISRA C 2012(misra.py) 및 암묵적 축소 형변환 검출<br>"
            "• Clang-Tidy (CERT-C / bugprone) 통합 정밀 분석 및 #include 자동 매칭<br><br>"
            "<span style='color:#9DA7B3'>설계 :</span> hankjeon@gmail.com<br>"
            "<span style='color:#9DA7B3'>개발 :</span> argon@google<br>"
            "<span style='color:#9DA7B3'>검증 :</span> claude@anthropic, codex@openai")
        ok = QPushButton("확인")
        ok.clicked.connect(self.accept)
        top = QHBoxLayout()
        top.addWidget(icon)
        top.addWidget(title, 1)
        layout = QVBoxLayout(self)
        layout.addLayout(top)
        layout.addWidget(body)
        row = QHBoxLayout()
        row.addStretch(1)
        row.addWidget(ok)
        layout.addLayout(row)
