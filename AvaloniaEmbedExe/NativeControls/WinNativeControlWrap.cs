using Avalonia.Controls;
using Avalonia.Platform;

namespace AvaloniaEmbedExe.NativeControls
{
	internal sealed class WinNativeControlWrap : NativeControlHost
	{
		private readonly IWinNativeControl _Control;
		public WinNativeControlWrap(IWinNativeControl control)
		{
			_Control = control;
		}
		protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
		{
			return _Control?.CreateControl(parent, () => base.CreateNativeControlCore(parent)) ?? base.CreateNativeControlCore(parent);
		}
		protected override void DestroyNativeControlCore(IPlatformHandle control)
		{
			base.DestroyNativeControlCore(control);
		}
	}
}
