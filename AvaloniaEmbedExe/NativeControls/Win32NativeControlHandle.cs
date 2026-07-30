using Avalonia.Controls.Platform;
using Avalonia.Platform;
using System;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class Win32NativeControlHandle : PlatformHandle, INativeControlHostDestroyableControlHandle
	{
		public Win32NativeControlHandle(IntPtr handle, string descriptor) 
			: base(handle, descriptor)
		{
		}
		public void Destroy()
		{
			_ = InteropUtil.DestroyWindow(Handle);
		}
	}
}
