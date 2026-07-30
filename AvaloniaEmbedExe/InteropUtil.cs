using System;
using System.Runtime.InteropServices;

namespace AvaloniaEmbedExe
{
    //https://learn.microsoft.com/zh-cn/dotnet/standard/native-interop/tutorial-custom-marshaller
    // runtime src/libraries/Common/src/Interop/Windows
    public static partial class InteropUtil
    {
        // ---- INI 文件 ----

        [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial long WritePrivateProfileString(string section, string key, string val, string filePath);

        // ---- 窗口样式常量 ----

        public const int GWL_STYLE = -16;
        public const uint WS_CAPTION = 0x00C00000;
        public const uint WS_THICKFRAME = 0x00040000;
        public const uint WS_SYSMENU = 0x00080000;

        // ---- SetWindowPos 标志 ----

        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        // ---- ShowWindow 常量 ----

        public const int SW_SHOW = 5;

        // ---- 窗口消息常量 ----

        public const uint WM_CLOSE = 0x0010;

        // ---- 窗口样式操作 ----

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        public static partial uint GetWindowLong(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        public static partial uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        // ---- 窗口枚举 ----

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        // ---- 进程/窗口管理 ----

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // ---- 窗口尺寸 ----

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // ---- 窗口显示 ----

        [LibraryImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // ---- WinEventHook（UI 事件钩子）----

        public delegate void WinEventProc(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime);

        [LibraryImport("user32.dll")]
        public static partial IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess, uint idThread,
            uint dwFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnhookWinEvent(IntPtr hWinEventHook);

        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        // ---- 窗口激活辅助方法 ----

        private const int WS_SHOWNORMAL = 1;
        private const int SW_SHOWMAXIMIZED = 3;

        [LibraryImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

        [LibraryImport("User32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);

        public static bool SetWndTopNormal(IntPtr hWnd)
        {
            return ShowWindowAsync(hWnd, WS_SHOWNORMAL) && SetForegroundWindow(hWnd);
        }

        public static bool SetWndTopMaximized(IntPtr hWnd)
        {
            return ShowWindowAsync(hWnd, SW_SHOWMAXIMIZED) && SetForegroundWindow(hWnd);
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(IntPtr hwnd);
    }
}
