"""전검사(JeonCheck) PySide6 앱 실행: python -m jeoncheck"""

import sys

from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

from .paths import RESOURCES_DIR
from .state import AppState
from .ui import theme
from .ui.main_window import MainWindow


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("JeonCheck")
    app.setWindowIcon(QIcon(str(RESOURCES_DIR / "app_icon.png")))
    theme.apply_theme(app)
    window = MainWindow(AppState())
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
