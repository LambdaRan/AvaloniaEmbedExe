using Avalonia;
using System;

namespace AvaloniaEmbedExe
{
	internal sealed class Program
	{
		// Initialization code. Don't use any Avalonia, third-party APIs or any
		// SynchronizationContext-reliant code before AppMain is called: things aren't initialized
		// yet and stuff might break.
		[STAThread]
		public static void Main(string[] args) => BuildAvaloniaApp()
			.StartWithClassicDesktopLifetime(args);

		// Avalonia configuration, don't remove; also used by visual designer.
		// Win32PlatformOptions：缓解"拖动缩放窗口时内嵌原生界面抖动/白边闪烁"。
		// 默认配置（WinUIComposition/DirectComposition）下 Avalonia 在后台线程按 vsync
		// 渲染，而内嵌的原生 HWND 在 WM_SIZE 中被同步移动，两条时钟不同步 → 拖影；
		// 且窗口带 WS_EX_NOREDIRECTIONBITMAP，resize 空档 DWM 无旧帧可拉伸 → 闪底。
		// RedirectionSurface + ShouldRenderOnUIThread 是官方为 WPF 这类必须同线程渲染
		// 的互操作场景留的组合（见 Win32PlatformOptions.ShouldRenderOnUIThread 注释）。
		// 代价：失去亚克力/透明能力。若验证后不需要，删掉 .With(...) 即可还原。
		public static AppBuilder BuildAvaloniaApp()
			=> AppBuilder.Configure<App>()
				//.With(new Win32PlatformOptions
				//{
				//	CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
				//	ShouldRenderOnUIThread = true,
				//})
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
	}
}
