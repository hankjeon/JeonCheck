"""읽기 전용 코드 뷰어: 줄별 상태 색(상태바·배경), 상태 점, 줄 번호, 코드, [규칙ID +N] 배지와 툴팁."""

from __future__ import annotations

from PySide6.QtCore import QAbstractListModel, QModelIndex, QRect, QSize, Qt, Signal
from PySide6.QtGui import QColor, QFontMetrics, QPainter
from PySide6.QtWidgets import QListView, QMenu, QStyle, QStyledItemDelegate, QStyleOptionViewItem

from ..models import CodeLine, CodeLineStatus
from . import theme

LINE_ROLE = Qt.ItemDataRole.UserRole + 1

BAR_W, NUM_W, DOT_W, PAD = 4, 52, 18, 6


class CodeLineModel(QAbstractListModel):
    def __init__(self):
        super().__init__()
        self.lines: list[CodeLine] = []

    def set_lines(self, lines: list[CodeLine]) -> None:
        self.beginResetModel()
        self.lines = list(lines)
        self.endResetModel()

    def rowCount(self, parent=QModelIndex()) -> int:  # noqa: N802
        return 0 if parent.isValid() else len(self.lines)

    def data(self, index, role=Qt.ItemDataRole.DisplayRole):
        if not index.isValid():
            return None
        line = self.lines[index.row()]
        if role == Qt.ItemDataRole.DisplayRole:
            return line.code_text
        if role == Qt.ItemDataRole.ToolTipRole:
            return line.tooltip_text or None
        if role == LINE_ROLE:
            return line
        return None


class CodeLineDelegate(QStyledItemDelegate):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.font = theme.code_font(10)
        self.metrics = QFontMetrics(self.font)

    def sizeHint(self, option, index) -> QSize:  # noqa: N802
        line: CodeLine = index.data(LINE_ROLE)
        width = BAR_W + NUM_W + DOT_W + self.metrics.horizontalAdvance(line.code_text.replace("\t", "    "))
        if line.inline_annotation:
            width += 16 + self.metrics.horizontalAdvance(line.inline_annotation) + 20
        # 상태 배경색이 창 너비 전체에 칠해지도록 최소 너비를 뷰포트 너비로 맞춘다.
        view = self.parent()
        if view is not None:
            width = max(width, view.viewport().width())
        return QSize(width, self.metrics.height() + 4)

    def paint(self, painter: QPainter, option: QStyleOptionViewItem, index) -> None:
        line: CodeLine = index.data(LINE_ROLE)
        r = option.rect
        painter.save()
        bar, bg = theme.STATUS_COLORS.get(line.status, (None, None))
        painter.fillRect(r, QColor(bg) if bg else QColor(theme.BG_EDITOR))
        if option.state & QStyle.StateFlag.State_Selected:
            painter.fillRect(r, QColor(14, 99, 156, 110))
        if bar:
            painter.fillRect(QRect(r.left(), r.top(), BAR_W, r.height()), QColor(bar))

        painter.setFont(self.font)
        x = r.left() + BAR_W
        painter.setPen(QColor(theme.TEXT_SECONDARY))
        painter.drawText(QRect(x, r.top(), NUM_W - PAD, r.height()),
                         Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter, str(line.line_number))
        x += NUM_W
        if bar and line.status != CodeLineStatus.ANALYZED:
            painter.setRenderHint(QPainter.RenderHint.Antialiasing)
            painter.setBrush(QColor(bar))
            painter.setPen(Qt.PenStyle.NoPen)
            d = 8
            painter.drawEllipse(x + (DOT_W - d) // 2, r.top() + (r.height() - d) // 2, d, d)
        x += DOT_W

        text = line.code_text.replace("\t", "    ")
        font = self.font
        if line.status == CodeLineStatus.SKIPPED:
            font = theme.code_font(10)
            font.setItalic(True)
            painter.setPen(QColor("#6E7681"))
        else:
            painter.setPen(QColor(theme.TEXT_PRIMARY))
        painter.setFont(font)
        text_w = QFontMetrics(font).horizontalAdvance(text)
        painter.drawText(QRect(x, r.top(), max(text_w, 1) + 2, r.height()),
                         Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter, text)

        if line.inline_annotation:
            ax = x + text_w + 16
            painter.setFont(self.font)
            aw = self.metrics.horizontalAdvance(line.inline_annotation) + 10
            badge = QRect(ax, r.top() + 1, aw, r.height() - 2)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(QColor(bar or theme.BG_HEADER).darker(170))
            painter.drawRoundedRect(badge, 3, 3)
            painter.setPen(QColor(bar) if bar else QColor(theme.TEXT_SECONDARY))
            painter.drawText(badge, Qt.AlignmentFlag.AlignCenter, line.inline_annotation)
        painter.restore()


class CodeView(QListView):
    """줄 선택(lineSelected), 줄 예외/복원 요청(suppressRequested/restoreRequested) 신호를 보낸다."""

    lineSelected = Signal(object)
    suppressRequested = Signal(object)
    restoreRequested = Signal(object)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.code_model = CodeLineModel()
        self.setModel(self.code_model)
        self.setItemDelegate(CodeLineDelegate(self))
        self.setResizeMode(QListView.ResizeMode.Adjust)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.setStyleSheet(f"QListView {{ background: {theme.BG_EDITOR}; border: 0; }}")
        self.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.customContextMenuRequested.connect(self._context_menu)
        self.selectionModel().currentChanged.connect(self._current_changed)
        self._programmatic = False

    def set_lines(self, lines: list[CodeLine]) -> None:
        self._programmatic = True
        try:
            self.code_model.set_lines(lines)
        finally:
            self._programmatic = False

    def current_line(self) -> CodeLine | None:
        idx = self.currentIndex()
        return idx.data(LINE_ROLE) if idx.isValid() else None

    def go_to_line(self, line: int) -> None:
        """결함 목록 등에서 이동: 줄 선택만 바꾸고 lineSelected는 보내지 않는다."""
        if not 0 < line <= self.code_model.rowCount():
            return
        self._programmatic = True
        try:
            idx = self.code_model.index(line - 1)
            self.setCurrentIndex(idx)
            self.scrollTo(idx, QListView.ScrollHint.PositionAtCenter)
        finally:
            self._programmatic = False

    def _current_changed(self, current, _previous) -> None:
        if not self._programmatic and current.isValid():
            self.lineSelected.emit(current.data(LINE_ROLE))

    def _context_menu(self, pos) -> None:
        idx = self.indexAt(pos)
        if not idx.isValid():
            return
        line: CodeLine = idx.data(LINE_ROLE)
        menu = QMenu(self)
        if line.can_suppress_line:
            menu.addAction("🔇 이 줄 예외", lambda: self.suppressRequested.emit(line))
        if line.can_restore_line:
            menu.addAction("↩ 다시 검사(복원)", lambda: self.restoreRequested.emit(line))
        if not menu.isEmpty():
            menu.exec(self.viewport().mapToGlobal(pos))
