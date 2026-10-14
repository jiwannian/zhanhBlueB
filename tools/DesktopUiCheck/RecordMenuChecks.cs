using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlueBridge.Core;
using BlueBridge.Desktop;

internal static class RecordMenuChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zhanhBlueB-menu-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var recycler = new FakeRecycler();
        bool approve = false;
        var launches = new List<ProcessStartInfo>();
        var view = new MainWindow(false, launches.Add, directory, recycler, (row, path) => {
            Require(path == Path.GetFullPath(row.LocalPath!), "Confirmation path differs from target"); return approve;
        });
        var surface = (FrameworkElement)view.Content;
        try
        {
            string path = Path.Combine(directory, "中文 & 原始文件.xlsx");
            File.WriteAllText(path, "keep bytes");
            var first = new TransferRow(Guid.NewGuid(), "第一条", "电脑 → 手机", "已完成", 1, 1, Source: path);
            var target = first with { Id = Guid.NewGuid(), Name = "右键目标" };
            view.Transfers.Add(first); view.Transfers.Add(target);
            Layout(surface); var list = Descendants(surface).OfType<ListBox>().Single(); list.SelectedIndex = 0;
            var menu = Menu(surface, list, 1);
            Require(!ReferenceEquals(menu, Menu(surface, list, 0)), "Rows share a context menu");
            Require(list.ContextMenu is null, "Blank list has a file menu");
            Prepare(menu);
            Click(menu, 0);
            Require(launches.Single().FileName == path, "Menu open uses wrong target");
            Click(menu, 3); // Cancel confirmation.
            Require(view.Transfers.Count == 2 && recycler.Paths.Count == 0, "Cancel changed record or file");
            Click(menu, 2);
            Require(view.Transfers.Single().Id == first.Id && File.Exists(path), "Record-only deletion targeted selection or deleted file");
            typeof(MainWindow).GetMethod("UpdateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new TransferProgress(target.Id, target.Name, 1, 1, true)]);
            Require(view.Transfers.Count == 1, "Late progress resurrected deleted record");

            view.Transfers.Add(target); Layout(surface); menu = Menu(surface, list, 1); Prepare(menu);
            approve = true; recycler.Result = new(RecycleStatus.Failed, "测试占用"); Click(menu, 3);
            Require(view.Transfers.Count == 2 && view.Transfers[1].State == "已完成", "Failed recycle removed or failed transfer");
            var cache = (Dictionary<Guid, byte[]>)typeof(MainWindow).GetField("retryImages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            cache[target.Id] = [1, 2, 3];
            typeof(MainWindow).GetField("lastImage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, path);
            var saveButton = (Button)view.FindName("SaveImageButton"); saveButton.IsEnabled = true;
            recycler.Result = new(RecycleStatus.Success); Prepare(menu); Click(menu, 3);
            Require(view.Transfers.Single().Id == first.Id && view.Transfers[0].LocalFileRemoved, "Related record not invalidated");
            Require(!cache.ContainsKey(target.Id) && !saveButton.IsEnabled, "Retry cache/image-save source survived deletion");

            view.Transfers.Clear();
            view.Transfers.Add(first with { State = "正在发送", JobPending = true });
            menu = Menu(surface, list, 0); Prepare(menu);
            Require(!((MenuItem)menu.Items[0]).IsEnabled && !((MenuItem)menu.Items[2]).IsEnabled && !((MenuItem)menu.Items[3]).IsEnabled,
                "Active task menu is enabled");
            view.Transfers[0] = first with { JobPending = true }; menu = Menu(surface, list, 0); Prepare(menu);
            Require(!((MenuItem)menu.Items[2]).IsEnabled, "Pending cleanup deletion is enabled");
            view.Transfers[0] = first with { Source = null }; menu = Menu(surface, list, 0); Prepare(menu);
            Require(((MenuItem)menu.Items[2]).IsEnabled && !((MenuItem)menu.Items[3]).IsEnabled, "No-path record availability is wrong");
            // A menu cached before a record changes must re-find the ID on click.
            view.Transfers.Clear(); Click(menu, 2);
            Require(view.Transfers.Count == 0, "Stale menu affected another row");
            Console.WriteLine("PASS: WPF row-specific menu, non-selected target, open/remove/cancel/failure, pending guards, cache/related records, late progress, blank area");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ContextMenu Menu(FrameworkElement surface, ListBox list, int index)
    {
        Layout(surface); list.ScrollIntoView(list.Items[index]); Layout(surface);
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        var border = Descendants(item).OfType<Border>().First(element => element.ContextMenu is not null);
        border.ContextMenu!.PlacementTarget = border;
        return border.ContextMenu;
    }
    private static void Prepare(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
    private static void Click(ContextMenu menu, int index) => ((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Layout(FrameworkElement surface) { surface.Measure(new(900, 700)); surface.Arrange(new(0, 0, 900, 700)); surface.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeRecycler : ILocalFileRecycler
    {
        public List<string> Paths { get; } = [];
        public RecycleResult Result = new(RecycleStatus.Success);
        public Task<RecycleResult> RecycleAsync(string path) { Paths.Add(path); return Task.FromResult(Result); }
    }
}
