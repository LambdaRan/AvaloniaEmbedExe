using Avalonia.Platform;
using System;

namespace AvaloniaEmbedExe.NativeControls
{
	internal interface IWinNativeControl
	{
		/// <param name="parent"></param>
		/// <param name="createDefault"></param>
		IPlatformHandle CreateControl(IPlatformHandle parent, Func<IPlatformHandle> createDefault);
	}
}
