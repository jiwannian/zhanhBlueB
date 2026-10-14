using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlueBridge.Core;
using BlueBridge.Desktop;

// Exercise production XAML event wiring on the rendered item containers.
// The injected launcher records requests; no default app or user window opens.
internal static class FileOpenChecks
{
    public static void Run(MainWindow view, FrameworkElement surface, List<ProcessStartInfo> launches)
    {
        string directory = Path.Combine(Path.GetTempPath(), "zhanhBlueB-ui-open-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "截图 & 中文 (1).jpg");
            File.WriteAllBytes(path, [1, 2, 3]);
            var id = Guid.NewGuid();
            view.Transfers.Clear();
            view.Transfers.Add(new(id, "截图 & 中文.jpg", "手机 → 电脑", "正在接收", 3, 3));
            var receive = typeof(MainWindow).GetMethod("ReceiveItemAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Pump((Task)receive.Invoke(view, [new ReceivedItem(id, path, new("截图 & 中文.jpg", 3, "file", "image/jpeg"))])!);
            Require(view.Transfers[0].SavedPath == path && view.Transfers[0].CanOpen, "Received item lost its final renamed path");

            var list = Descendants(surface).OfType<ListBox>().Single();
            var received = Item(list, surface, 0);
            var title = Descendants(received).OfType<TextBlock>().First(text => text.Text == "截图 & 中文.jpg");
            DoubleClick(received, title, MouseButton.Left);
            Require(launches.Count == 1 && launches[0].FileName == path, "Completed received item double click did not open its saved path");
            Require(launches[0].UseShellExecute && launches[0].Arguments.Length == 0, "File launch is not a default-association request");

            DoubleClick(received, received, MouseButton.Right);
            Require(launches.Count == 1, "Right double click opened a file");
            foreach (var button in Descendants(received).OfType<Button>())
                DoubleClick(received, button, MouseButton.Left);
            Require(launches.Count == 1, "Double click on cancel/retry opened a file");

            var inline = new System.Windows.Documents.Run("截图 & 中文.jpg");
            title.Inlines.Clear();
            title.Inlines.Add(inline);
            DoubleClick(received, inline, MouseButton.Left);
            Require(launches.Count == 2 && launches[1].FileName == path, "Inline text double click could not resolve a content-element source");

            view.Transfers.Add(new(Guid.NewGuid(), "未完成.jpg", "手机 → 电脑", "正在接收", 3, 3, SavedPath: path));
            var unfinished = Item(list, surface, 1);
            DoubleClick(unfinished, unfinished, MouseButton.Left);
            Require(launches.Count == 2, "Unfinished item opened despite awaiting confirmation");

            view.Transfers.Add(new(Guid.NewGuid(), "发送图片.jpg", "电脑 → 手机", "已完成", 3, 3, Source: path));
            var sent = Item(list, surface, 2);
            DoubleClick(sent, sent, MouseButton.Left);
            Require(launches.Count == 3 && launches[2].FileName == path, "Sent file did not open its local source");

            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var imageId = Guid.NewGuid();
            view.Transfers.Add(new(imageId, "clipboard.png", "电脑 → 手机", "正在发送", png.Length, png.Length));
            var complete = typeof(MainWindow).GetMethod("CompleteSentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Pump((Task)complete.Invoke(view, [imageId, png])!);
            string? savedImage = view.Transfers[3].SavedPath;
            Require(savedImage is not null && File.ReadAllBytes(savedImage).SequenceEqual(png), "Sent clipboard image was not saved for reopening");
            var clipboard = Item(list, surface, 3);
            DoubleClick(clipboard, clipboard, MouseButton.Left);
            Require(launches.Count == 4 && launches[3].FileName == savedImage, "Sent clipboard PNG record did not open its local copy");
            CheckLocalSaveFailure(view, png, Path.GetDirectoryName(savedImage!)!, complete);

            File.Delete(path);
            DoubleClick(received, received, MouseButton.Left);
            Require(launches.Count == 4 && ((TextBlock)view.FindName("ActivityText")).Text.Contains("移动或删除"), "Missing file was launched or not reported");
            Console.WriteLine("PASS: received final path, WPF item/text/inline double click, default-app request, right/button/unfinished exclusions, sent source, clipboard PNG copy/save failure, deleted-file message");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckLocalSaveFailure(MainWindow view, byte[] png, string directory, MethodInfo complete)
    {
        string partial = Path.Combine(directory, ".partial");
        string backup = partial + ".ui-fixture-backup";
        Directory.Move(partial, backup);
        try
        {
            File.WriteAllText(partial, "Block the test's temporary directory to force an IOException");
            var id = Guid.NewGuid();
            view.Transfers.Add(new(id, "clipboard.png", "电脑 → 手机", "正在发送", png.Length, png.Length));
            Pump((Task)complete.Invoke(view, [id, png])!);
            var row = view.Transfers.Single(item => item.Id == id);
            Require(row.State == "已完成" && !row.CanOpen, "Failed local save changed the successfully sent state");
            Require(((TextBlock)view.FindName("ActivityText")).Text.Contains("本地保存失败"), "Failed local save did not show an explanation");
        }
        finally
        {
            File.Delete(partial);
            Directory.Move(backup, partial);
        }
    }

    private static ListBoxItem Item(ListBox list, FrameworkElement surface, int index)
    {
        surface.UpdateLayout();
        list.ScrollIntoView(list.Items[index]);
        surface.UpdateLayout();
        return (ListBoxItem)(list.ItemContainerGenerator.ContainerFromIndex(index) ?? throw new InvalidOperationException("Item container was not generated"));
    }

    private static void DoubleClick(ListBoxItem item, DependencyObject source, MouseButton button) => item.RaiseEvent(
        new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = Control.MouseDoubleClickEvent, Source = source });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var deadline = Stopwatch.StartNew();
            timeout.Tick += (_, _) => { if (task.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("WPF receive completion did not return");
        }
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
