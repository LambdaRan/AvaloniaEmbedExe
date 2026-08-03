using Avalonia.Platform;
using System;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class DemoWpf : IWinNativeControl
	{
		private EmbedDemoWpf.DemoWpf? _wpf;

		public DemoWpf()
		{
			_wpf = new EmbedDemoWpf.DemoWpf();
		}

		public EmbedDemoWpf.DemoWpf? Wpf => _wpf;

		public IPlatformHandle CreateControl(IPlatformHandle parent, Func<IPlatformHandle> createDefault)
		{
			if (_wpf == null)
				throw new ObjectDisposedException(nameof(DemoWpf));

			return new WpfNativeControlHandle(_wpf, "HWND");
		}

		public void Dispose()
		{
			// WPF UserControl 本身不是 IDisposable；真正的原生资源（ElementHost）
			// 由 WpfNativeControlHandle.Destroy() 释放。这里断开引用，让控件树可被回收。
			_wpf = null;
		}
	}
}
