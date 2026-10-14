using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace BlueBridge.Desktop;

public partial class MainWindow
{
    private readonly TransferRecordActions recordActions;
    private readonly Func<TransferRow, string, bool> confirmRecycle;

    private void RecordMenuOpened(object sender, RoutedEventArgs args)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { DataContext: TransferRow row }) return;
        menu.Tag = row.Id;
        var available = recordActions.Availability(row.Id);
        ((MenuItem)menu.Items[0]).IsEnabled = available.CanOpen;
        ((MenuItem)menu.Items[2]).IsEnabled = available.CanRemove;
        ((MenuItem)menu.Items[3]).IsEnabled = available.CanRecycle;
    }

    private static Guid? MenuRecordId(object sender) => sender is MenuItem item &&
        ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu { Tag: Guid id } ? id : null;

    private void OpenRecordClick(object sender, RoutedEventArgs args)
    {
        if (MenuRecordId(sender) is { } id) OpenRecord(id);
    }
    private void OpenRecord(Guid id)
    {
        int index = FindRow(id);
        if (index < 0) { SetActivity("这条记录已不存在"); return; }
        var row = Transfers[index];
        string? error = TransferFileOpener.Open(row, fileLauncher);
        SetActivity(error ?? "已打开：" + row.Name);
    }
    private void RemoveRecordClick(object sender, RoutedEventArgs args)
    {
        if (MenuRecordId(sender) is { } id) SetActivity(recordActions.Remove(id).Message);
    }
    private async void RecycleRecordClick(object sender, RoutedEventArgs args)
    {
        if (MenuRecordId(sender) is not { } id) return;
        try { SetActivity((await recordActions.RemoveWithFileAsync(id, confirmRecycle)).Message); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Record recycling failed: {0}", error);
            SetActivity("回收操作失败，记录已保留，请稍后重试");
        }
    }
    private bool ConfirmRecycle(TransferRow row, string path)
    {
        bool source = TransferRecordActions.SamePath(row.Source, path) && string.IsNullOrWhiteSpace(row.SavedPath);
        string message = $"将以下电脑文件移入回收站，并删除这条记录？\n\n{path}\n\n" +
            (source ? "这是发送时选择的原文件。\n" : "") + "手机上的副本不受影响。";
        return MessageBox.Show(this, message, "删除记录与本地文件", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }
    private void ReleaseRecord(Guid id)
    {
        retryImages.Remove(id);
        var remaining = retryImageOrder.Where(value => value != id).ToArray();
        retryImageOrder.Clear(); foreach (Guid value in remaining) retryImageOrder.Enqueue(value);
    }
    private void LocalFileRemoved(string path)
    {
        if (TransferRecordActions.SamePath(lastImage, path)) { lastImage = null; SaveImageButton.IsEnabled = false; }
    }
}
