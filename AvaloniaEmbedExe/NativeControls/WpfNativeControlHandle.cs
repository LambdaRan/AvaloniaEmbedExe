using Avalonia.Controls.Platform;
using Avalonia.Platform;
using System;
using System.Windows;
using System.Windows.Forms.Integration;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class WpfNativeControlHandle : IPlatformHandle, INativeControlHostDestroyableControlHandle
	{
		private ElementHost? _ElementHost;

		public WpfNativeControlHandle(UIElement element, string descriptor)
		{
			var host = new ElementHost { Child = element };
			_ElementHost = host;
			Handle = host.Handle;
			HandleDescriptor = descriptor;
		}

		public IntPtr Handle { get; }

		public string? HandleDescriptor { get; }

		public void Destroy()
		{
			// ElementHost 是 IDisposable 的 WinForms 控件，它同时持有 WPF 内容和
			// 一个 Dispatcher 引用。原实现直接 DestroyWindow(Handle) 把窗口拆了，
			// 但 ElementHost 自身的托管状态从未释放 —— 每次销毁都泄漏。
			// Dispose 会走 WinForms 正常的销毁流程（内部会销毁窗口句柄）。
			var host = _ElementHost;
			_ElementHost = null;
			host?.Dispose();
		}
	}
}
