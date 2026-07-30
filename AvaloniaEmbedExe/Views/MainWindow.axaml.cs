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
		}
	}
}