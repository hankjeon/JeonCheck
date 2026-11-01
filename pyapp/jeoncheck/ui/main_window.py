"""메인 창: 상단 툴바·옵션, 좌측 소스 트리, 가운데 코드 뷰어·결과 탭, 오른쪽 상세 해설 (doc/04_ui_design.md)."""

from __future__ import annotations

import os
from html import escape

from PySide6.QtCore import QAbstractTableModel, QModelIndex, QSortFilterProxyModel, Qt, QThread, QTimer, Signal
from PySide6.QtGui import QColor, QIcon, QKeySequence
from PySide6.QtWidgets import (QAbstractItemView, QCheckBox, QComboBox, QFileDialog, QFrame, QHBoxLayout,
                               QHeaderView, QLabel, QLineEdit, QMainWindow, QMenu, QMessageBox, QPushButton,
                               QSplitter, QTableView, QTableWidget, QTableWidgetItem, QTabWidget, QTextBrowser,
                               QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget)

from ..engine.process import AnalysisCancelled
from ..missing_headers import group_missing_headers
from ..models import AnalysisIssue, SourceTreeNode, SuppressedIssueEntry
from ..paths import RESOURCES_DIR
from ..state import C_STANDARDS, AnalysisRequest, AppState
from . import theme
from .code_view import CodeView, DefectOverview
from .dialogs import AboutDialog, RulesetDialog
from .missing_header_dialog import MissingHeaderDialog

OBJ_ROLE = Qt.ItemDataRole.UserRole + 1
SORT_ROLE = Qt.ItemDataRole.UserRole + 2


class AnalysisWorker(QThread):
    """백그라운드에서 분석을 실행하고 결과를 메인 스레드로 전달한다."""

    done = Signal(object, object, object, bool)  # 요청, 결과, 오류, 취소 여부
    progress = Signal(object, float, str)  # 요청, 진행률(0~100), 단계 설명

    def __init__(self, req: AnalysisRequest, parent=None):
        super().__init__(parent)
        self.req = req
        # 분석 스레드에서 호출되므로 시그널로 UI 스레드에 넘긴다.
        req.progress = lambda pct, msg: self.progress.emit(req, pct, msg)

    def run(self) -> None:
        try:
            self.done.emit(self.req, self.req.run(), None, False)
        except AnalysisCancelled:
            self.done.emit(self.req, None, None, True)
        except Exception as ex:  # noqa: BLE001  (상태 메시지로 안내)
            self.done.emit(self.req, None, ex, False)


class DefectsModel(QAbstractTableModel):
    HEADERS = ["심각도", "분류", "규칙 ID", "파일명", "라인", "검출 내용 및 한글 요약"]

    def __init__(self):
        super().__init__()
        self.items: list[AnalysisIssue] = []

    def set_items(self, items: list[AnalysisIssue]) -> None:
        self.beginResetModel()
        self.items = list(items)
        self.endResetModel()

    def rowCount(self, parent=QModelIndex()) -> int:  # noqa: N802
        return 0 if parent.isValid() else len(self.items)

    def columnCount(self, parent=QModelIndex()) -> int:  # noqa: N802
        return len(self.HEADERS)

    def headerData(self, section, orientation, role=Qt.ItemDataRole.DisplayRole):  # noqa: N802
        if orientation == Qt.Orientation.Horizontal and role == Qt.ItemDataRole.DisplayRole:
            return self.HEADERS[section]
        return None

    def data(self, index, role=Qt.ItemDataRole.DisplayRole):
        if not index.isValid():
            return None
        d = self.items[index.row()]
        col = index.column()
        if role == OBJ_ROLE:
            return d
        if role in (Qt.ItemDataRole.DisplayRole, SORT_ROLE):
            values = [d.review_severity_display, d.category, d.rule_id, d.file_name, d.line, d.message]
            if role == SORT_ROLE and col == 0:
                return {"Error": 0, "Warning": 1, "Advisory": 2}[d.review_severity]
            return values[col]
        if role == Qt.ItemDataRole.ForegroundRole and col == 0:
            return QColor(theme.SEVERITY_COLORS[d.review_severity])
        if role == Qt.ItemDataRole.ToolTipRole and col == 5:
            return d.message
        if role == Qt.ItemDataRole.FontRole and col == 2:
            return theme.code_font(9)
        return None


def _read_only_table(headers: list[str]) -> QTableWidget:
    t = QTableWidget(0, len(headers))
    t.setHorizontalHeaderLabels(headers)
    t.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
    t.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
    t.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
    t.verticalHeader().hide()
    t.setAlternatingRowColors(True)
    t.horizontalHeader().setStretchLastSection(True)
    return t


class MainWindow(QMainWindow):
    def __init__(self, state: AppState):
        super().__init__()
        self.state = state
        self._workers: list[AnalysisWorker] = []
        self._tree_signature: tuple = ()
        self.setWindowIcon(QIcon(str(RESOURCES_DIR / "app_icon.png")))
        self.resize(1600, 960)
        self._build_menu()

        central = QWidget()
        root = QVBoxLayout(central)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)
        root.addWidget(self._build_toolbar())
        body = QSplitter(Qt.Orientation.Horizontal)
        body.addWidget(self._build_tree_panel())
        body.addWidget(self._build_center_panel())
        body.addWidget(self._build_detail_panel())
        body.setSizes([300, 900, 400])
        body.setStretchFactor(1, 1)
        root.addWidget(body, 1)
        self.setCentralWidget(central)

        self.status_label = QLabel()
        self.arch_label = QLabel()
        self.arch_label.setObjectName("secondary")
        self.statusBar().addWidget(self.status_label, 1)
        self.statusBar().addPermanentWidget(self.arch_label)

        self._search_timer = QTimer(self, singleShot=True, interval=200)
        self._search_timer.timeout.connect(lambda: self.state.set_search_text(self._pending_search))
        self._pending_search = ""

        state.listeners.append(self._on_state_event)
        for event in ("options", "rules", "trees", "results", "code", "project", "selection", "analyzing", "status"):
            self._on_state_event(event)

    # ================================================================ 화면 구성
    def _button(self, text: str, slot, object_name: str = "") -> QPushButton:
        b = QPushButton(text)
        if object_name:
            b.setObjectName(object_name)
        b.clicked.connect(slot)
        return b

    def _build_menu(self) -> None:
        bar = self.menuBar()
        file_menu = bar.addMenu("파일(&F)")
        file_menu.addAction("📄 새 프로젝트(&N)", QKeySequence("Ctrl+N"), self.state.clear_workspace)
        file_menu.addAction("📂 프로젝트 열기(&O)...", QKeySequence("Ctrl+O"), self._open_project)
        self.recent_menu = file_menu.addMenu("🕒 최근 사용한 프로젝트(&R)")
        self.recent_menu.aboutToShow.connect(self._rebuild_recent_menu)
        self._rebuild_recent_menu()
        file_menu.addSeparator()
        file_menu.addAction("💾 저장(&S)", QKeySequence("Ctrl+S"), self._save_project)
        file_menu.addAction("💾 다른 이름으로 저장(&A)...", QKeySequence("Ctrl+Shift+S"), self._save_project_as)
        file_menu.addSeparator()
        file_menu.addAction("🧪 샘플 프로젝트 로드(&L)", self.state.load_sample_workspace)
        file_menu.addSeparator()
        file_menu.addAction("종료(&X)", self.close)
        help_menu = bar.addMenu("도움말(&H)")
        help_menu.addAction("ℹ️ 전검사 정보(&A)...", lambda: AboutDialog(self).exec())

    def _rebuild_recent_menu(self) -> None:
        menu = self.recent_menu
        menu.clear()
        recent = self.state.recent_projects
        if not recent:
            menu.addAction("(최근 사용한 프로젝트 없음)").setEnabled(False)
            return
        for i, full in enumerate(recent, start=1):
            key = f"&{i}" if i <= 9 else "1&0"
            action = menu.addAction(f"{key}. {os.path.basename(full)}  ({full})",
                                    lambda p=full: self._open_recent_project(p))
            action.setToolTip(full)
        menu.addSeparator()
        menu.addAction("🗑 최근 목록 비우기(&C)", self.state.clear_recent_projects)

    def _open_recent_project(self, path: str) -> None:
        if not os.path.isfile(path):
            answer = QMessageBox.warning(
                self, "파일 없음", f"프로젝트 파일을 찾을 수 없습니다:\n{path}\n\n최근 사용한 프로젝트 목록에서 제거할까요?",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No)
            if answer == QMessageBox.StandardButton.Yes:
                self.state.remove_recent_project(path)
            return
        self.state.load_project(path)

    def _build_toolbar(self) -> QWidget:
        bar = QFrame()
        bar.setObjectName("toolbar")
        v = QVBoxLayout(bar)
        v.setContentsMargins(8, 6, 8, 6)

        row1 = QHBoxLayout()
        row1.addStretch(1)
        self.ruleset_button = self._button("", self._open_ruleset)
        self.cancel_button = self._button("■ 분석 취소", self.state.cancel_analysis, "danger")
        self.analyze_button = self._button("▶ 정적 분석 시작", self._start_analysis, "primary")
        self.analyze_button.setMinimumWidth(200)  # 진행률 문구가 바뀌어도 버튼 폭이 흔들리지 않게 함
        row1.addWidget(self.ruleset_button)
        row1.addWidget(self.cancel_button)
        row1.addWidget(self.analyze_button)
        v.addLayout(row1)

        row2 = QHBoxLayout()
        row2.addWidget(QLabel("타겟:"))
        self.preset_combo = QComboBox()
        self.preset_combo.setMinimumWidth(320)
        for p in self.state.target_presets:
            self.preset_combo.addItem(p.name, p)
            self.preset_combo.setItemData(self.preset_combo.count() - 1, p.description, Qt.ItemDataRole.ToolTipRole)
        self.preset_combo.currentIndexChanged.connect(self._preset_changed)
        row2.addWidget(self.preset_combo)
        row2.addWidget(QLabel("C 표준:"))
        self.std_combo = QComboBox()
        self.std_combo.addItems(C_STANDARDS)
        self.std_combo.currentTextChanged.connect(lambda t: setattr(self.state, "c_standard", t))
        row2.addWidget(self.std_combo)
        row2.addWidget(QLabel("사용자 매크로(-D):"))
        self.defines_edit = QLineEdit()
        self.defines_edit.setPlaceholderText("예: DEBUG=1, USE_DMA")
        self.defines_edit.textChanged.connect(lambda t: setattr(self.state, "custom_defines", t))
        row2.addWidget(self.defines_edit, 1)
        self.force_check = QCheckBox("모든 #ifdef 분기 강제 검사 (--force)")
        self.force_check.toggled.connect(lambda b: setattr(self.state, "force_all_ifdef_configs", b))
        self.clang_check = QCheckBox("Clang-Tidy (CERT-C / 심층 분석) 포함")
        self.clang_check.toggled.connect(lambda b: setattr(self.state, "enable_clang_tidy", b))
        row2.addWidget(self.force_check)
        row2.addWidget(self.clang_check)
        v.addLayout(row2)
        return bar

    def _tree_section(self, title: str, add_slot, remove_slot) -> tuple[QWidget, QTreeWidget]:
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(0)
        head = QHBoxLayout()
        label = QLabel(title)
        label.setObjectName("sectionHeader")
        head.addWidget(label, 1)
        add = QPushButton("+")
        menu = QMenu(add)
        menu.addAction("📁 폴더 추가...", lambda: add_slot(folder=True))
        menu.addAction("📄 파일 추가...", lambda: add_slot(folder=False))
        add.setMenu(menu)
        head.addWidget(add)
        head.addWidget(self._button("-", remove_slot))
        lay.addLayout(head)
        tree = QTreeWidget()
        tree.setHeaderHidden(True)
        tree.itemSelectionChanged.connect(lambda t=tree: self._tree_selection_changed(t))
        lay.addWidget(tree, 1)
        return w, tree

    def _build_tree_panel(self) -> QWidget:
        panel = QWidget()
        lay = QVBoxLayout(panel)
        lay.setContentsMargins(4, 4, 4, 4)
        target, self.target_tree = self._tree_section(
            "검사대상 코드", lambda folder: self._add_paths(True, folder), lambda: self._remove_node(True))
        non_target, self.non_target_tree = self._tree_section(
            "참조코드(비대상)", lambda folder: self._add_paths(False, folder), lambda: self._remove_node(False))
        move = QHBoxLayout()
        move.addWidget(self._button("▼ 비대상으로 제외", self._move_to_non_target))
        move.addWidget(self._button("▲ 검사 대상으로 포함", self._move_to_target))
        lay.addWidget(target, 3)
        lay.addLayout(move)
        lay.addWidget(non_target, 2)
        return panel

    def _filter_row(self) -> tuple[QHBoxLayout, QComboBox, QLineEdit]:
        row = QHBoxLayout()
        combo = QComboBox()
        combo.setMinimumWidth(180)
        combo.currentTextChanged.connect(self._category_changed)
        search = QLineEdit()
        search.setPlaceholderText("🔍 규칙 ID·제목·메시지·파일명 검색")
        search.textChanged.connect(self._search_changed)
        row.addWidget(QLabel("분류:"))
        row.addWidget(combo)
        row.addWidget(search, 1)
        return row, combo, search

    def _severity_filter_row(self) -> QHBoxLayout:
        row = QHBoxLayout()
        row.addWidget(QLabel("표시:"))
        buttons = {}
        for severity, label, color in (("Error", "🔴 오류", "#F85149"),
                                       ("Warning", "🟠 경고", "#D29922"),
                                       ("Advisory", "🔵 권고", "#58A6FF")):
            button = QPushButton(label)
            button.setCheckable(True)
            button.setStyleSheet(f"QPushButton {{ color: {theme.TEXT_SECONDARY}; background: {theme.BG_TOOLBAR}; "
                                 f"border: 1px solid {theme.BORDER}; padding: 3px 8px; }} "
                                 f"QPushButton:checked {{ color: {color}; background: #30363D; border-color: {color}; }}")
            button.toggled.connect(lambda checked, level=severity: self.state.set_severity_visible(level, checked))
            row.addWidget(button)
            buttons[severity] = button
        self.severity_buttons.append(buttons)
        row.addStretch(1)
        row.addWidget(self._button("오류만", self.state.show_only_errors))
        row.addWidget(self._button("모두 표시", self.state.show_all_severities))
        return row

    def _build_center_panel(self) -> QWidget:
        split = QSplitter(Qt.Orientation.Vertical)

        code = QWidget()
        cl = QVBoxLayout(code)
        cl.setContentsMargins(0, 4, 0, 0)
        head = QHBoxLayout()
        self.file_label = QLabel()
        self.file_label.setStyleSheet("font-weight: bold;")
        head.addWidget(self.file_label, 1)
        head.addWidget(self._button("📝 외부 편집기로 열기", self._open_external_editor))
        head.addWidget(self._button("📁 폴더 열기", self.state.open_current_file_folder))
        cl.addLayout(head)
        self.stats_label = QLabel()
        self.stats_label.setObjectName("secondary")
        cl.addWidget(self.stats_label)
        self.code_view = CodeView()
        self.code_view.lineSelected.connect(self._code_line_selected)
        self.code_view.suppressRequested.connect(lambda line: self._keep_line(self.state.suppress_code_line, line))
        self.code_view.restoreRequested.connect(lambda line: self._keep_line(self.state.restore_code_line, line))
        code_row = QHBoxLayout()
        code_row.setContentsMargins(0, 0, 0, 0)
        code_row.setSpacing(0)
        code_row.addWidget(self.code_view, 1)
        self.defect_overview = DefectOverview()
        self.defect_overview.lineRequested.connect(self._overview_line_requested)
        code_row.addWidget(self.defect_overview)
        cl.addLayout(code_row, 1)
        split.addWidget(code)

        self.tabs = QTabWidget()
        self.severity_buttons = []
        # 1. 결함 목록
        defects = QWidget()
        dl = QVBoxLayout(defects)
        row, self.defects_category, self.defects_search = self._filter_row()
        dl.addLayout(row)
        dl.addLayout(self._severity_filter_row())
        self.defects_model = DefectsModel()
        proxy = QSortFilterProxyModel(self)
        proxy.setSourceModel(self.defects_model)
        proxy.setSortRole(SORT_ROLE)
        self.defects_table = QTableView()
        self.defects_table.setModel(proxy)
        self.defects_table.setSortingEnabled(True)
        # 처음에는 엔진 정렬(파일 → 줄 → 열 → 규칙)을 유지하고, 열 제목을 클릭하면 정렬한다.
        self.defects_table.horizontalHeader().setSortIndicator(-1, Qt.SortOrder.AscendingOrder)
        proxy.sort(-1)
        self.defects_table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.defects_table.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.defects_table.verticalHeader().hide()
        self.defects_table.setAlternatingRowColors(True)
        self.defects_table.horizontalHeader().setStretchLastSection(True)
        for col, w in enumerate((60, 150, 230, 150, 50)):
            self.defects_table.setColumnWidth(col, w)
        self.defects_table.selectionModel().currentRowChanged.connect(self._defect_selected)
        self.defects_table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.defects_table.customContextMenuRequested.connect(self._defects_menu)
        dl.addWidget(self.defects_table, 1)
        self.tabs.addTab(defects, "")

        # 2. 규칙별 요약
        summary = QWidget()
        sl = QVBoxLayout(summary)
        row, self.summary_category, self.summary_search = self._filter_row()
        sl.addLayout(row)
        sl.addLayout(self._severity_filter_row())
        self.summary_status = QLabel()
        self.summary_status.setObjectName("secondary")
        sl.addWidget(self.summary_status)
        self.summary_tree = QTreeWidget()
        self.summary_tree.setColumnCount(2)
        self.summary_tree.setHeaderLabels(["규칙 / 발생 위치", "코드"])
        self.summary_tree.header().setSectionResizeMode(0, QHeaderView.ResizeMode.Interactive)
        self.summary_tree.setColumnWidth(0, 520)
        self.summary_tree.currentItemChanged.connect(self._summary_selected)
        self.summary_tree.itemExpanded.connect(lambda it: self._group_expanded(it, True))
        self.summary_tree.itemCollapsed.connect(lambda it: self._group_expanded(it, False))
        self.summary_tree.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.summary_tree.customContextMenuRequested.connect(self._summary_menu)
        sl.addWidget(self.summary_tree, 1)
        self.summary_empty = QLabel("표시할 진단이 없습니다. 분석을 실행하거나 검색·분류·심각도·예외 조건을 확인하십시오.")
        self.summary_empty.setObjectName("secondary")
        self.summary_empty.setAlignment(Qt.AlignmentFlag.AlignCenter)
        sl.addWidget(self.summary_empty)
        self.tabs.addTab(summary, "")

        # 3. 노트
        self.notes_table = _read_only_table(["구분", "파일명", "라인", "상세 참고 안내"])
        for col, w in enumerate((200, 150, 50)):
            self.notes_table.setColumnWidth(col, w)
        self.notes_table.currentCellChanged.connect(self._note_selected)
        self.notes_table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.notes_table.customContextMenuRequested.connect(self._notes_menu)
        self.tabs.addTab(self.notes_table, "")

        # 4. 예외 항목
        sup = QWidget()
        ul = QVBoxLayout(sup)
        head = QHBoxLayout()
        info = QLabel("개별 위치 예외(무시) 처리된 결함 목록입니다. 선택 후 복원 버튼이나 우클릭으로 원래 결함 목록으로 되돌릴 수 있습니다.")
        info.setObjectName("secondary")
        info.setWordWrap(True)
        head.addWidget(info, 1)
        head.addWidget(self._button("↩ 선택 항목 예외 해제(복원)", self._restore_selected_suppressed))
        head.addWidget(self._button("전체 복원", self.state.restore_all_suppressed_issues))
        ul.addLayout(head)
        self.suppressed_table = _read_only_table(["규칙 ID", "파일 경로", "라인", "무시된 결함 내용"])
        for col, w in enumerate((200, 300, 50)):
            self.suppressed_table.setColumnWidth(col, w)
        self.suppressed_table.currentCellChanged.connect(self._suppressed_selected)
        self.suppressed_table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.suppressed_table.customContextMenuRequested.connect(self._suppressed_menu)
        ul.addWidget(self.suppressed_table, 1)
        self.tabs.addTab(sup, "")

        split.addWidget(self.tabs)
        split.setSizes([560, 360])
        return split

    def _build_detail_panel(self) -> QWidget:
        panel = QWidget()
        lay = QVBoxLayout(panel)
        lay.setContentsMargins(4, 4, 4, 4)
        header = QLabel("📖 결함 및 MISRA 규칙 상세 한글 해설")
        header.setObjectName("sectionHeader")
        lay.addWidget(header)
        self.detail_head = QTextBrowser()
        self.detail_head.setMaximumHeight(150)
        lay.addWidget(self.detail_head)
        buttons = QHBoxLayout()
        self.toggle_suppress_button = self._button("", self.state.toggle_selected_issue_suppression)
        self.disable_rule_button = self._button("🚫 이 규칙 전체 끄기", self._disable_selected_rule)
        buttons.addWidget(self.toggle_suppress_button)
        buttons.addWidget(self.disable_rule_button)
        lay.addLayout(buttons)
        self.detail_body = QTextBrowser()
        lay.addWidget(self.detail_body, 1)
        return panel

    # ================================================================ 상태 알림 처리
    def _on_state_event(self, event: str) -> None:
        handler = {
            "status": lambda: self.status_label.setText(self.state.status_message),
            "options": self._sync_options,
            "rules": self._sync_rules,
            "trees": self._sync_trees,
            "results": self._sync_results,
            "code": self._sync_code,
            "project": lambda: self.setWindowTitle(self.state.project_title),
            "selection": self._sync_detail,
            "analyzing": self._sync_analyzing,
            "progress": self._sync_progress,
        }.get(event)
        if handler:
            handler()

    def _sync_options(self) -> None:
        s = self.state
        for w in (self.preset_combo, self.std_combo, self.defines_edit, self.force_check, self.clang_check):
            w.blockSignals(True)
        idx = next((i for i in range(self.preset_combo.count()) if self.preset_combo.itemData(i) is s.selected_preset),
                   -1)
        self.preset_combo.setCurrentIndex(idx)
        self.std_combo.setCurrentText(s.c_standard)
        self.defines_edit.setText(s.custom_defines)
        self.force_check.setChecked(s.force_all_ifdef_configs)
        self.clang_check.setChecked(s.enable_clang_tidy)
        for w in (self.preset_combo, self.std_combo, self.defines_edit, self.force_check, self.clang_check):
            w.blockSignals(False)
        self.arch_label.setText(s.selected_preset.architecture if s.selected_preset else "")

    def _sync_rules(self) -> None:
        self.ruleset_button.setText(self.state.ruleset_button_text)
        for combo in (self.defects_category, self.summary_category):
            combo.blockSignals(True)
            combo.clear()
            combo.addItems(self.state.category_filters)
            combo.setCurrentText(self.state.category_filter)
            combo.blockSignals(False)

    def _sync_trees(self) -> None:
        roots = (self.state.target_roots, self.state.non_target_roots)

        def signature(nodes):
            return tuple((id(n), signature(n.children)) for n in nodes)

        sig = (signature(roots[0]), signature(roots[1]))
        if sig != self._tree_signature:
            self._tree_signature = sig
            for tree, nodes in zip((self.target_tree, self.non_target_tree), roots):
                tree.blockSignals(True)
                tree.clear()
                for n in nodes:
                    tree.addTopLevelItem(self._tree_item(n))
                tree.expandAll()
                tree.blockSignals(False)
        else:
            for tree in (self.target_tree, self.non_target_tree):
                for item in _iter_tree_items(tree):
                    item.setText(0, self._node_text(item.data(0, OBJ_ROLE)))

    @staticmethod
    def _node_text(node: SourceTreeNode) -> str:
        icon = "📁" if node.is_directory else ("📘" if node.full_path.lower().endswith(".h") else "📄")
        badge = f"   🔴 {node.defect_count}" if node.is_target and node.defect_count > 0 else ""
        return f"{icon} {node.name}{badge}"

    def _tree_item(self, node: SourceTreeNode) -> QTreeWidgetItem:
        item = QTreeWidgetItem([self._node_text(node)])
        item.setData(0, OBJ_ROLE, node)
        item.setToolTip(0, node.full_path)
        for c in node.children:
            item.addChild(self._tree_item(c))
        return item

    def _sync_results(self) -> None:
        s = self.state
        self.defects_model.set_items(s.filtered_defects)
        self.tabs.setTabText(0, s.defects_tab_header)
        self.tabs.setTabText(1, s.rule_summary_tab_header)
        self.tabs.setTabText(2, s.notes_tab_header)
        self.tabs.setTabText(3, s.suppressed_tab_header)
        self.summary_status.setText(s.rule_summary_status_text)

        # 검색·분류·심각도 필터는 두 탭이 공유
        for combo, edit in ((self.defects_category, self.defects_search), (self.summary_category, self.summary_search)):
            combo.blockSignals(True)
            combo.setCurrentText(s.category_filter)
            combo.blockSignals(False)
            if not self._search_timer.isActive() and edit.text() != s.search_text:
                edit.blockSignals(True)
                edit.setText(s.search_text)
                edit.blockSignals(False)
        for buttons in self.severity_buttons:
            for severity, button in buttons.items():
                button.blockSignals(True)
                button.setChecked(severity in s.visible_severities)
                button.blockSignals(False)

        self.summary_tree.blockSignals(True)
        self.summary_tree.clear()
        for g in s.rule_issue_groups:
            top = QTreeWidgetItem([g.header, ""])
            top.setData(0, OBJ_ROLE, g)
            top.setFont(0, theme.code_font(10))
            for o in g.occurrences:
                child = QTreeWidgetItem([o.location, o.code_text])
                child.setData(0, OBJ_ROLE, o.issue)
                child.setFont(1, theme.code_font(9))
                child.setToolTip(1, o.issue.message)
                top.addChild(child)
            self.summary_tree.addTopLevelItem(top)
            top.setExpanded(g.is_expanded)
        self.summary_tree.blockSignals(False)
        self.summary_empty.setVisible(not s.rule_issue_groups)

        self.notes_table.blockSignals(True)
        self.notes_table.setRowCount(0)
        for n in s.notes:
            self._append_row(self.notes_table, n, [n.title, n.file_name, str(n.line) if n.line else "", n.message])
        self.notes_table.blockSignals(False)

        self.suppressed_table.blockSignals(True)
        self.suppressed_table.setRowCount(0)
        for e in s.suppressed:
            self._append_row(self.suppressed_table, e, [e.rule_id, e.file_path, str(e.line), e.message])
        self.suppressed_table.blockSignals(False)

        self.ruleset_button.setText(s.ruleset_button_text)
        self._sync_detail()

    @staticmethod
    def _append_row(table: QTableWidget, obj, values: list[str]) -> None:
        row = table.rowCount()
        table.insertRow(row)
        for col, v in enumerate(values):
            item = QTableWidgetItem(v)
            item.setToolTip(v)
            if col == 0:
                item.setData(OBJ_ROLE, obj)
            table.setItem(row, col, item)

    def _sync_code(self) -> None:
        self.file_label.setText(self.state.current_code_file_name)
        self.stats_label.setText(self.state.code_stats_summary)
        self.code_view.set_lines(self.state.current_file_lines)
        self.defect_overview.set_lines(self.state.current_file_lines)

    def _sync_detail(self) -> None:
        issue = self.state.selected_issue
        has = issue is not None
        self.toggle_suppress_button.setEnabled(has)
        self.disable_rule_button.setEnabled(has)
        if not has:
            self.toggle_suppress_button.setText("🔇 이 줄만 예외 처리")
            self.detail_head.setHtml(f"<p style='color:{theme.TEXT_SECONDARY}'>결함 목록이나 코드 줄을 선택하면 "
                                     "상세 한글 해설이 표시됩니다.</p>")
            self.detail_body.clear()
            return
        self.toggle_suppress_button.setText("↩ 이 줄 예외 해제 (복원)" if self.state.is_issue_suppressed(issue)
                                            else "🔇 이 줄만 예외 처리")
        rule = issue.rule_details
        chip = "background:{}; padding:2px 6px; font-weight:bold"
        self.detail_head.setHtml(
            f"<span style='{chip.format('#1F3A5F')}'>&nbsp;{escape(issue.category)}&nbsp;</span> "
            f"<span style='{chip.format('#3A2F1F')}'>&nbsp;{escape(issue.obligation_display)}&nbsp;</span>"
            f"<p style='font-family:\"{theme.code_font().family()}\"; font-weight:bold; font-size:14px'>"
            f"{escape(issue.rule_id)}</p>"
            f"<p style='font-weight:bold'>{escape(issue.title)}</p>"
            f"<p>위치: <b>{escape(issue.file_name)}</b> (<b>{issue.line}</b>번째 줄)</p>")
        desc = rule.description if rule else ""
        bad = rule.bad_example_display if rule else ""
        good = rule.good_example_display if rule else ""
        pre = "background:{}; padding:8px; white-space:pre-wrap; font-family:\"" + theme.code_font().family() + "\""
        self.detail_body.setHtml(
            f"<p><b>🔍 검출된 코드 진단</b></p><p>{escape(issue.message)}</p>"
            f"<p><b>💡 왜 위험한가? (임베디드 상세 해설)</b></p><p>{escape(desc)}</p>"
            f"<p><b>❌ 위반 코드 예시 (Bad)</b></p><pre style='{pre.format('#2A1215')}'>{escape(bad)}</pre>"
            f"<p><b>✅ 안전한 개선 예시 (Good)</b></p><pre style='{pre.format('#0F2418')}'>{escape(good)}</pre>")

    def _sync_analyzing(self) -> None:
        busy = self.state.is_analyzing
        self.analyze_button.setEnabled(not busy)
        self.cancel_button.setEnabled(busy)
        self._sync_progress()

    def _sync_progress(self) -> None:
        """분석 중에는 [정적 분석 시작] 버튼을 왼쪽부터 진행률만큼 녹색으로 채운다 (WPF 앱과 같은 방식)."""
        self.analyze_button.setText(self.state.run_button_text)
        if not self.state.is_analyzing:
            self.analyze_button.setStyleSheet("")
            return
        ratio = self.state.analysis_progress_percent / 100.0
        filled, empty = "#2EA043", "#122618"
        if ratio <= 0.0:
            background = empty
        elif ratio >= 1.0:
            background = filled
        else:
            edge = min(1.0, ratio + 0.001)
            background = (f"qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 {filled}, stop:{ratio:.3f} {filled}, "
                          f"stop:{edge:.3f} {empty}, stop:1 {empty})")
        self.analyze_button.setStyleSheet(
            f"QPushButton#primary:disabled {{ background: {background}; border-color: #3FB950; color: white; }}")

    # ================================================================ 사용자 동작
    def _preset_changed(self, index: int) -> None:
        self.state.selected_preset = self.preset_combo.itemData(index)
        self.arch_label.setText(self.state.selected_preset.architecture if self.state.selected_preset else "")

    def _category_changed(self, text: str) -> None:
        if text:
            self.state.set_category_filter(text)

    def _search_changed(self, text: str) -> None:
        self._pending_search = text
        for edit in (self.defects_search, self.summary_search):
            if edit is not self.sender() and edit.text() != text:
                edit.blockSignals(True)
                edit.setText(text)
                edit.blockSignals(False)
        self._search_timer.start()

    def _open_project(self) -> None:
        path, _ = QFileDialog.getOpenFileName(self, "전검사 프로젝트 파일 열기", "",
                                              "전검사 프로젝트 (*.jcproj);;JSON 파일 (*.json);;모든 파일 (*)")
        if path:
            self.state.load_project(path)

    def _save_project(self) -> None:
        # 이미 저장된 프로젝트는 같은 파일에 바로 덮어쓰고, 처음 저장할 때만 파일 이름을 묻는다.
        if self.state.current_project_file_path:
            self.state.save_project(self.state.current_project_file_path)
        else:
            self._save_project_as()

    def _save_project_as(self) -> None:
        start = self.state.current_project_file_path or "embedded_analysis.jcproj"
        path, _ = QFileDialog.getSaveFileName(self, "전검사 프로젝트 다른 이름으로 저장", start,
                                              "전검사 프로젝트 (*.jcproj)")
        if path:
            if not path.endswith(".jcproj"):
                path += ".jcproj"
            self.state.save_project(path)

    def _open_ruleset(self) -> None:
        RulesetDialog(self.state.all_rules, self).exec()
        self.state.apply_rule_selection()

    def _add_paths(self, target: bool, folder: bool) -> None:
        title = "검사대상 코드 추가" if target else "참조코드(비대상) 추가"
        if folder:
            d = QFileDialog.getExistingDirectory(self, f"{title} - 폴더 선택")
            paths = [d] if d else []
        else:
            paths, _ = QFileDialog.getOpenFileNames(self, f"{title} - 파일 선택", "",
                                                    "C/C++ 소스 및 헤더 (*.c *.h *.cpp *.hpp);;모든 파일 (*)")
        for p in paths:
            (self.state.add_target_path if target else self.state.add_non_target_path)(p)

    def _selected_node(self, tree: QTreeWidget) -> SourceTreeNode | None:
        item = tree.currentItem()
        return item.data(0, OBJ_ROLE) if item is not None and item.isSelected() else None

    def _remove_node(self, target: bool) -> None:
        node = self._selected_node(self.target_tree if target else self.non_target_tree)
        if node is None:
            name = "검사대상 코드" if target else "참조코드(비대상)"
            self.state.set_status(f"제거할 파일이나 폴더를 [{name}] 목록에서 먼저 선택해 주세요.")
            return
        self.state.remove_node(node)

    def _move_to_non_target(self) -> None:
        node = self._selected_node(self.target_tree)
        if node is None:
            self.state.set_status("상단 [검사 대상 코드] 트리에서 비대상으로 제외할 폴더나 파일을 먼저 선택해 주세요.")
            return
        self.state.move_node_to_non_target(node)

    def _move_to_target(self) -> None:
        node = self._selected_node(self.non_target_tree)
        if node is None:
            self.state.set_status("하단 [비대상 / 참조 코드] 트리에서 검사 대상으로 포함할 폴더나 파일을 먼저 선택해 주세요.")
            return
        self.state.move_node_to_target(node)

    def _tree_selection_changed(self, tree: QTreeWidget) -> None:
        node = self._selected_node(tree)
        if node is not None and not node.is_directory:
            self.state.load_file_into_code_viewer(node.full_path)

    def _start_analysis(self) -> None:
        req = self.state.begin_analysis()
        if req is None:
            return
        worker = AnalysisWorker(req, self)
        worker.done.connect(self._analysis_done)
        worker.progress.connect(self._analysis_progress)
        worker.finished.connect(lambda w=worker: self._workers.remove(w) if w in self._workers else None)
        self._workers.append(worker)
        worker.start()

    def _analysis_progress(self, req, percent: float, message: str) -> None:
        self.state.report_progress(req, percent, message)

    def _analysis_done(self, req, result, error, cancelled) -> None:
        self.state.finish_analysis(req, result=result, error=error, cancelled=cancelled)
        issue = self.state.selected_issue
        if issue is not None:
            self._navigate(issue.file_path, issue.line)

    def _navigate(self, file_path: str, line: int) -> None:
        if not os.path.isfile(file_path):
            self.state.set_status(f"이동할 파일을 찾을 수 없습니다: {file_path}")
            return
        if os.path.abspath(file_path) != self.state.current_code_file_path:
            self.state.load_file_into_code_viewer(file_path)
        self.code_view.go_to_line(line)

    def _defect_selected(self, current, _previous) -> None:
        issue = current.data(OBJ_ROLE) if current.isValid() else None
        if issue is not None:
            self.state.select_issue(issue)
            self._navigate(issue.file_path, issue.line)

    def _defects_menu(self, pos) -> None:
        idx = self.defects_table.indexAt(pos)
        issue = idx.data(OBJ_ROLE) if idx.isValid() else None
        if issue is None:
            return
        menu = QMenu(self)
        menu.addAction("🔇 이 위치의 결함만 예외 처리", lambda: self.state.suppress_single_issue(issue))
        menu.addAction(f"🚫 규칙 '{issue.rule_id}' 전체 끄기", lambda: self.state.disable_rule_entirely(issue.rule_id))
        menu.exec(self.defects_table.viewport().mapToGlobal(pos))

    def _summary_selected(self, current, _previous) -> None:
        obj = current.data(0, OBJ_ROLE) if current is not None else None
        if isinstance(obj, AnalysisIssue):
            self.state.select_issue(obj)
            self._navigate(obj.file_path, obj.line)

    def _group_expanded(self, item: QTreeWidgetItem, expanded: bool) -> None:
        group = item.data(0, OBJ_ROLE)
        if hasattr(group, "is_expanded"):
            group.is_expanded = expanded

    def _summary_menu(self, pos) -> None:
        menu = QMenu(self)
        menu.addAction("모두 접기", lambda: self._set_all_summary_groups_expanded(False))
        menu.addAction("모두 펼치기", lambda: self._set_all_summary_groups_expanded(True))
        menu.exec(self.summary_tree.viewport().mapToGlobal(pos))

    def _set_all_summary_groups_expanded(self, expanded: bool) -> None:
        for i in range(self.summary_tree.topLevelItemCount()):
            item = self.summary_tree.topLevelItem(i)
            item.data(0, OBJ_ROLE).is_expanded = expanded
            item.setExpanded(expanded)

    def _note_selected(self, row, _col, _prev_row, _prev_col) -> None:
        item = self.notes_table.item(row, 0) if row >= 0 else None
        note = item.data(OBJ_ROLE) if item else None
        if note is not None and note.file_path:
            self._navigate(note.file_path, note.line)

    def _notes_menu(self, pos) -> None:
        menu = QMenu(self)
        menu.addAction("🔍 모든 누락 헤더를 폴더에서 일괄 검색하여 참조 코드에 추가...",
                       lambda: self._search_missing_headers(only_selected=False))
        menu.addAction("🔍 선택한 줄의 누락 헤더만 폴더에서 검색하여 참조 코드에 추가...",
                       lambda: self._search_missing_headers(only_selected=True))
        menu.exec(self.notes_table.viewport().mapToGlobal(pos))

    def _search_missing_headers(self, only_selected: bool) -> None:
        notes = self.state.notes
        items = []
        if only_selected:
            cell = self.notes_table.item(self.notes_table.currentRow(), 0)
            selected = cell.data(OBJ_ROLE) if cell else None
            items = group_missing_headers([selected] if selected is not None else [])
        # 선택한 행이 누락 헤더 노트가 아니면 전체 누락 헤더로 대신 검색
        if not items:
            items = group_missing_headers(notes)
        if not items:
            QMessageBox.information(self, "누락 헤더 없음",
                                    "현재 노트 목록에 누락된 포함 파일(#include) 안내가 없습니다.\n"
                                    "정적 분석 실행 후 누락된 헤더가 있을 때 사용할 수 있습니다.")
            return
        folder = QFileDialog.getExistingDirectory(self, f"누락된 헤더({len(items)}종)를 하위까지 탐색할 상위 폴더 선택")
        if not folder or not os.path.isdir(folder):
            return
        dialog = MissingHeaderDialog(items, folder, self)
        if dialog.exec() and dialog.selected_paths:
            for p in dialog.selected_paths:
                self.state.add_non_target_path(p)
            self.state.set_status(f"참조코드(비대상)에 헤더 파일 {len(dialog.selected_paths)}개를 추가했습니다. "
                                  "상단 [▶ 정적 분석 시작]을 다시 실행하면 누락 헤더가 반영됩니다.")

    def _current_suppressed(self) -> SuppressedIssueEntry | None:
        item = self.suppressed_table.item(self.suppressed_table.currentRow(), 0)
        return item.data(OBJ_ROLE) if item else None

    def _suppressed_selected(self, row, _col, _prev_row, _prev_col) -> None:
        entry = self._current_suppressed() if row >= 0 else None
        if entry is not None and entry.file_path:
            self._navigate(entry.file_path, entry.line)

    def _restore_selected_suppressed(self) -> None:
        entry = self._current_suppressed()
        if entry is not None:
            self.state.restore_suppressed_issue(entry)

    def _suppressed_menu(self, pos) -> None:
        if self._current_suppressed() is None:
            return
        menu = QMenu(self)
        menu.addAction("↩ 이 항목의 예외 처리 해제 (결함 목록으로 복원)", self._restore_selected_suppressed)
        menu.exec(self.suppressed_table.viewport().mapToGlobal(pos))

    def _code_line_selected(self, line) -> None:
        # 코드 뷰어에서 직접 줄을 선택한 경우에만 그 줄의 첫 번째 결함을 선택 (같은 줄의 다른 결함 선택은 유지)
        selected = self.state.selected_issue
        if line.issues and (selected is None or selected not in line.issues):
            self.state.select_issue(line.issues[0])

    def _overview_line_requested(self, line) -> None:
        self.code_view.go_to_line(line.line_number)
        if line.issues:
            self.state.select_issue(line.issues[0])

    def _keep_line(self, action, line) -> None:
        number = line.line_number
        action(line)
        self.code_view.go_to_line(number)

    def _disable_selected_rule(self) -> None:
        if self.state.selected_issue is not None:
            self.state.disable_rule_entirely(self.state.selected_issue.rule_id)

    def _open_external_editor(self) -> None:
        line = self.code_view.current_line()
        self.state.open_current_file_in_external_editor(line.line_number if line else 1)

    def closeEvent(self, event) -> None:  # noqa: N802
        # 창을 닫을 때 진행 중인 분석을 취소하여 Cppcheck/Clang-Tidy 프로세스가 남지 않도록 함
        self.state.cancel_analysis()
        for w in list(self._workers):
            w.wait(5000)
        super().closeEvent(event)


def _iter_tree_items(tree: QTreeWidget):
    """트리의 모든 항목을 깊이 우선으로 순회."""
    stack = [tree.topLevelItem(i) for i in range(tree.topLevelItemCount() - 1, -1, -1)]
    while stack:
        item = stack.pop()
        yield item
        stack.extend(item.child(i) for i in range(item.childCount() - 1, -1, -1))
