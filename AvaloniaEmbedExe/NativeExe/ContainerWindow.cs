using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls.Platform;
using Avalonia.Platform;

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
        private static readonly InteropUtil.WndProcDelegate WndProcDelegate = WndProc;
        private static readonly object RegistrationGate = new();
        private static ushort _classAtom;

        private IntPtr _handle;

        public ContainerWindow(IntPtr parent)
        {
            ushort atom = EnsureClassRegistered();
            if (atom == 0)
                throw new InvalidOperationException(
                    $"注册容器窗口类失败, Win32Error={Marshal.GetLastWin32Error()}");

            _handle = InteropUtil.CreateWindowEx(
                dwExStyle: 0,
                lpClassName: atom,   // 类原子按 IntPtr 传递（低 16 位为原子值）
                lpWindowName: null,
                // WS_CLIPCHILDREN: 容器自身重绘时不覆盖子窗口区域，避免嵌入应用闪烁
                dwStyle: InteropUtil.WS_CHILD | InteropUtil.WS_VISIBLE | InteropUtil.WS_CLIPCHILDREN,
                x: 0, y: 0, nWidth: 1, nHeight: 1,
                hWndParent: parent,
                hMenu: IntPtr.Zero,
                hInstance: InteropUtil.GetModuleHandle(null),
                lpParam: IntPtr.Zero);

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"创建容器窗口失败, Win32Error={Marshal.GetLastWin32Error()}");
        }

        public IntPtr Handle => _handle;

        public string? HandleDescriptor => "HWND";

        /// <summary>容器当前的客户区尺寸（物理像素）。</summary>
        public (int width, int height) ClientSizePx =>
            InteropUtil.GetWindowRect(_handle, out var r)
                ? (r.Right - r.Left, r.Bottom - r.Top)
                : (0, 0);

        public void Destroy()
        {
            if (_handle == IntPtr.Zero)
                return;

            IntPtr handle = _handle;
            _handle = IntPtr.Zero;

            // 走应用退出路径时，顶层窗口可能已先被销毁，容器作为其子窗口会被 Windows
            // 一并回收 —— 此时句柄已失效，DestroyWindow 必然失败，无需当成错误上报。
            if (!InteropUtil.IsWindow(handle))
                return;

            if (!InteropUtil.DestroyWindow(handle))
                Debug.WriteLine($"[ContainerWindow] DestroyWindow 失败, Win32Error={Marshal.GetLastWin32Error()}");
        }

        private static ushort EnsureClassRegistered()
        {
            lock (RegistrationGate)
            {
                if (_classAtom != 0)
                    return _classAtom;

                var wndClass = new InteropUtil.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<InteropUtil.WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = WndProcDelegate,
                    hInstance = InteropUtil.GetModuleHandle(null),
                    // 用系统色索引 +1 作为背景画刷：当外部窗口比容器小时（居中留白），
                    // 这块区域才有确定的底色而不是未初始化的显存内容。
                    hbrBackground = COLOR_BTNFACE + 1,
                    lpszClassName = ClassName,
                };

                _classAtom = InteropUtil.RegisterClassEx(ref wndClass);
                return _classAtom;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            => InteropUtil.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
