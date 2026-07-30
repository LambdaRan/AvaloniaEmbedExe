using Avalonia.Controls.Platform;
using Avalonia.Platform;
using System;
using System.Windows;
using System.Windows.Forms.Integration;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class WpfNativeControlHandle : IPlatformHandle, INativeControlHostDestroyableControlHandle
	{
		private readonly ElementHost _ElementHost = new();
		public WpfNativeControlHandle(UIElement element, string descriptor)
		{
			_ElementHost.Child = element;
			Handle = _ElementHost.Handle;
			HandleDescriptor = descriptor;
		}
		public IntPtr Handle { get; }

		public string? HandleDescriptor { get; }

		public void Destroy()
		{
			_ = InteropUtil.DestroyWindow(Handle);
		}
	}
}
