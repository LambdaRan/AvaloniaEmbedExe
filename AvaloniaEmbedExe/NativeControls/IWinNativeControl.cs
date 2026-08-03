using Avalonia.Platform;
using System;

namespace AvaloniaEmbedExe.NativeControls
{
	/// <summary>
	/// 可被 <see cref="WinNativeControlWrap"/> 承载的原生控件。
	/// </summary>
	/// <remarks>
	/// 继承 <see cref="IDisposable"/>：实现通常持有原生/互操作资源
	/// （如 ElementHost、WPF 控件树），需要在控件销毁时确定性释放。
	/// </remarks>
	internal interface IWinNativeControl : IDisposable
	{
		/// <param name="parent">Avalonia 提供的宿主窗口句柄。</param>
		/// <param name="createDefault">回退用：创建 Avalonia 的默认子窗口。</param>
		IPlatformHandle CreateControl(IPlatformHandle parent, Func<IPlatformHandle> createDefault);
	}
}
