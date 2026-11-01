"""다크 테마 팔레트·스타일과 코드 폰트(D2Coding) 로드. 색상은 WPF 앱(App.xaml, doc/04_ui_design.md)과 같다."""

from __future__ import annotations

from PySide6.QtGui import QColor, QFont, QFontDatabase, QPalette
from PySide6.QtWidgets import QApplication

from ..models import CodeLineStatus
from ..paths import RESOURCES_DIR

BG_EDITOR = "#1E1E1E"
BG_PANEL = "#252526"
BG_TOOLBAR = "#2D2D30"
BG_HEADER = "#333337"
BORDER = "#3E3E42"
TEXT_PRIMARY = "#E6EDF3"
TEXT_SECONDARY = "#9DA7B3"
ACCENT_BLUE = "#0E639C"
ACCENT_GREEN = "#2EA043"

# 코드 줄 상태: (상태바 색, 배경색)
STATUS_COLORS = {
    CodeLineStatus.ANALYZED: ("#2EA043", "#122119"),
    CodeLineStatus.SKIPPED: ("#484F58", "#161618"),
    CodeLineStatus.ERROR: ("#F85149", "#3B1D20"),
    CodeLineStatus.WARNING: ("#D29922", "#342814"),
    CodeLineStatus.SUPPRESSED: ("#8957E5", "#1F192C"),
}

SEVERITY_COLORS = {"Error": "#F85149", "Warning": "#D29922", "Style": "#58A6FF", "Info": "#9DA7B3"}

_code_font_family = "monospace"


def code_font(point_size: int = 10) -> QFont:
    f = QFont(_code_font_family, point_size)
    f.setStyleHint(QFont.StyleHint.Monospace)
    return f


def apply_theme(app: QApplication) -> None:
    global _code_font_family
    font_id = QFontDatabase.addApplicationFont(str(RESOURCES_DIR / "D2Coding.ttf"))
    families = QFontDatabase.applicationFontFamilies(font_id) if font_id >= 0 else []
    if families:
        _code_font_family = families[0]

    app.setStyle("Fusion")
    p = QPalette()
    c = QColor
    p.setColor(QPalette.ColorRole.Window, c(BG_PANEL))
    p.setColor(QPalette.ColorRole.WindowText, c(TEXT_PRIMARY))
    p.setColor(QPalette.ColorRole.Base, c(BG_EDITOR))
    p.setColor(QPalette.ColorRole.AlternateBase, c("#202022"))
    p.setColor(QPalette.ColorRole.ToolTipBase, c(BG_TOOLBAR))
    p.setColor(QPalette.ColorRole.ToolTipText, c(TEXT_PRIMARY))
    p.setColor(QPalette.ColorRole.Text, c(TEXT_PRIMARY))
    p.setColor(QPalette.ColorRole.PlaceholderText, c(TEXT_SECONDARY))
    p.setColor(QPalette.ColorRole.Button, c(BG_TOOLBAR))
    p.setColor(QPalette.ColorRole.ButtonText, c(TEXT_PRIMARY))
    p.setColor(QPalette.ColorRole.Highlight, c(ACCENT_BLUE))
    p.setColor(QPalette.ColorRole.HighlightedText, c("#FFFFFF"))
    p.setColor(QPalette.ColorRole.Link, c("#58A6FF"))
    for role in (QPalette.ColorRole.WindowText, QPalette.ColorRole.Text, QPalette.ColorRole.ButtonText):
        p.setColor(QPalette.ColorGroup.Disabled, role, c("#6E7681"))
    app.setPalette(p)
    app.setStyleSheet(f"""
        QToolTip {{ color: {TEXT_PRIMARY}; background: {BG_TOOLBAR}; border: 1px solid {BORDER}; }}
        QPushButton {{ background: #3A3D41; border: 1px solid #4E5157; border-radius: 3px; padding: 4px 10px; }}
        QPushButton:hover {{ background: #45494E; }}
        QPushButton:disabled {{ color: #6E7681; background: #2D2D30; }}
        QPushButton#primary {{ background: {ACCENT_GREEN}; border-color: {ACCENT_GREEN}; color: white; font-weight: bold; }}
        QPushButton#primary:disabled {{ background: #23472C; color: #8B949E; }}
        QPushButton#danger {{ background: #6E2B2B; border-color: #8B3A3A; }}
        QHeaderView::section {{ background: {BG_TOOLBAR}; color: {TEXT_PRIMARY}; padding: 4px 8px;
                                border: 0; border-right: 1px solid {BORDER}; border-bottom: 1px solid {BORDER}; }}
        QTabBar::tab {{ background: {BG_TOOLBAR}; padding: 6px 12px; border: 1px solid {BORDER}; border-bottom: 0; }}
        QTabBar::tab:selected {{ background: {BG_EDITOR}; color: white; }}
        QTabWidget::pane {{ border: 1px solid {BORDER}; }}
        QLabel#sectionHeader {{ background: {BG_HEADER}; padding: 5px 8px; font-weight: bold; }}
        QLabel#secondary {{ color: {TEXT_SECONDARY}; }}
        QFrame#toolbar {{ background: {BG_TOOLBAR}; border-bottom: 1px solid {BORDER}; }}
        QSplitter::handle {{ background: {BORDER}; }}
    """)
