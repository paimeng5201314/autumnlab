using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    // Accessed only on this window's dispatcher. Cancellation requests dismissal;
    // an operation stays registered until its original native completion arrives.
    private readonly HashSet<ContentDialog> ownedDialogs = [];
    private readonly Dictionary<Guid, IAsyncInfo> ownedPickers = [];
    private readonly HashSet<Task> ownedNetworkTasks = [];
    private readonly List<Task> retiredDownloadDrains = [];

    private async Task<T> TrackOwnedNetworkAsync<T>(Task<T> task)
    {
        ownedNetworkTasks.Add(task);
        try { return await task; }
        finally { ownedNetworkTasks.Remove(task); }
    }

    private async Task TrackOwnedNetworkAsync(Task task)
    {
        ownedNetworkTasks.Add(task);
        try { await task; }
        finally { ownedNetworkTasks.Remove(task); }
    }

    private void RetireStoreDownloads()
    {
        retiredDownloadDrains.RemoveAll(task => task.IsCompletedSuccessfully);
        if (storeDownloads is not null) retiredDownloadDrains.Add(storeDownloads.DisposeAsync().AsTask());
    }

    private async Task<ContentDialogResult> ShowOwnedDialogAsync(ContentDialog dialog, CancellationToken token = default)
    {
        if (shutdownRequested || lifetime.IsCancellationRequested || token.IsCancellationRequested) return ContentDialogResult.None;
        ownedDialogs.Add(dialog);
        using var cancel = token.Register(() => DispatcherQueue.TryEnqueue(() => HideOwnedDialog(dialog)));
        using var close = lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(() => HideOwnedDialog(dialog)));
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            return shutdownRequested || lifetime.IsCancellationRequested || token.IsCancellationRequested ? ContentDialogResult.None : result;
        }
        finally { ownedDialogs.Remove(dialog); }
    }

    private void HideOwnedDialog(ContentDialog dialog)
    {
        if (!ownedDialogs.Contains(dialog)) return;
        try { dialog.Hide(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ReportShutdownWait("dialog_cancel", error.GetType().Name); }
    }

    private async Task<T> RunOwnedPickerAsync<T>(Func<IAsyncOperation<T>> start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lifetime.Token.ThrowIfCancellationRequested();
        if (shutdownRequested) throw new OperationCanceledException(lifetime.Token);
        Guid id = Guid.NewGuid();
        IAsyncOperation<T> operation = start();
        ownedPickers.Add(id, operation);
        using var cancel = token.Register(() => DispatcherQueue.TryEnqueue(() => CancelOwnedPicker(id)));
        using var close = lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(() => CancelOwnedPicker(id)));
        try
        {
            // Do not use AsTask(token): its cancellation is not native completion.
            T result = await operation.AsTask();
            token.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (shutdownRequested) throw new OperationCanceledException(lifetime.Token);
            return result;
        }
        finally { ownedPickers.Remove(id); }
    }

    private void CancelOwnedPicker(Guid id)
    {
        if (!ownedPickers.TryGetValue(id, out IAsyncInfo? operation)) return;
        try { operation.Cancel(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ReportShutdownWait("picker_cancel", error.GetType().Name); }
    }

    private void CancelOwnedUiOperations()
    {
        foreach (ContentDialog dialog in ownedDialogs.ToArray()) HideOwnedDialog(dialog);
        foreach (Guid id in ownedPickers.Keys.ToArray()) CancelOwnedPicker(id);
    }
}
