using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TimelapseConverter.Helpers;

/// <summary>
/// Windows 11 제목 표시줄을 Porcelain 배경색에 맞춘다 (Windows 10에서는 무시됨).
/// </summary>
static class TitleBar
{
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_TEXT_COLOR = 36;

    // COLORREF = 0x00BBGGRR
    const int Porcelain = 0x00F4F7F8; // #F8F7F4
    const int TextDark = 0x0025201D;  // #1D2025

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int caption = Porcelain, text = TextDark;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch { }
    }
}
