using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AvaloniaEmbedExe.Controls;

/// <summary>
/// 在 Avalonia 视觉树中嵌入一个外部程序的主窗口。
/// </summary>
/// <remarks>
/// 结构上分三层：Avalonia 的 holder 窗口 → <see cref="ContainerWindow"/> → 外部程序窗口。
/// 中间那层容器是必需的，原因见 <see cref="ContainerWindow"/> 的注释。
/// </remarks>
public class ExternalAppHost : NativeControlHost
{
    /// <summary>
    /// 发出 WM_CLOSE 后等待进程自行退出的时间上限。
    /// 超时即强杀。刻意取小值：这段等待发生在 UI 线程上，
    /// 而 <see cref="ChildProcessJob"/> 已经保证了最终一定会被回收。
    /// </summary>
    private const int GracefulExitBudgetMs = 250;

    /// <summary>
    /// 判定外部窗口尺寸"已稳定"所需的连续相同采样次数。
    /// 与 <see cref="GeometryPollIntervalMs"/> 一起决定稳定窗口的长度：名义上 4×16≈48ms，
    /// 受系统计时器精度影响实测约 50~90ms。必须长于窗口创建初期 CW_USEDEFAULT 瞬态的
    /// 持续时间（calc1.exe 实测约 29ms）。见 <see cref="WaitForStableClientSizeAsync"/>。
    /// </summary>
    private const int GeometryStableSamples = 4;

    /// <summary>尺寸稳定性采样间隔。约一帧，足够密而不至于空转。</summary>
    private const int GeometryPollIntervalMs = 16;

    /// <summary>
    /// 等待尺寸稳定的时间上限。超时就按当前值继续 ——
    /// 尺寸后续还有 WinEventHook 兜着，不值得为它无限期等下去。
    /// </summary>
    private const int GeometrySettleBudgetMs = 500;

    private ContainerWindow? _container;
    private Process? _process;
    private IntPtr _embeddedHwnd;
    private CancellationTokenSource? _launchCts;
    private bool _tornDown;

    // ---- 尺寸同步（WinEventHook + 防抖）----
    private IntPtr _winEventHookHandle;
    private InteropUtil.WinEventProc? _winEventProcDelegate; // 防止 GC 回收回调存根
    private (int width, int height) _naturalSizePx;
    private DispatcherTimer? _debounceTimer;
    private (int width, int height) _pendingSizePx;

    // ---- 关闭钩子 ----
    private IClassicDesktopStyleApplicationLifetime? _subscribedLifetime;

    public static readonly StyledProperty<string> ExePathProperty =
        AvaloniaProperty.Register<ExternalAppHost, string>(
            nameof(ExePath),
            defaultValue: Path.Combine(AppContext.BaseDirectory, "Calc", "calc1.exe"));

    /// <summary>
    /// 要启动的外部程序路径。默认使用项目自带的经典 Win32 计算器 (Calc/calc1.exe)。
    /// </summary>
    /// <remarks>修改仅对下一次控件挂载生效，不会热重启当前已嵌入的进程。</remarks>
    public string ExePath
    {
        get => GetValue(ExePathProperty);
        set => SetValue(ExePathProperty, value);
    }

    public static readonly StyledProperty<TimeSpan> LaunchTimeoutProperty =
        AvaloniaProperty.Register<ExternalAppHost, TimeSpan>(
            nameof(LaunchTimeout), defaultValue: TimeSpan.FromSeconds(15));

    /// <summary>等待外部程序主窗口出现的超时时间。</summary>
    public TimeSpan LaunchTimeout
    {
        get => GetValue(LaunchTimeoutProperty);
        set => SetValue(LaunchTimeoutProperty, value);
    }

    /// <summary>外部程序启动失败（路径无效、超时、窗口未出现等）时触发。</summary>
    public event EventHandler<ExternalAppHostErrorEventArgs>? LaunchFailed;

    /// <summary>外部程序自行退出（用户关闭或崩溃）时触发。</summary>
    public event EventHandler? ExternalAppExited;

    // ---- 生命周期 ----

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _tornDown = false;
        _container = new ContainerWindow(parent.Handle);

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounceTimer.Tick += (_, _) => ApplyPendingSize();

        // 立刻返回容器句柄，绝不在这里等外部程序 —— 本方法由 UpdateHost() 在
        // OnAttachedToVisualTree 中同步调用于 UI 线程，任何阻塞都是界面卡死。
        // 容器句柄在这里就地取出传给后台任务：嵌入过程需要它，而后台线程不该去读
        // 可能已被 Teardown 置空的 _container 字段。
        _launchCts = new CancellationTokenSource();
        _ = LaunchAsync(ExePath, _container.Handle, LaunchTimeout, _launchCts.Token);

        return _container;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 关键：不能只依赖 DestroyNativeControlCore 做清理。
        // NativeControlHost 在控件脱离视觉树时是用 Dispatcher.Post(..., Background) 延迟销毁的，
        // 而应用退出时 Dispatcher.ShutdownImpl() 会对队列里所有待执行操作调用 Abort() 而非执行它们
        // —— 于是销毁回调被丢弃，外部进程永久残留。
        // 因此在 lifetime 的 Exit 事件上再挂一次：它在窗口关闭之后、Dispatcher 关停之前同步触发。
        if (_subscribedLifetime == null &&
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _subscribedLifetime = lifetime;
            lifetime.Exit += OnApplicationExit;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeLifetime();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => Teardown();

    private void UnsubscribeLifetime()
    {
        if (_subscribedLifetime != null)
        {
            _subscribedLifetime.Exit -= OnApplicationExit;
            _subscribedLifetime = null;
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Teardown();
        base.DestroyNativeControlCore(control);
    }

    /// <summary>
    /// 幂等的清理逻辑。可能从 <see cref="DestroyNativeControlCore"/>（控件分离）
    /// 或 lifetime 的 Exit（应用退出）进入，两条路径都必须能安全重入。
    /// </summary>
    private void Teardown()
    {
        if (_tornDown)
            return;
        _tornDown = true;

        UnsubscribeLifetime();

        _launchCts?.Cancel();
        _launchCts?.Dispose();
        _launchCts = null;

        _debounceTimer?.Stop();
        _debounceTimer = null;

        if (_winEventHookHandle != IntPtr.Zero)
        {
            InteropUtil.UnhookWinEvent(_winEventHookHandle);
            _winEventHookHandle = IntPtr.Zero;
        }
        _winEventProcDelegate = null;

        StopExitWatch();
        CloseExternalProcess();

        _embeddedHwnd = IntPtr.Zero;
        _naturalSizePx = default;
        _pendingSizePx = default;

        // 容器由 Avalonia 通过 INativeControlHostDestroyableControlHandle.Destroy() 销毁
        // （base.DestroyNativeControlCore）。但走 Exit 路径时 Avalonia 不会再调，这里补上。
        _container?.Destroy();
        _container = null;
    }

    private void CloseExternalProcess()
    {
        Process? process = _process;
        _process = null;

        try
        {
            // 先给外部程序一个优雅退出的机会。
            // 注意：走应用退出路径时，顶层窗口已在 lifetime.Exit 之前被销毁，容器和外部窗口
            // 作为其子窗口会被 Windows 递归回收，此处 IsWindow 已为 false。外部程序此时收到的是
            // WM_DESTROY（标准的"窗口即将消失"通知），多数程序会据此自行退出，随后被下面的
            // WaitForExit 确认。控件单独分离（应用继续运行）时窗口仍然存活，WM_CLOSE 正常送达。
            if (_embeddedHwnd != IntPtr.Zero && InteropUtil.IsWindow(_embeddedHwnd))
                InteropUtil.PostMessage(_embeddedHwnd, InteropUtil.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            if (process == null)
                return;

            // 有界等待，不用 Thread.Sleep —— WaitForExit 会在进程退出时立即返回，
            // 而 Sleep 无论如何都要睡满。
            if (!process.WaitForExit(GracefulExitBudgetMs))
            {
                Debug.WriteLine($"[ExternalAppHost] PID {process.Id} 未在 {GracefulExitBudgetMs}ms 内退出，强制结束");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            // 进程可能已经退出 / 句柄已失效，属预期情况
            Debug.WriteLine($"[ExternalAppHost] 关闭外部进程时出现异常（通常无害）: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    // ---- 启动 ----

    private async Task LaunchAsync(string exePath, IntPtr containerHandle, TimeSpan timeout, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            if (string.IsNullOrWhiteSpace(exePath))
                throw new InvalidOperationException($"{nameof(ExePath)} 未设置。");

            // 提前显式检查，比让 Process.Start 抛 Win32Exception 更容易定位
            if (!File.Exists(exePath))
                throw new FileNotFoundException($"找不到要嵌入的程序: {exePath}", exePath);

            HashSet<IntPtr> windowsBeforeLaunch = GetTopLevelWindowHandles();

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    // 不设 WindowStyle。ProcessWindowStyle.Hidden 拦不住 calc1.exe，
                    // UseShellExecute 取 true 或 false 都一样 —— 实测三种组合下
                    // IsWindowVisible 都在启动后 215~242ms 变成 true：
                    //   UseShellExecute=true  + Hidden -> 窗口 55ms 创建，242ms 显示
                    //   UseShellExecute=false + Hidden -> 29ms 创建，229ms 显示
                    //   UseShellExecute=false + 不设    -> 15ms 创建，215ms 显示
                    // STARTUPINFO.wShowWindow 只对程序的**第一次** ShowWindow 生效，
                    // 而 calc1.exe 会再调一次，第二次就按它自己的参数显示了。
                    // 真正消除闪现靠的是"尽早 SetParent"（见 EmbedIntoContainerAsync），
                    // 不是启动参数。
                    UseShellExecute = false,
                },

            };

            process.Start();

            // 尽早登记进 Job：这样即使后续步骤失败、甚至本进程被强杀，
            // 内核也会回收这个子进程。
            ChildProcessJob.TryRegister(process);

            IntPtr hwnd = await WaitForMainWindowAsync(process, windowsBeforeLaunch, timeout, ct)
                .ConfigureAwait(false);

            if (hwnd == IntPtr.Zero)
                throw new TimeoutException(
                    $"在 {timeout.TotalSeconds:0.#} 秒内未能获取外部程序的窗口句柄 " +
                    $"(进程: {exePath}, PID: {process.Id})。程序可能启动失败或未创建窗口。");

// 就地完成嵌入，绝不为此跳回 UI 线程 —— 详见 EmbedIntoContainerAsync。
            (int width, int height) naturalSizePx =
                await EmbedIntoContainerAsync(hwnd, containerHandle, ct).ConfigureAwait(false);

            Process started = process;
            process = null; // 所有权移交给 UI 线程

            await Dispatcher.UIThread.InvokeAsync(() => AttachWindow(started, hwnd, naturalSizePx));

}
        catch (OperationCanceledException)
        {
            // 控件在启动过程中被销毁，静默收尾
            KillQuietly(process);
        }
        catch (Exception ex)
        {
            KillQuietly(process);
            Debug.WriteLine($"[ExternalAppHost] 启动失败: {ex}");

            if (!ct.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    LaunchFailed?.Invoke(this, new ExternalAppHostErrorEventArgs(ex)));
            }
        }
    }

    private static void KillQuietly(Process? process)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* 已退出或无权限，忽略 */ }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// 把外部窗口挂进容器并调整到正确尺寸。**整个过程都在后台线程上就地完成。**
    /// </summary>
    /// <returns>嵌入后窗口的自然尺寸（物理像素），即控件应该占据的大小。</returns>
    /// <remarks>
    /// <para><b>为什么必须在后台线程做，而不是跳回 UI 线程。</b></para>
    /// <para>
    /// 这几个 Win32 调用本身都不要求 UI 线程，而"跳回 UI 线程再做"会致命地迟到：
    /// 应用启动阶段 UI 线程正忙于首帧渲染，实测 <c>Dispatcher.UIThread.InvokeAsync</c>
    /// 的回调要等到<b>启动后约 280ms</b> 才被执行（而句柄早在 65ms 就拿到了）。
    /// calc1.exe 自己在约 <b>215ms</b> 就调 ShowWindow 把窗口显示出来 ——
    /// 于是窗口先在桌面上露一下脸，之后才被 SetParent 收进来，这就是"闪现"。
    /// </para>
    /// <para>
    /// 实测在后台线程直接调用时，SetParent 不会被卡住的 UI 线程阻塞：
    /// 隐藏 + SetParent 在 <b>73ms</b> 就全部返回，比 215ms 的自显示早了 140ms，闪现彻底消失。
    /// </para>
    /// </remarks>
    private static async Task<(int width, int height)> EmbedIntoContainerAsync(
        IntPtr hwnd, IntPtr containerHandle, CancellationToken ct)
    {
        // 1. 先隐藏。若窗口已经显示出来了（找得晚），这一步立刻把它从桌面上摘掉。
        InteropUtil.ShowWindow(hwnd, InteropUtil.SW_HIDE);

        // 2. 立刻 SetParent —— 消除闪现的关键一步，越早越好。
        //    此刻窗口还带着标题栏，但它是隐藏的，而且已经进了容器的层级：
        //    后面无论花多久整形，都不会再有任何东西出现在桌面上。
        if (InteropUtil.SetParent(hwnd, containerHandle) == IntPtr.Zero)
            Debug.WriteLine($"[ExternalAppHost] SetParent 失败, " +
                            $"Win32Error={Marshal.GetLastWin32Error()}");

        // 3. 等尺寸稳定后再量"客户区应有的尺寸"。此时窗口尚未被改样式，量到的就是
        //    程序自己排版用的客户区大小。
        (int width, int height) desiredClient =
            await WaitForStableClientSizeAsync(hwnd, ct).ConfigureAwait(false);

        // 4. 摘掉装饰，再触发 frame 重算。重算必须在 SetParent 之后，
        //    否则系统会按顶层窗口来算非客户区。
        PrepareWindowStylesForEmbedding(hwnd);
        ApplyFrameChanged(hwnd);

        // 5. 把窗口调整到"客户区正好等于 desiredClient"的大小。
        //
        //    这一步不能省。ApplyFrameChanged 带的是 SWP_NOSIZE：窗口矩形保持不变，
        //    让出来的装饰空间全部变成客户区。实测 calc1.exe：窗口 228x323 /
        //    客户区 212x264，摘掉装饰后窗口仍是 228x323，而客户区涨到了 228x303。
        //    程序的内容只按 212x264 排版，于是右边多出 16px、下边多出 39px 的死白边
        //    —— 这就是"嵌入窗口大小设置得不对"。
        //
        //    重算后还剩下的非客户区（calc1.exe 是 20px 高的菜单条）要加回去，
        //    客户区才能恰好落在 desiredClient 上：212x264 + 0x20 = 212x284。
        //    （这个 212x284 也正是把窗口交给 calc1.exe 自己调整时它最终收敛到的尺寸，
        //      两条路算出同一个数，可以互相印证。）
        var (nonClientWidth, nonClientHeight) = GetNonClientOverheadPx(hwnd);
        int targetWidth = desiredClient.width + nonClientWidth;
        int targetHeight = desiredClient.height + nonClientHeight;

        // 保持隐藏：这里只定尺寸。显示交给 UI 线程侧的 PositionEmbeddedWindow，
        // 或者外部程序自己的 ShowWindow —— 那时它已经在容器里了。
        InteropUtil.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight,
            InteropUtil.SWP_NOMOVE | InteropUtil.SWP_NOZORDER | InteropUtil.SWP_NOACTIVATE);

        return GetWindowSizePx(hwnd);
    }

    /// <summary>
    /// 轮询等待窗口客户区尺寸稳定，返回稳定后的值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 拿到句柄不能立刻量尺寸。程序的主窗口通常先按 <c>CW_USEDEFAULT</c> 创建，之后才
    /// 调整到自己真正的大小 —— 实测 calc1.exe 的 CalcFrame 在启动后 32ms 时是
    /// <b>1440x753</b>，直到 44ms 才变成真正的 <b>228x323</b>。在这个空档里量到的尺寸
    /// 会把控件撑到上千像素宽。
    /// </para>
    /// <para>
    /// 判据取"连续 <see cref="GeometryStableSamples"/> 次采样不变"而不是"等固定时长"：
    /// 前者对启动快慢不敏感。样本数刻意凑到约 48ms 的稳定窗口 —— 比 1440x753 这个瞬态
    /// 本身（实测约 29ms）更长，才不会把瞬态误判成已经稳定。
    /// </para>
    /// <para>
    /// 这段等待是安全的：调用方已经先 SetParent 了，窗口此刻在容器里且隐藏着，
    /// 等多久都不会有东西闪到桌面上。
    /// </para>
    /// </remarks>
    private static async Task<(int width, int height)> WaitForStableClientSizeAsync(
        IntPtr hwnd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        (int width, int height) last = GetClientSizePx(hwnd);
        int stableCount = 1;

        while (stableCount < GeometryStableSamples && sw.ElapsedMilliseconds < GeometrySettleBudgetMs)
        {
            await Task.Delay(GeometryPollIntervalMs, ct).ConfigureAwait(false);

            (int width, int height) current = GetClientSizePx(hwnd);
            stableCount = current == last ? stableCount + 1 : 1;
            last = current;
        }

        if (stableCount < GeometryStableSamples)
            Debug.WriteLine($"[ExternalAppHost] 客户区尺寸在 {GeometrySettleBudgetMs}ms 内未稳定，" +
                            $"按当前值 {last.width}x{last.height} 继续");

        // 窗口在等待期间被销毁时 GetClientRect 会失败返回 (0,0)。
        // 退回窗口矩形，至少不会算出一个 0 尺寸的控件。
        if (last.width <= 0 || last.height <= 0)
        {
            last = GetWindowSizePx(hwnd);
            Debug.WriteLine($"[ExternalAppHost] 客户区尺寸不可用，退回窗口矩形 {last.width}x{last.height}");
        }

        return last;
    }

    /// <summary>当前样式下，窗口矩形比客户区大出来的部分（物理像素）。</summary>
    private static (int width, int height) GetNonClientOverheadPx(IntPtr hwnd)
    {
        var (windowWidth, windowHeight) = GetWindowSizePx(hwnd);
        var (clientWidth, clientHeight) = GetClientSizePx(hwnd);

        return (Math.Max(0, windowWidth - clientWidth), Math.Max(0, windowHeight - clientHeight));
    }

    /// <summary>
    /// 在 UI 线程上完成嵌入的记账工作 —— 窗口的整形已经由后台线程做完了。
    /// </summary>
    private void AttachWindow(Process process, IntPtr hwnd, (int width, int height) naturalSizePx)
    {
        // 等待期间控件可能已被销毁
        if (_tornDown || _container == null || !InteropUtil.IsWindow(hwnd))
        {
            KillQuietly(process);
            return;
        }

        _process = process;
        _embeddedHwnd = hwnd;
        _naturalSizePx = naturalSizePx;

        InstallWinEventHook(hwnd);
        StartExitWatch(process);

        Debug.WriteLine($"[ExternalAppHost] 已嵌入 HWND={hwnd}, PID={process.Id}, " +
                        $"自然尺寸={_naturalSizePx.width}x{_naturalSizePx.height}px");

        InvalidateMeasure();
        PositionEmbeddedWindow(Bounds.Size);
    }

    /// <summary>
    /// 调整窗口样式使其适合嵌入：去掉标题栏、系统按钮和可调边框。
    /// 注意：此方法只修改样式，不触发 frame 重算。调用方必须在 SetParent 之后
    /// 另行调用 <see cref="ApplyFrameChanged"/> 让非客户区重新计算。
    /// </summary>
    /// <remarks>
    /// <para><b>刻意不加 WS_CHILD。</b></para>
    /// <para>
    /// 直觉上"被 SetParent 的窗口应该是 WS_CHILD"，但 Win32 明确规定：
    /// <i>"Only an overlapped or pop-up window can contain a menu bar; a child window cannot
    /// contain one."</i>（<c>menurc/about-menus</c>）。一旦置上 WS_CHILD，被嵌入程序的菜单条
    /// 就永远不会绘制 —— 对 calc1.exe 这类靠菜单提供全部功能（查看/编辑/帮助）的程序，
    /// 等于把功能砍掉了。
    /// </para>
    /// <para>
    /// 因此这里保留窗口原本的 overlapped/popup 身份，只摘掉装饰。SetParent 之后窗口依然
    /// 会被裁剪到容器内、随容器移动，菜单条则正常保留。
    /// </para>
    /// </remarks>
    private static void PrepareWindowStylesForEmbedding(IntPtr hwnd)
    {
        uint style = InteropUtil.GetWindowLong(hwnd, InteropUtil.GWL_STYLE);

        // 去掉标题栏（WS_CAPTION）、系统菜单/关闭按钮（WS_SYSMENU）、
        // 最小化/最大化按钮、可调边框（WS_THICKFRAME）。
        style &= ~(InteropUtil.WS_CAPTION | InteropUtil.WS_SYSMENU |
                   InteropUtil.WS_THICKFRAME |
                   InteropUtil.WS_MINIMIZEBOX | InteropUtil.WS_MAXIMIZEBOX);

        InteropUtil.SetWindowLong(hwnd, InteropUtil.GWL_STYLE, style);
    }

    /// <summary>
    /// 触发 SWP_FRAMECHANGED，让系统根据窗口当前的样式和层级（父窗口）重新计算非客户区。
    /// 必须在 SetParent 之后调用，否则系统会按顶层窗口来计算 frame。
    /// </summary>
    private static void ApplyFrameChanged(IntPtr hwnd)
    {
        InteropUtil.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            InteropUtil.SWP_NOMOVE | InteropUtil.SWP_NOSIZE | InteropUtil.SWP_NOZORDER |
            InteropUtil.SWP_NOACTIVATE | InteropUtil.SWP_FRAMECHANGED);
    }

    // ---- 布局 ----

    /// <summary>
    /// 物理像素与 DIP 的换算比例。
    /// 刻意取 <see cref="TopLevel.RenderScaling"/>：Avalonia 的 ShowInBounds 用的正是
    /// <c>_attachedTo.Window.RenderScaling</c>，两边必须是同一个值，否则容器与内嵌窗口的尺寸会错位。
    /// </summary>
    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (w, h) = _naturalSizePx;
        if (w <= 0 || h <= 0)
            return base.MeasureOverride(availableSize);

        // GetWindowRect 返回物理像素，而 Avalonia 的布局单位是 DIP。
        // 原实现把像素值直接当 DIP 用，在非 100% 缩放下会与 Avalonia 的
        // ShowInBounds(bounds * RenderScaling) 叠乘，形成指数级放大的自激回路。
        double scale = RenderScaling;
        return new Size(w / scale, h / scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 调用 base 是安全的：NativeControlHost 并未重写 ArrangeOverride，
        // 这里落到 Layoutable.ArrangeOverride，对无子元素的控件是空操作。
        // （真正拉伸原生窗口的是 Avalonia 的 ShowInBounds → MoveWindow，
        //   它作用于我们返回的容器窗口，不会碰到外部程序窗口。）
        var result = base.ArrangeOverride(finalSize);
        PositionEmbeddedWindow(finalSize);
        return result;
    }

    /// <summary>
    /// 在容器内把外部窗口按其自然尺寸居中 —— <b>只移动，不改尺寸</b>。
    /// 因为容器是我们自己的窗口，这里的定位不会被 Avalonia 覆盖。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须带 SWP_NOSIZE。</b></para>
    /// <para>
    /// 尺寸的所有权在外部程序那一侧，数据流是单向的：外部程序改尺寸 →
    /// <see cref="OnWinEvent"/> 观察到 → 更新 <see cref="_naturalSizePx"/> → 控件重新布局。
    /// 一旦这里也写尺寸，这条流就成了环 —— 我们写进去的尺寸同样会触发
    /// <c>EVENT_OBJECT_LOCATIONCHANGE</c>，再被当成"外部程序改了尺寸"读回来。
    /// </para>
    /// <para>
    /// 实测这个环会发散。曾经把 <c>nw/nh</c> 一并写进 SetWindowPos，而 calc1.exe 想要的是
    /// 212x285、我们算出来的是 212x284 —— 差这 1px 就足以让双方一直互相追着改：
    /// 212x284 → 212x285 → 212x284 → …，三次启动里有一次直接跑飞到 222x308 停住不动了。
    /// 改成只移动之后尺寸单向收敛，抖动消失。
    /// </para>
    /// <para>
    /// 代价是启动瞬间可能有一小段"窗口比控件量到的尺寸大"的过渡期
    /// （calc1.exe 自己 ShowWindow 时会把尺寸恢复成它记住的旧值），
    /// 由防抖定时器在 100ms 内收敛掉。比永久停在错误尺寸上划算得多。
    /// </para>
    /// </remarks>
    private void PositionEmbeddedWindow(Size finalSize)
    {
        if (_embeddedHwnd == IntPtr.Zero || !InteropUtil.IsWindow(_embeddedHwnd))
            return;

        var (nw, nh) = _naturalSizePx;
        if (nw <= 0 || nh <= 0)
            return;

        // finalSize 是 DIP，容器的实际像素尺寸是 finalSize * scale
        double scale = RenderScaling;
        int containerW = (int)Math.Round(finalSize.Width * scale);
        int containerH = (int)Math.Round(finalSize.Height * scale);

        // 容器还没有真正的尺寸就别动手。
        // AttachWindow 里调用本方法时，InvalidateMeasure() 只是把布局标脏，Bounds 仍是
        // 上一轮的旧值 —— 实测首帧是 84x0。用它算出来的居中偏移是错的，而 SWP_SHOWWINDOW
        // 又会把窗口就地显示出来，等于在应用内先摆错一次再纠正（可见的跳动）。
        // 直接跳过：自然尺寸一变必然触发新的布局，ArrangeOverride 会带着正确的 finalSize 再来一次。
        if (containerW <= 0 || containerH <= 0)
            return;

        int offsetX = Math.Max(0, (containerW - nw) / 2);
        int offsetY = Math.Max(0, (containerH - nh) / 2);

        // 尺寸不能在这里写回去 —— 必须带 SWP_NOSIZE，理由见方法注释。
        InteropUtil.SetWindowPos(_embeddedHwnd, IntPtr.Zero,
            offsetX, offsetY, 0, 0,
            InteropUtil.SWP_NOSIZE | InteropUtil.SWP_NOZORDER |
            InteropUtil.SWP_NOACTIVATE | InteropUtil.SWP_SHOWWINDOW);
    }

    // ---- 外部窗口尺寸变化监听 ----

    private void InstallWinEventHook(IntPtr hwnd)
    {
        _winEventProcDelegate = OnWinEvent;

        uint threadId = InteropUtil.GetWindowThreadProcessId(hwnd, out uint processId);

        _winEventHookHandle = InteropUtil.SetWinEventHook(
            InteropUtil.EVENT_OBJECT_LOCATIONCHANGE,
            InteropUtil.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventProcDelegate,
            processId,   // 只监听嵌入应用的进程
            threadId,    // 只监听嵌入窗口的线程
            InteropUtil.WINEVENT_OUTOFCONTEXT);

        if (_winEventHookHandle == IntPtr.Zero)
            Debug.WriteLine("[ExternalAppHost] SetWinEventHook 失败，将无法跟随外部窗口的尺寸变化");
    }

    /// <summary>
    /// 外部窗口位置/尺寸变化的回调。WINEVENT_OUTOFCONTEXT 保证它在
    /// 调用 SetWinEventHook 的线程（即 UI 线程）上派发，因此可以直接操作控件状态。
    /// </summary>
    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd != _embeddedHwnd ||
            idObject != InteropUtil.OBJID_WINDOW ||
            idChild != InteropUtil.CHILDID_SELF ||
            eventType != InteropUtil.EVENT_OBJECT_LOCATIONCHANGE)
            return;

        var currentPx = GetWindowSizePx(hwnd);
        if (currentPx.width <= 0 || currentPx.height <= 0)
            return;

        // 只比较尺寸，不比较位置。这里之所以不需要"抑制自己触发的事件"的标记，
        // 完全依赖一条不变量：<see cref="PositionEmbeddedWindow"/> 只改位置、绝不改尺寸
        // （它带着 SWP_NOSIZE）。破坏那条不变量，这里立刻就会形成自激回路 —— 别去动它。
        if (currentPx != _naturalSizePx)
        {
            _pendingSizePx = currentPx;
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
    }

    /// <summary>防抖回调：100ms 内无新事件才应用尺寸更新。</summary>
    private void ApplyPendingSize()
    {
        _debounceTimer?.Stop();

        if (_pendingSizePx == _naturalSizePx || _pendingSizePx.width <= 0 || _pendingSizePx.height <= 0)
            return;

        _naturalSizePx = _pendingSizePx;
        InvalidateMeasure();
        Debug.WriteLine($"[ExternalAppHost] 外部窗口自然尺寸更新为 {_naturalSizePx.width}x{_naturalSizePx.height}px");
    }

    private static (int width, int height) GetWindowSizePx(IntPtr hwnd)
        => InteropUtil.GetWindowRect(hwnd, out var rect)
            ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0);

    private static (int width, int height) GetClientSizePx(IntPtr hwnd)
        => InteropUtil.GetClientRect(hwnd, out var rect)
            ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0);

    // ---- 外部进程退出监听 ----

    private void StartExitWatch(Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnExternalProcessExited;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExternalAppHost] 无法监听进程退出: {ex.Message}");
        }
    }

    private void StopExitWatch()
    {
        try
        {
            if (_process != null)
                _process.Exited -= OnExternalProcessExited;
        }
        catch { /* 句柄可能已失效 */ }
    }

    private void OnExternalProcessExited(object? sender, EventArgs e)
    {
        // Process.Exited 在线程池线程上触发。若此时 Dispatcher 已关停，Post 会抛异常，
        // 而线程池线程上的未处理异常会直接终结进程 —— 必须兜住。
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_tornDown)
                    return;

                Debug.WriteLine("[ExternalAppHost] 外部程序已自行退出");
                _embeddedHwnd = IntPtr.Zero;
                _naturalSizePx = default;
                InvalidateMeasure();
                ExternalAppExited?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExternalAppHost] 派发进程退出通知失败（通常是应用正在关闭）: {ex.Message}");
        }
    }

    // ---- 窗口句柄查找 ----

    /// <summary>
    /// 轮询等待外部程序的主框架窗口出现。
    /// </summary>
    /// <remarks>
    /// <para><b>查找顺序刻意是"先严格判据、后 MainWindowHandle"。</b></para>
    /// <para>
    /// <see cref="FindWindowByProcess"/> 不依赖可见性，能在窗口"已创建但程序还没 ShowWindow"
    /// 的空档里命中（实测 calc1.exe 这个空档是启动后 64ms→240ms）。越早拿到句柄，
    /// 留给桌面闪现的窗口就越窄。
    /// </para>
    /// <para>
    /// <see cref="Process.MainWindowHandle"/> 的判据是"无 owner 且 <c>IsWindowVisible</c>"，
    /// 只有窗口显示出来之后才非零 —— 用它当主路径就必然要等到窗口已经出现在桌面上。
    /// 因此把它放在兜底位置：只有当严格判据认不出主窗口（例如自绘标题栏、无 WS_SYSMENU
    /// 的程序）时才用。
    /// </para>
    /// <para>
    /// <b>越早拿到句柄，闪现的窗口期就越窄。</b>实测 calc1.exe 在启动后约 215ms 才自己
    /// ShowWindow，而这里 15~65ms 就能命中 —— 拿到句柄后由
    /// <see cref="EmbedIntoContainerAsync"/> 就地（后台线程上）完成 SetParent，
    /// 赶在自显示之前把窗口收进容器，闪现就此消失。
    /// 关键是别为了 SetParent 跳回 UI 线程：启动阶段那一跳要等到约 280ms。
    /// </para>
    /// </remarks>
    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process, HashSet<IntPtr> windowsBeforeLaunch, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
                throw new InvalidOperationException(
                    $"外部程序在创建窗口前就退出了 (PID: {process.Id}, 退出码: {process.ExitCode})。");

            IntPtr found = FindWindowByProcess(process, windowsBeforeLaunch);
            if (found != IntPtr.Zero)
            {
                // 窗口此刻很可能还没显示；先隐藏是为了应对"已经显示出来了"的情况，
                // 让它在 SetParent 完成前从桌面上消失。
                InteropUtil.ShowWindow(found, InteropUtil.SW_HIDE);
                return found;
            }

            try
            {
                process.Refresh();
                IntPtr main = process.MainWindowHandle;
                if (main != IntPtr.Zero)
                {
                    InteropUtil.ShowWindow(main, InteropUtil.SW_HIDE);
                    return main;
                }
            }
            catch { /* 进程状态尚未稳定，继续轮询 */ }

            // 间隔取 25ms：要在上面那个"已创建、未显示"的空档内命中，轮询必须比它密。
            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 按 PID 查找属于目标进程的主框架窗口（可以是尚未显示的）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么筛选条件必须这么严。</b>程序启动瞬间会冒出好几个无 owner 的顶层窗口，
    /// 实测 calc1.exe 在头 64ms 内就有三个：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>GDI+ Hook Window Class</c> —— 1x1 的 WS_POPUP 辅助窗口</item>
    ///   <item><c>Edit</c> —— 计算器的显示框，先以顶层窗口创建（CW_USEDEFAULT 尺寸 1440x753），
    ///         之后才被程序自己 SetParent 进主框架</item>
    ///   <item><c>CalcFrame</c> —— 真正的主窗口，228x323，带 3 项菜单条</item>
    /// </list>
    /// <para>
    /// 只按"PID 匹配 + 无 owner"挑，EnumWindows 会先撞上 <c>Edit</c> 并把它当成主窗口嵌进去 ——
    /// 于是界面里是一块 1440x753 的空白（还把 Avalonia 布局撑爆），真正的计算器仍留在桌面上。
    /// </para>
    /// <para>
    /// 判据取 <c>WS_CAPTION | WS_SYSMENU</c>：普通桌面程序的主窗口用 WS_OVERLAPPEDWINDOW 创建，
    /// 二者必然都有；而上面那两个干扰窗口都缺 WS_SYSMENU。这个判据不依赖可见性，
    /// 因此能在窗口还隐藏时就命中（见 <see cref="WaitForMainWindowAsync"/> 的说明）。
    /// 自绘标题栏（纯 WS_POPUP）的程序会漏判，由调用方的 MainWindowHandle 兜底。
    /// </para>
    /// </remarks>
    private static IntPtr FindWindowByProcess(Process process, HashSet<IntPtr> windowsBeforeLaunch)
    {
        uint targetPid;
        try
        {
            targetPid = (uint)process.Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        IntPtr foundHwnd = IntPtr.Zero;

        InteropUtil.EnumWindows((hwnd, _) =>
        {
            // 排除启动前就已存在的窗口
            if (windowsBeforeLaunch.Contains(hwnd))
                return true;

            // 只接受归属目标进程的窗口
            InteropUtil.GetWindowThreadProcessId(hwnd, out uint wndPid);
            if (wndPid != targetPid)
                return true;

            // 跳过有 owner 的窗口（工具窗、对话框、IME 窗口）
            if (InteropUtil.GetWindow(hwnd, InteropUtil.GW_OWNER) != IntPtr.Zero)
                return true;

            uint style = InteropUtil.GetWindowLong(hwnd, InteropUtil.GWL_STYLE);

            // 子窗口不可能是主框架
            if ((style & InteropUtil.WS_CHILD) != 0)
                return true;

            // 必须同时具备标题栏和系统菜单 —— 见方法注释
            if ((style & InteropUtil.WS_CAPTION) != InteropUtil.WS_CAPTION ||
                (style & InteropUtil.WS_SYSMENU) == 0)
                return true;

            foundHwnd = hwnd;
            return false;
        }, IntPtr.Zero);

        return foundHwnd;
    }

    /// <summary>
    /// 快照当前所有顶层窗口句柄，用于把启动前就存在的窗口排除在查找之外。
    /// </summary>
    /// <remarks>
    /// 刻意不按可见性过滤：<see cref="FindWindowByProcess"/> 会匹配尚未显示的窗口，
    /// 若这里只记录可见窗口，排除集就和匹配集不是同一个口径了。
    /// </remarks>
    private static HashSet<IntPtr> GetTopLevelWindowHandles()
    {
        var result = new HashSet<IntPtr>();
        InteropUtil.EnumWindows((hwnd, _) =>
        {
            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

/// <summary>携带外部程序启动失败原因。</summary>
public sealed class ExternalAppHostErrorEventArgs(Exception error) : EventArgs
{
    public Exception Error { get; } = error;
}
