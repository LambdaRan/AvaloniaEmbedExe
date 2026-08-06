using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace AvaloniaEmbedExe.Controls
{
    /// <summary>
    /// 把子进程登记到一个设置了 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 的 Windows Job Object 中。
    /// </summary>
    /// <remarks>
    /// 这是**兜底**机制，不是主清理路径。
    /// <para>
    /// 主路径（<see cref="ExternalAppHost"/> 在窗口关闭 / 控件分离时发 WM_CLOSE）只能覆盖"宿主正常退出"。
    /// 而宿主一旦崩溃、被任务管理器强杀、或调试器中途 Stop，那条路径都不会执行 —— 子进程就永久残留。
    /// </para>
    /// <para>
    /// Job Object 把回收责任交给内核：本进程是该 Job 的唯一句柄持有者，进程一死（任何原因）
    /// 句柄随之关闭，内核立即终止 Job 内所有进程。因此 Job 句柄要故意"泄漏"到进程生命周期结束，
    /// 绝不主动 Dispose（SafeHandle 被 GC 终结时同样会关闭句柄，所以必须用静态字段钉住它）。
    /// </para>
    /// </remarks>
    internal static class ChildProcessJob
    {
        private static readonly object Gate = new();
        private static SafeFileHandle? _jobHandle;
        private static bool _initialized;

        /// <summary>
        /// 将进程登记进 Job。失败不抛异常 —— Job 只是兜底，
        /// 失败时优雅关闭路径仍然有效，不应因此让嵌入功能不可用。
        /// </summary>
        /// <returns>登记成功返回 true。</returns>
        public static bool TryRegister(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);

            SafeFileHandle? job = EnsureJob();
            if (job is null)
                return false;

            try
            {
                IntPtr processHandle = process.Handle;
                if (processHandle == IntPtr.Zero)
                    return false;

                // 只是把现有句柄借给这次调用，不拥有它，绝不能让 SafeHandle 去关闭它
                using var borrowedProcessHandle = new SafeFileHandle(processHandle, ownsHandle: false);
                if (PInvoke.AssignProcessToJobObject(job, borrowedProcessHandle))
                {
                    Debug.WriteLine($"[ChildProcessJob] PID {process.Id} 已登记进 Job，宿主退出时将由内核回收");
                    return true;
                }

                Debug.WriteLine($"[ChildProcessJob] AssignProcessToJobObject 失败, PID={process.Id}, " +
                                $"Win32Error={Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                // 进程可能在拿 Handle 之前就退出了
                Debug.WriteLine($"[ChildProcessJob] 登记 PID 失败: {ex.Message}");
            }

            return false;
        }

        private static SafeFileHandle? EnsureJob()
        {
            lock (Gate)
            {
                if (_initialized)
                    return _jobHandle;

                _initialized = true;

                SafeFileHandle job = PInvoke.CreateJobObject(null, null);
                if (job.IsInvalid)
                {
                    Debug.WriteLine($"[ChildProcessJob] CreateJobObject 失败, Win32Error={Marshal.GetLastWin32Error()}");
                    job.Dispose();
                    return null;
                }

                // KILL_ON_JOB_CLOSE 是基本限制里的 LimitFlags 位，basic 结构足够，
                // 无需 extended（extended 只为进程/Job 内存上限等扩展字段而存在）
                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                };

                // 友好重载直接收 ReadOnlySpan<byte>，省掉了原先 AllocHGlobal/StructureToPtr 的手工编排
                if (!PInvoke.SetInformationJobObject(
                        job,
                        JOBOBJECTINFOCLASS.JobObjectBasicLimitInformation,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref info, 1))))
                {
                    Debug.WriteLine($"[ChildProcessJob] SetInformationJobObject 失败, " +
                                    $"Win32Error={Marshal.GetLastWin32Error()}");
                    job.Dispose();
                    return null;
                }

                // 故意不 Dispose：句柄必须活到本进程结束，届时内核回收 Job 内所有进程。
                _jobHandle = job;
                Debug.WriteLine("[ChildProcessJob] Job Object 已创建 (KILL_ON_JOB_CLOSE)");
                return _jobHandle;
            }
        }
    }
}
