using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueBridge.Desktop;

// Render and inspect the actual WPF view without opening a Bluetooth session,
// registering a clipboard listener, interacting with user windows, or changing startup.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/ui");
            Directory.CreateDirectory(output);
            var application = new App(startProduct: false);
            application.InitializeComponent();
            var launches = new List<ProcessStartInfo>();
            var view = new MainWindow(startHost: false, fileLauncher: info => launches.Add(info), downloadDirectory: Path.Combine(output, "downloads"));
            Require(view.Title.Contains("zhanhBlueB"), "Window title is missing brand");
            Require(((Button)view.FindName("ListenButton")).Content.ToString() == "开启连接", "Connection action missing");
            Require(((Button)view.FindName("SendFilesButton")).Content.ToString() == "选择文件发送", "File action missing");
            Require(!((Button)view.FindName("SaveImageButton")).IsEnabled, "No-image save must be disabled");
            var element = (FrameworkElement)view.Content;
            view.Content = null;
            element.DataContext = view;
            var surface = new Border { Background = view.Background, Child = element };
            System.Windows.Documents.TextElement.SetForeground(surface, view.Foreground);
            System.Windows.Documents.TextElement.SetFontFamily(surface, view.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(surface, view.FontSize);
            Capture(surface, (int)view.Width, (int)view.Height, Path.Combine(output, "windows-default.png"));
            view.Transfers.Add(new(Guid.NewGuid(), "项目资料与设计稿.zip", "电脑 → 手机", "正在发送", 48L << 20, 100L << 20));
            view.Transfers.Add(new(Guid.NewGuid(), "测试截图.png", "手机 → 电脑", "已完成", 1850000, 1850000));
            view.Transfers.Add(new(Guid.NewGuid(), "包含中文、空格和较长名称的文档-最终版本.pdf", "电脑 → 手机", "失败，可重试", 0, 630000, "fixture.pdf"));
            Capture(surface, (int)view.Width, (int)view.Height, Path.Combine(output, "windows-transfers.png"));
            Capture(surface, (int)view.MinWidth, (int)view.MinHeight, Path.Combine(output, "windows-minimum.png"));
            Require(element.ActualWidth > 0 && element.ActualHeight > 0, "Layout did not render");
            FileOpenChecks.Run(view, surface, launches);
            Console.WriteLine("PASS: actual WPF view/actions/default image state and standard/minimum layouts");
            Console.WriteLine("Screenshots: " + output);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Capture(FrameworkElement surface, int width, int height, string path)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

}
