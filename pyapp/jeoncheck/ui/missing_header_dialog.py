"""누락 헤더 파일 검색 및 참조 코드 추가 창 (WPF 앱 MissingHeaderSearchWindow 대응)."""

from __future__ import annotations

import os

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (QAbstractItemView, QComboBox, QDialog, QFileDialog, QHBoxLayout, QHeaderView, QLabel,
                               QMessageBox, QPushButton, QTableWidget, QTableWidgetItem, QVBoxLayout)

from ..missing_headers import MatchStatus, MissingHeaderItem, search_folder, selected_paths_to_add
from ..models import normalize_path
from . import theme

STATUS_COLORS = {
    MatchStatus.FOUND_SINGLE: "#3FB950",
    MatchStatus.FOUND_MULTIPLE: "#D29922",
    MatchStatus.NOT_FOUND: "#F85149",
}
COL_CHECK, COL_STATUS, COL_INCLUDE, COL_REFERENCED, COL_PATH = range(5)


class MissingHeaderDialog(QDialog):
    """확인을 누르면 selected_paths에 참조 코드로 추가할 헤더 파일 경로가 담긴다."""

    def __init__(self, items: list[MissingHeaderItem], initial_folder: str | None = None, parent=None):
        super().__init__(parent)
        self.setWindowTitle("누락 헤더 파일 검색 및 참조 코드 추가")
        self.resize(1200, 640)
        self.items = items
        self.searched_folders: list[str] = []
        self.selected_paths: list[str] = []
        self._loading = False  # 프로그램에서 표를 다시 그리는 동안 itemChanged 무시

        head = QLabel("🔍 누락된 포함 파일(#include) 하위 폴더 일괄 검색")
        head.setStyleSheet("font-size: 15px; font-weight: bold; color: #58A6FF;")
        desc = QLabel("상위 폴더(SDK, BSP, 프로젝트 루트 등)를 선택하면 하위 모든 폴더를 뒤져 누락된 헤더를 찾아줍니다. "
                      "중복된 파일은 가장 알맞은 경로를 추천하며 드롭다운에서 직접 변경할 수 있습니다.")
        desc.setObjectName("secondary")
        desc.setWordWrap(True)

        folder_row = QHBoxLayout()
        add_folder = QPushButton("📂 다른 폴더 추가로 검색...")
        add_folder.clicked.connect(self._select_folder_and_search)
        folder_row.addWidget(add_folder)
        folder_row.addWidget(QLabel("검색한 폴더:"))
        self.folders_label = QLabel("(아직 선택된 폴더 없음)")
        self.folders_label.setObjectName("secondary")
        folder_row.addWidget(self.folders_label, 1)

        select_row = QHBoxLayout()
        for text, checked in (("발견된 항목 전체 선택", True), ("전체 해제", False)):
            b = QPushButton(text)
            b.clicked.connect(lambda _=False, c=checked: self._set_all(c))
            select_row.addWidget(b)
        self.summary_label = QLabel("검색 대기 중...")
        self.summary_label.setObjectName("secondary")
        select_row.addWidget(self.summary_label, 1)

        self.table = QTableWidget(0, 5)
        self.table.setHorizontalHeaderLabels(["추가", "검색 상태", "누락된 #include 헤더", "참조한 소스",
                                              "발견된 파일 경로 (중복 발견 시 클릭하여 다른 경로 선택 가능)"])
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.verticalHeader().hide()
        self.table.horizontalHeader().setSectionResizeMode(COL_PATH, QHeaderView.ResizeMode.Stretch)
        for col, w in ((COL_CHECK, 52), (COL_STATUS, 135), (COL_INCLUDE, 220), (COL_REFERENCED, 160)):
            self.table.setColumnWidth(col, w)
        self.table.itemChanged.connect(self._item_changed)

        buttons = QHBoxLayout()
        buttons.addStretch(1)
        cancel = QPushButton("취소")
        cancel.clicked.connect(self.reject)
        confirm = QPushButton("✔ 선택한 헤더 파일들을 참조 코드에 추가")
        confirm.setObjectName("primary")
        confirm.clicked.connect(self._confirm)
        buttons.addWidget(cancel)
        buttons.addWidget(confirm)
        note = QLabel("※ 체크된 헤더 파일들만 [참조코드(비대상)] 목록에 개별 파일로 추가되며, "
                      "분석 시 상위 #include 경로(-I)가 자동 연결됩니다.")
        note.setObjectName("secondary")

        lay = QVBoxLayout(self)
        lay.addWidget(head)
        lay.addWidget(desc)
        lay.addLayout(folder_row)
        lay.addLayout(select_row)
        lay.addWidget(self.table, 1)
        lay.addLayout(buttons)
        lay.addWidget(note)

        self._refresh()
        if initial_folder and os.path.isdir(initial_folder):
            self.search_in_folder(initial_folder)

    # ---------------------------------------------------------------- 검색
    def _select_folder_and_search(self) -> None:
        folder = QFileDialog.getExistingDirectory(self, "누락된 헤더 파일을 검색할 상위 폴더 선택 (하위 모든 폴더 자동 탐색)")
        if folder and os.path.isdir(folder):
            self.search_in_folder(folder)

    def search_in_folder(self, folder: str) -> None:
        full = os.path.abspath(folder)
        if all(normalize_path(f) != normalize_path(full) for f in self.searched_folders):
            self.searched_folders.append(full)
            self.folders_label.setText(" ; ".join(self.searched_folders))
            self.folders_label.setToolTip(self.folders_label.text())
        search_folder(self.items, full)
        self._refresh()

    # ---------------------------------------------------------------- 표 그리기
    def _refresh(self) -> None:
        self._loading = True
        self.table.setRowCount(0)
        for row, item in enumerate(self.items):
            self.table.insertRow(row)
            check = QTableWidgetItem()
            flags = Qt.ItemFlag.ItemIsUserCheckable | Qt.ItemFlag.ItemIsSelectable
            if item.can_select:
                flags |= Qt.ItemFlag.ItemIsEnabled
            check.setFlags(flags)
            check.setCheckState(Qt.CheckState.Checked if item.is_selected else Qt.CheckState.Unchecked)
            self.table.setItem(row, COL_CHECK, check)

            status = QTableWidgetItem(item.status_display)
            if item.status in STATUS_COLORS:
                status.setForeground(QColor(STATUS_COLORS[item.status]))
            self.table.setItem(row, COL_STATUS, status)

            inc = QTableWidgetItem(item.raw_include)
            inc.setFont(theme.code_font(9))
            self.table.setItem(row, COL_INCLUDE, inc)

            ref = QTableWidgetItem(item.referenced_by_display)
            ref.setToolTip("\n".join(item.referenced_by_files))
            self.table.setItem(row, COL_REFERENCED, ref)

            if item.status == MatchStatus.FOUND_MULTIPLE:
                combo = QComboBox()
                combo.addItems(item.candidate_paths)
                combo.setCurrentText(item.selected_candidate_path)
                combo.currentTextChanged.connect(lambda text, it=item: setattr(it, "selected_candidate_path", text))
                self.table.setCellWidget(row, COL_PATH, combo)
            else:
                path = QTableWidgetItem(item.single_or_not_found_display)
                path.setToolTip(path.text())
                if item.status != MatchStatus.FOUND_SINGLE:
                    path.setForeground(QColor(theme.TEXT_SECONDARY))
                self.table.setItem(row, COL_PATH, path)
        self._loading = False
        self._update_summary()

    def _update_summary(self) -> None:
        def count(s: MatchStatus) -> int:
            return sum(1 for i in self.items if i.status == s)
        self.summary_label.setText(
            f"총 누락 헤더 {len(self.items)}종  |  ✅ 단일 발견: {count(MatchStatus.FOUND_SINGLE)}건  |  "
            f"⚠️ 중복 발견(경로 선택 가능): {count(MatchStatus.FOUND_MULTIPLE)}건  |  "
            f"❌ 미발견: {count(MatchStatus.NOT_FOUND)}건")

    def _item_changed(self, cell: QTableWidgetItem) -> None:
        if self._loading or cell.column() != COL_CHECK:
            return
        self.items[cell.row()].is_selected = cell.checkState() == Qt.CheckState.Checked

    def _set_all(self, checked: bool) -> None:
        for item in self.items:
            if not checked or item.can_select:
                item.is_selected = checked
        self._refresh()

    # ---------------------------------------------------------------- 확인
    def _confirm(self) -> None:
        paths = selected_paths_to_add(self.items)
        if not paths:
            QMessageBox.information(self, "안내", "추가할 항목이 선택되지 않았거나 발견된 헤더 파일이 없습니다.")
            return
        self.selected_paths = paths
        self.accept()
