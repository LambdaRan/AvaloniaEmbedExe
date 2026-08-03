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
			return _Control?.CreateControl(parent, () => base.CreateNativeControlCore(parent))
				?? base.CreateNativeControlCore(parent);
		}

		protected override void DestroyNativeControlCore(IPlatformHandle control)
		{
			// 先让 base 销毁句柄（会调用 INativeControlHostDestroyableControlHandle.Destroy()），
			// 再释放控件包装自身持有的托管资源。顺序不能反 —— Destroy() 依赖尚未释放的宿主对象。
			base.DestroyNativeControlCore(control);
			_Control?.Dispose();
		}
	}
}
