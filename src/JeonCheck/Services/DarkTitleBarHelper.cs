using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JeonCheck.Services;

/// <summary>
/// Windows 10/11 창의 상단 타이틀바(제목 표시줄)를 다크 테마 색상(#1E1E1E)으로 적용하는 헬퍼
/// </summary>
public static class DarkTitleBarHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Window window)
    {
        if (window.IsInitialized)
        {
            ApplyToHandle(window);
        }

        window.SourceInitialized += (_, _) => ApplyToHandle(window);
    }

    private static void ApplyToHandle(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));

            // COLORREF 포맷 (0x00BBGGRR): #1E1E1E -> 0x001E1E1E
            int captionColor = 0x001E1E1E;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // 타이틀 텍스트 색상: #E6EDF3 -> R=0xE6, G=0xED, B=0xF3 -> 0x00F3EDE6
            int textColor = 0x00F3EDE6;
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }
        catch
        {
            // 구버전 Windows 등 DWM 미지원 환경에서는 안전하게 무시
        }
    }
}
