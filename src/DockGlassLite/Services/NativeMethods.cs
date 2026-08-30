using System.Runtime.InteropServices;
using System.Text;

namespace DockGlassLite.Services;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExNoActivate = 0x08000000L;

    public static readonly nint HwndNoTopMost = new(-2);

    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint AbmGetTaskbarPos = 0x00000005;
    public const uint AbeBottom = 3;
    public const int SwRestore = 9;

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out WinRect lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    // ---- DWM 液态玻璃 / 深色标题栏 / 圆角 ----
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaUseImmersiveDarkModeLegacy = 19;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaSystemBackdropType = 38;

    public const int DwmwcpRound = 2;

    public const int DwmsbtNone = 1;
    public const int DwmsbtMainWindow = 2;      // Mica
    public const int DwmsbtTransientWindow = 3; // Acrylic
    public const int DwmsbtTabbedWindow = 4;    // Mica Alt

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref NativeMargins pMarInset);

    // ---- SetWindowCompositionAttribute：自定义染色的 Acrylic 模糊 ----
    public const int WcaAccentPolicy = 19;

    public const int AccentDisabled = 0;
    public const int AccentEnableBlurBehind = 3;
    public const int AccentEnableAcrylicBlurBehind = 4;

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] nint[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        string pszPath,
        nint pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    public static extern nint SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    public static string GetWindowClassName(nint hwnd)
    {
        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : "";
    }

    public static bool TryGetTaskbarPosition(out AppBarData appBarData)
    {
        appBarData = new AppBarData { CbSize = (uint)Marshal.SizeOf<AppBarData>() };
        return SHAppBarMessage(AbmGetTaskbarPos, ref appBarData) != nint.Zero;
    }
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    void GetImage(NativeSize size, ShellItemImageFactoryFlags flags, out nint phbm);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Cx;
    public int Cy;

    public NativeSize(int width, int height)
    {
        Cx = width;
        Cy = height;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMargins
{
    public int Left;
    public int Right;
    public int Top;
    public int Bottom;

    public NativeMargins(int left, int right, int top, int bottom)
    {
        Left = left;
        Right = right;
        Top = top;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    public int AccentState;
    public int AccentFlags;
    public uint GradientColor; // ABGR：高字节是 Alpha
    public int AnimationId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    public int Attribute;
    public nint Data;
    public int SizeOfData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[Flags]
internal enum ShellItemImageFactoryFlags
{
    ResizeToFit = 0x00000000,
    BiggerSizeOk = 0x00000001,
    IconOnly = 0x00000004,
    ScaleUp = 0x00000100
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Intersects(WinRect other)
    {
        return Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AppBarData
{
    public uint CbSize;
    public nint HWnd;
    public uint CallbackMessage;
    public uint Edge;
    public WinRect Rect;
    public int LParam;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ShFileInfo
{
    public nint hIcon;
    public int iIcon;
    public uint dwAttributes;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}
