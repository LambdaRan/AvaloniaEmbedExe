using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AvaloniaEmbedExe.Controls
{
    /// <summary>
    /// 一个空的 <c>WS_CHILD</c> 容器窗口，作为 Avalonia 与被嵌入的外部程序窗口之间的隔离层。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须有这一层。</b></para>
    /// <para>
    /// Avalonia 的 <c>Win32NativeControlAttachment.ShowInBounds</c> 会无条件执行
    /// <c>MoveWindow(child, 0, 0, bounds.Width * RenderScaling, bounds.Height * RenderScaling, true)</c>
    /// —— 也就是说，凡是从 <c>CreateNativeControlCore</c> 返回的那个 HWND，都会被 Avalonia 强行拉伸。
    /// </para>
    /// <para>
    /// 如果直接把外部程序的窗口交给 Avalonia，就会形成一个自激回路：
    /// Avalonia 拉伸外部窗口 → 触发 <c>EVENT_OBJECT_LOCATIONCHANGE</c> → 我们把新尺寸写回控件的
    /// Width/Height → Bounds 变化 → Avalonia 再乘一次 RenderScaling 拉伸 → …
    /// 在非 100% 缩放下这是个 <c>Wₙ₊₁ = Wₙ × scale</c> 的等比数列，窗口会指数级膨胀。
    /// </para>
    /// <para>
    /// 有了容器层，Avalonia 拉伸的是这个空容器（无副作用），外部程序窗口成为容器的子窗口，
    /// 位置和尺寸完全由我们掌控，回路被切断。副产品是"不拉伸 + 居中"这类需求终于能真正生效。
    /// </para>
    /// </remarks>
    internal sealed class ContainerWindow : IPlatformHandle, INativeControlHostDestroyableControlHandle
    {
        private const string ClassName = "AvaloniaEmbedExe.ExternalAppContainer";

        /// <summary>系统色索引 COLOR_BTNFACE，用作 hbrBackground 时需 +1。</summary>
        private const int COLOR_BTNFACE = 15;

        // 窗口类按进程注册一次即可；委托必须由静态字段持有，否则会被 GC 回收导致
        // 系统回调到已释放的存根上（表现为随机崩溃）。
        private static readonly WNDPROC WndProcDelegate = WndProc;
        private static readonly object RegistrationGate = new();
        private static ushort _classAtom;

        // GetModuleHandle 的友好重载返回 FreeLibrarySafeHandle（Dispose 会调 FreeLibrary）。
        // 对 GetModuleHandle(null) 拿到的 EXE 自身模块绝不能 FreeLibrary，
        // 因此这里用静态字段持有、永不 Dispose。
        private static readonly FreeLibrarySafeHandle ModuleHandle = PInvoke.GetModuleHandle(null);

        private HWND _handle;

        // unsafe：CreateWindowEx 的友好重载签名里带 void* lpParam，调用它需要不安全上下文
        public unsafe ContainerWindow(IntPtr parent)
        {
            ushort atom = EnsureClassRegistered();
            if (atom == 0)
                throw new InvalidOperationException(
                    $"注册容器窗口类失败, Win32Error={Marshal.GetLastWin32Error()}");

            HWND hwnd = PInvoke.CreateWindowEx(
                dwExStyle: 0,
                lpClassName: ClassName,   // 按类名创建（与原先传类原子语义等价）
                lpWindowName: null,
                // WS_CLIPCHILDREN: 容器自身重绘时不覆盖子窗口区域，避免嵌入应用闪烁
                dwStyle: WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPCHILDREN,
                X: 0, Y: 0, nWidth: 1, nHeight: 1,
                hWndParent: (HWND)parent,
                hMenu: null,
                hInstance: ModuleHandle,
                lpParam: null);

            if (hwnd == HWND.Null)
                throw new InvalidOperationException(
                    $"创建容器窗口失败, Win32Error={Marshal.GetLastWin32Error()}");

            _handle = hwnd;
        }

        public IntPtr Handle => _handle;

        public string? HandleDescriptor => "HWND";

        /// <summary>容器当前的客户区尺寸（物理像素）。</summary>
        public (int width, int height) ClientSizePx =>
            PInvoke.GetWindowRect(_handle, out var r)
                ? (r.right - r.left, r.bottom - r.top)
                : (0, 0);

        public void Destroy()
        {
            if (_handle == HWND.Null)
                return;

            HWND handle = _handle;
            _handle = HWND.Null;

            // 走应用退出路径时，顶层窗口可能已先被销毁，容器作为其子窗口会被 Windows
            // 一并回收 —— 此时句柄已失效，DestroyWindow 必然失败，无需当成错误上报。
            if (!PInvoke.IsWindow(handle))
                return;

            if (!PInvoke.DestroyWindow(handle))
                Debug.WriteLine($"[ContainerWindow] DestroyWindow 失败, Win32Error={Marshal.GetLastWin32Error()}");
        }

        private static unsafe ushort EnsureClassRegistered()
        {
            lock (RegistrationGate)
            {
                if (_classAtom != 0)
                    return _classAtom;

                // WNDCLASSEXW 的字符串字段是 PCWSTR，需要 fixed 固定类名再取指针
                fixed (char* className = ClassName)
                {
                    var wndClass = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        style = 0,
                        lpfnWndProc = WndProcDelegate,
                        hInstance = (HINSTANCE)ModuleHandle.DangerousGetHandle(),
                        // 用系统色索引 +1 作为背景画刷：当外部窗口比容器小时（居中留白），
                        // 这块区域才有确定的底色而不是未初始化的显存内容。
                        hbrBackground = (HBRUSH)(nint)(COLOR_BTNFACE + 1),
                        lpszClassName = new PCWSTR(className),
                    };

                    _classAtom = PInvoke.RegisterClassEx(in wndClass);
                }

                return _classAtom;
            }
        }

        private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
            => PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}

/*
 不写 UnregisterClass 不是疏忽，是这个类的生命周期设计上就不该回收
 1. Windows 会替你回收

窗口类的生命周期绑定在注册它的模块上：模块卸载时，系统自动注销该模块注册的所有类。这个类的注册模块是 GetModuleHandle(null) 拿到的 EXE 自身（ContainerWindow.cs:123）——EXE 模块在进程结束前不可能卸载。所以：

- 类的回收点 = 进程退出；
- 进程退出时系统自动清理，不需要你调用 UnregisterClass。

2. 显式调用反而有害

- EnsureClassRegistered 是带锁 + _classAtom != 0 守卫的进程一次注册。类一旦注销，第二个 ContainerWindow 创建时 CreateWindowEx 拿着已失效的类名直接失败——自己砸自己的脚。
- UnregisterClass 还有一个前置条件：该类名下不能有存活的窗口。也就是说只能在"最后一个容器窗口销毁后、且以后不再创建"这个时间窗口里安全调用——这个窗口类恰恰是"以后还要建"的，调用时机本身就是制造风险。
 */