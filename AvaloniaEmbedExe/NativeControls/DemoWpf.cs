using Avalonia.Platform;
using System;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class DemoWpf : IWinNativeControl
	{
		public DemoWpf() 
		{
			Wpf = new EmbedDemoWpf.DemoWpf();
		}
		public EmbedDemoWpf.DemoWpf Wpf { get; private set;  }
		public IPlatformHandle CreateControl(IPlatformHandle parent, Func<IPlatformHandle> createDefault)
		{
			return new WpfNativeControlHandle(Wpf, "HWND");
		}
	}
}
