using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    /// 绝不主动 CloseHandle。
    /// </para>
    /// </remarks>
    internal static class ChildProcessJob
    {
        private static readonly object Gate = new();
        private static IntPtr _jobHandle;
        private static bool _initialized;

        /// <summary>
        /// 将进程登记进 Job。失败不抛异常 —— Job 只是兜底，
        /// 失败时优雅关闭路径仍然有效，不应因此让嵌入功能不可用。
        /// </summary>
        /// <returns>登记成功返回 true。</returns>
        public static bool TryRegister(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);

            IntPtr job = EnsureJob();
            if (job == IntPtr.Zero)
                return false;

            try
            {
                IntPtr processHandle = process.Handle;
                if (processHandle == IntPtr.Zero)
                    return false;

                if (InteropUtil.AssignProcessToJobObject(job, processHandle))
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

        private static IntPtr EnsureJob()
        {
            lock (Gate)
            {
                if (_initialized)
                    return _jobHandle;

                _initialized = true;

                IntPtr job = InteropUtil.CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    Debug.WriteLine($"[ChildProcessJob] CreateJobObject 失败, Win32Error={Marshal.GetLastWin32Error()}");
                    return IntPtr.Zero;
                }

                var info = new InteropUtil.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new InteropUtil.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = InteropUtil.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                int size = Marshal.SizeOf<InteropUtil.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                    if (!InteropUtil.SetInformationJobObject(
                            job, InteropUtil.JOBOBJECTINFOCLASS.ExtendedLimitInformation, buffer, (uint)size))
                    {
                        Debug.WriteLine($"[ChildProcessJob] SetInformationJobObject 失败, " +
                                        $"Win32Error={Marshal.GetLastWin32Error()}");
                        InteropUtil.CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                // 故意不关闭：句柄必须活到本进程结束，届时内核回收 Job 内所有进程。
                _jobHandle = job;
                Debug.WriteLine("[ChildProcessJob] Job Object 已创建 (KILL_ON_JOB_CLOSE)");
                return _jobHandle;
            }
        }
    }
}
