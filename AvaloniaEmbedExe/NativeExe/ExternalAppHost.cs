using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AvaloniaEmbedExe.Controls;

public class ExternalAppHost : NativeControlHost
{
    private Process? _process;
    private IntPtr _embeddedHwnd;
    private HashSet<IntPtr> _windowsBeforeLaunch = new();

    // ---- 尺寸同步（WinEventHook + 防抖）----
    private IntPtr _winEventHookHandle;
    private InteropUtil.WinEventProc? _winEventProcDelegate; // prevent GC
    private (int width, int height) _embeddedNaturalSize;
    private DispatcherTimer? _debounceTimer;
    private (int width, int height) _pendingSize;

    /// <summary>
    /// 要启动的外部程序路径或名称。
    /// 默认使用项目自带的经典 Win32 计算器 (Calc/calc1.exe)。
    /// </summary>
    public string ExePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Calc", "calc1.exe");

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _windowsBeforeLaunch = GetVisibleWindowHandles();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExePath,
                WindowStyle = ProcessWindowStyle.Hidden,  // 隐藏启动，避免在 SetParent 嵌入前闪现到桌面
                UseShellExecute = true
            }
        };

        if (!_process.Start())
            throw new InvalidOperationException($"无法启动程序: {ExePath}");

        _embeddedHwnd = WaitForMainWindowHandle(_process, timeoutMs: 15000);

        if (_embeddedHwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"无法获取外部程序窗口句柄 (进程: {ExePath}, PID: {_process.Id})。\n" +
                "程序可能启动失败或窗口未正常显示，请检查 ExePath 设置。");

        Debug.WriteLine($"[ExternalAppHost] Got embedded HWND: {_embeddedHwnd}, calculator PID: {_process.Id}");

        // 验证 HWND 所属进程（调试用）
        InteropUtil.GetWindowThreadProcessId(_embeddedHwnd, out uint hwndOwnerPid);
        Debug.WriteLine($"[ExternalAppHost] HWND belongs to PID: {hwndOwnerPid}");

        // 4. 不主动 ShowWindow — 窗口保持隐藏直到 SetParent 嵌入后
        //    由 ArrangeOverride 的 SetWindowPos(SWP_SHOWWINDOW) 首次显示

        RemoveWindowDecorations(_embeddedHwnd);

        _embeddedNaturalSize = GetWindowSize(_embeddedHwnd);
        Width = _embeddedNaturalSize.width;
        Height = _embeddedNaturalSize.height;

        _winEventProcDelegate = OnWinEvent;
        // 获取嵌入窗口的线程 ID 和进程 ID，用于 SetWinEventHook 精确过滤
        uint embeddedThreadId = InteropUtil.GetWindowThreadProcessId(_embeddedHwnd, out uint embeddedProcessId);

        Debug.WriteLine($"[ExternalAppHost] Window thread: pid={embeddedProcessId}, tid={embeddedThreadId}");

        _winEventHookHandle = InteropUtil.SetWinEventHook(
            InteropUtil.EVENT_OBJECT_LOCATIONCHANGE,  // 只监听位置/大小变化
            InteropUtil.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventProcDelegate,
            embeddedProcessId,      // 只监听嵌入应用的进程
            embeddedThreadId,       // 只监听嵌入窗口的线程
            InteropUtil.WINEVENT_OUTOFCONTEXT);

        // 初始化防抖定时器（100ms 无新事件后才更新）
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounceTimer.Tick += (_, _) => ApplyPendingSize();

        Debug.WriteLine($"[ExternalAppHost] WinEventHook 成功: handle={_winEventHookHandle}, pid={embeddedProcessId}, tid={embeddedThreadId}");
        Console.WriteLine($"[ExternalAppHost] WinEventHook 成功: handle={_winEventHookHandle}, pid={embeddedProcessId}, tid={embeddedThreadId}");

        return new PlatformHandle(_embeddedHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _debounceTimer?.Stop();
        _debounceTimer = null;

        if (_winEventHookHandle != IntPtr.Zero)
        {
            // 注销 WinEventHook，释放系统事件监听资源
        InteropUtil.UnhookWinEvent(_winEventHookHandle);
            _winEventHookHandle = IntPtr.Zero;
        }
        _winEventProcDelegate = null;

        try
        {
            if (_embeddedHwnd != IntPtr.Zero)
            {
                // 向嵌入窗口投递 WM_CLOSE 消息，让应用自行优雅关闭（比直接 Kill 更安全）
                InteropUtil.PostMessage(_embeddedHwnd, InteropUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(300);
            }

            if (_process is { HasExited: false })
            {
                try { _process.CloseMainWindow(); } catch { }
                Thread.Sleep(500);
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            _process?.Dispose();

            if (_embeddedHwnd != IntPtr.Zero)
            {
                try
                {
                    // 根据 HWND 反查所属进程 PID，确认是否需要强制关闭
                    InteropUtil.GetWindowThreadProcessId(_embeddedHwnd, out uint windowPid);
                    if (windowPid != 0)
                    {
                        var windowProcess = Process.GetProcessById((int)windowPid);
                        string procName = windowProcess.ProcessName.ToLowerInvariant();
                        if (procName.Contains("applicationframehost") ||
                            procName.Contains("calc") ||
                            procName.Contains("win32calc") ||
                            procName.Contains("speedcrunch"))
                        {
                            if (!windowProcess.HasExited)
                            {
                                windowProcess.CloseMainWindow();
                                Thread.Sleep(300);
                                if (!windowProcess.HasExited)
                                    windowProcess.Kill();
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        _embeddedHwnd = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    // ---- 布局：防止拉伸 + 居中 ----

    protected override Size MeasureOverride(Size availableSize)
    {
        var (w, h) = _embeddedNaturalSize;
        if (w > 0 && h > 0)
            return new Size(w, h);

        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 不调用 base — 避免 NativeControlHost 在布局中强制修改 HWND 尺寸，
        // 否则嵌入窗口的自然尺寸变化会被覆盖，定时器无法检测到。
        if (_embeddedHwnd != IntPtr.Zero)
        {
            var currentSize = GetWindowSize(_embeddedHwnd);
            int nw = currentSize.width > 0 ? currentSize.width : _embeddedNaturalSize.width;
            int nh = currentSize.height > 0 ? currentSize.height : _embeddedNaturalSize.height;

            if (nw > 0 && nh > 0)
            {
                int offsetX = Math.Max(0, (int)(finalSize.Width - nw) / 2);
                int offsetY = Math.Max(0, (int)(finalSize.Height - nh) / 2);

                // 定位嵌入窗口到控件内的居中位置，SWP_SHOWWINDOW 使其在 SetParent 后首次可见
                InteropUtil.SetWindowPos(_embeddedHwnd, IntPtr.Zero,
                    offsetX, offsetY, nw, nh,
                    InteropUtil.SWP_NOZORDER | InteropUtil.SWP_NOACTIVATE | InteropUtil.SWP_SHOWWINDOW);
            }
        }

        return finalSize;
    }

    // ---- WinEventHook 回调 ----

    /// <summary>
    /// WinEventHook 回调：当嵌入窗口的位置/大小变化时触发。
    /// 使用防抖：记录最新尺寸，100ms 无新事件后才更新。
    /// </summary>
    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd != _embeddedHwnd || idObject != 0)
            return;

        if (eventType != InteropUtil.EVENT_OBJECT_LOCATIONCHANGE)
            return;

        var currentSize = GetWindowSize(hwnd);
        if (currentSize.width <= 0 || currentSize.height <= 0)
            return;

        if (currentSize != _embeddedNaturalSize)
        {
            // 记录待应用的尺寸，重置防抖定时器
            _pendingSize = currentSize;
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
    }

    /// <summary>
    /// 防抖定时器回调：100ms 无新事件后应用尺寸更新。
    /// </summary>
    private void ApplyPendingSize()
    {
        _debounceTimer?.Stop();

        if (_pendingSize != _embeddedNaturalSize && _pendingSize.width > 0 && _pendingSize.height > 0)
        {
            _embeddedNaturalSize = _pendingSize;
            Width = _pendingSize.width;
            Height = _pendingSize.height;
            InvalidateMeasure();
            Debug.WriteLine($"[ExternalAppHost] Avalonia control updated: Width={Width}, Height={Height}");
        }
    }

    private static (int width, int height) GetWindowSize(IntPtr hwnd)
    {
        // 获取窗口在屏幕坐标系中的外接矩形，计算出宽高
        if (InteropUtil.GetWindowRect(hwnd, out var rect))
            return (rect.Right - rect.Left, rect.Bottom - rect.Top);
        return (0, 0);
    }

    // ---- 窗口句柄查找 ----

    private IntPtr WaitForMainWindowHandle(Process process, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
            }
            catch { }

            IntPtr hwnd = FindWindowByThreadOrProcess(process);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            Thread.Sleep(200);
        }

        return IntPtr.Zero;
    }

    private IntPtr FindWindowByThreadOrProcess(Process process)
    {
        IntPtr foundHwnd = IntPtr.Zero;
        uint targetPid = (uint)process.Id;

        try
        {
            ProcessThreadCollection? threads = null;
            try { threads = process.Threads; } catch { }

            if (threads != null)
            {
                foreach (ProcessThread thread in threads)
                {
                    int threadId = thread.Id;
                    // 枚举系统中所有顶层窗口，逐一检查是否属于目标线程或进程
                    InteropUtil.EnumWindows((hwnd, _) =>
                    {
                        if (_windowsBeforeLaunch.Contains(hwnd))
                            return true;

                        // 获取当前枚举窗口的线程 ID 和进程 ID，与目标比对
                        uint wndPid;
                        uint wndTid = InteropUtil.GetWindowThreadProcessId(hwnd, out wndPid);

                        if (wndTid == (uint)threadId || wndPid == targetPid)
                        {
                            // 过滤掉隐藏/最小化窗口，只匹配可见窗口
                            if (InteropUtil.IsWindowVisible(hwnd))
                            {
                                foundHwnd = hwnd;
                                return false;
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (foundHwnd != IntPtr.Zero)
                        return foundHwnd;
                }
            }
        }
        catch { }

        // 兜底查找：按窗口标题匹配（应对 Process.MainWindowHandle 失效或进程树不一致的情况）
        InteropUtil.EnumWindows((hwnd, _) =>
        {
            if (_windowsBeforeLaunch.Contains(hwnd))
                return true;
            if (!InteropUtil.IsWindowVisible(hwnd)) // 跳过不可见窗口
                return true;

            // 读取窗口标题，与目标应用名称比对
            var sb = new char[256];
            InteropUtil.GetWindowText(hwnd, sb, 256);
            string title = new string(sb).TrimEnd('\0');

            if (title.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("计算器") ||
                title.Contains("SpeedCrunch", StringComparison.OrdinalIgnoreCase))
            {
                foundHwnd = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }

    private static HashSet<IntPtr> GetVisibleWindowHandles()
    {
        var result = new HashSet<IntPtr>();
        // 快照当前桌面上所有可见窗口句柄，用于后续差集查找时排除启动前已存在的窗口
        InteropUtil.EnumWindows((hwnd, _) =>
        {
            if (InteropUtil.IsWindowVisible(hwnd)) // 只记录可见窗口
                result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---- 窗口样式 ----

    private static void RemoveWindowDecorations(IntPtr hwnd)
    {
        // 读取窗口当前的样式标志位
        uint style = InteropUtil.GetWindowLong(hwnd, InteropUtil.GWL_STYLE);
        // 移除标题栏(WS_CAPTION)、可调整大小边框(WS_THICKFRAME)、系统菜单(WS_SYSMENU)
        style &= ~(InteropUtil.WS_CAPTION | InteropUtil.WS_THICKFRAME | InteropUtil.WS_SYSMENU);
        // 写回修改后的样式
        InteropUtil.SetWindowLong(hwnd, InteropUtil.GWL_STYLE, style);

        // SWP_FRAMECHANGED 触发窗口重新应用非客户区样式，使上述修改立即生效
        InteropUtil.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            InteropUtil.SWP_NOMOVE | InteropUtil.SWP_NOSIZE | InteropUtil.SWP_FRAMECHANGED);
    }
}
