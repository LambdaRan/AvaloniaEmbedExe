using Avalonia.Controls;
using AvaloniaEmbedExe.NativeControls;

namespace AvaloniaEmbedExe.Views
{
	public partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();
			EmbedWpf.Content = new WinNativeControlWrap(new DemoWpf());

			// 外部程序启动失败时降级显示错误，而不是让异常从布局阶段冒泡把应用带崩。
			ExternalApp.LaunchFailed += (_, e) => ShowStatus($"嵌入失败：{e.Error.Message}");
			ExternalApp.ExternalAppExited += (_, _) => ShowStatus("外部程序已退出");
		}

		private void ShowStatus(string message)
		{
			EmbedStatus.Text = message;
			EmbedStatus.IsVisible = true;
		}
	}
}
