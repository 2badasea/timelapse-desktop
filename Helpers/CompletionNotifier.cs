using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TimelapseConverter.Helpers;

/// <summary>
/// 창을 내려놓은 사이 변환이 끝났을 때 Windows 알림(알림 센터)과 작업 표시줄 깜빡임으로 알려 준다.
/// 별도 패키지 없이 알림 영역 아이콘(Shell_NotifyIcon)의 풍선 알림을 쓰며,
/// Windows 10/11에서는 일반 알림 창으로 표시된다. 알림 영역 아이콘은 사용자가 창으로 돌아오면 지운다.
/// </summary>
sealed class CompletionNotifier : IDisposable
{
    const int WM_APP = 0x8000;
    const int CallbackMessage = WM_APP + 41;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    const int NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    const int NIN_BALLOONUSERCLICK = 0x405;
    const int WM_LBUTTONUP = 0x202;

    readonly Window _window;
    IntPtr _hwnd, _largeIcon, _smallIcon;
    HwndSource? _source;
    bool _iconShown;

    public CompletionNotifier(Window window) => _window = window;

    /// <summary>창이 활성 상태가 아닐 때만 알린다. 알림을 띄웠으면 true.</summary>
    public bool NotifyIfInactive(string title, string text)
    {
        if (_window.IsActive && _window.WindowState != WindowState.Minimized) return false;
        return Show(title, text);
    }

    public bool Show(string title, string text)
    {
        try
        {
            EnsureInitialized();
            FlashTaskbar();
            return ShowBalloon(title, text);
        }
        catch
        {
            return false; // 알림 실패는 변환 결과에 영향 없음
        }
    }

    void EnsureInitialized()
    {
        if (_source != null) return;
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        if (Environment.ProcessPath is string exe)
            ExtractIconEx(exe, 0, out _largeIcon, out _smallIcon, 1);
        _window.Activated += (_, _) => RemoveIcon();
    }

    bool ShowBalloon(string title, string text)
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.szTip = "TimeLapse Converter";
        if (!_iconShown)
        {
            if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;
            _iconShown = true;
        }

        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = _largeIcon != IntPtr.Zero ? NIIF_USER | NIIF_LARGE_ICON : NIIF_USER;
        data.hBalloonIcon = _largeIcon;
        return Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;
        int evt = (int)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case NIN_BALLOONUSERCLICK:
            case WM_LBUTTONUP:
                BringToFront(); // 창이 활성화되면 아이콘도 지워진다
                break;
                // 알림이 화면에서 사라져도(NIN_BALLOONTIMEOUT/HIDE) 알림 센터에 남도록
                // 아이콘은 사용자가 창으로 돌아올 때까지 유지
        }
        handled = true;
        return IntPtr.Zero;
    }

    void BringToFront()
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void FlashTaskbar()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }

    void RemoveIcon()
    {
        if (!_iconShown) return;
        var data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _iconShown = false;
    }

    NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _smallIcon,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        RemoveIcon();
        _source?.RemoveHook(WndProc);
        if (_largeIcon != IntPtr.Zero) DestroyIcon(_largeIcon);
        if (_smallIcon != IntPtr.Zero) DestroyIcon(_smallIcon);
        _largeIcon = _smallIcon = IntPtr.Zero;
    }

    // ───────────────────────── Win32 ─────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO info);
}
