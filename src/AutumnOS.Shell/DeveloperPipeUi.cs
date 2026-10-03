using System.ComponentModel;
using System.IO.Pipes;
using AutumnOS.Packages;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private CancellationTokenSource? developerPipeLifetime;
    private Task? developerPipeTask;

    private void RefreshDeveloperPipe()
    {
        if (!DeveloperModeEnabled || lifetime.IsCancellationRequested) { StopDeveloperPipe(); return; }
        if (developerPipeLifetime is not null) return;
        developerPipeLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, developerTools!.CapabilityToken);
        CancellationToken cancellationToken = developerPipeLifetime.Token;
        developerPipeTask = Task.Run(() => RunDeveloperPipeAsync(cancellationToken), CancellationToken.None);
    }
    private void StopDeveloperPipe()
    {
        CancellationTokenSource? previous = developerPipeLifetime; developerPipeLifetime = null;
        if (previous is not null) { previous.Cancel(); previous.Dispose(); }
    }
    private async Task RunDeveloperPipeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using NamedPipeServerStream pipe = DeveloperPreviewProtocol.CreateServer(AppContext.BaseDirectory);
                using CancellationTokenRegistration close = cancellationToken.Register(() => pipe.Dispose());
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestLifetime.CancelAfter(TimeSpan.FromSeconds(120));
                CancellationToken requestToken = requestLifetime.Token;
                try
                {
                    DeveloperPreviewRequest request = await DeveloperPreviewProtocol.ReadAsync<DeveloperPreviewRequest>(pipe, requestLifetime.Token).ConfigureAwait(false);
                    DeveloperPreviewProtocol.Validate(request);
                    TaskCompletionSource<DeveloperPreviewResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!DispatcherQueue.TryEnqueue(async () =>
                    {
                        try { completion.TrySetResult(await HandleDeveloperRequestAsync(request, requestToken)); }
                        catch (OperationCanceledException) { completion.TrySetResult(new(false, "USER_CANCELLED")); }
                        catch (PackageException error) { completion.TrySetResult(new(false, error.Code)); }
                        catch (Exception error) when (error is not OutOfMemoryException) { completion.TrySetResult(new(false, "DEVELOPER_OPERATION_FAILED")); }
                    })) throw new OperationCanceledException();
                    DeveloperPreviewResponse response = await completion.Task.WaitAsync(requestLifetime.Token).ConfigureAwait(false);
                    await DeveloperPreviewProtocol.WriteAsync(pipe, response, requestLifetime.Token).ConfigureAwait(false);
                }
                catch (PackageException error)
                {
                    if (!requestLifetime.IsCancellationRequested)
                        try { await DeveloperPreviewProtocol.WriteAsync(pipe, new DeveloperPreviewResponse(false, error.Code), requestLifetime.Token).ConfigureAwait(false); }
                        catch (Exception writeError) when (writeError is IOException or ObjectDisposedException or OperationCanceledException) { }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or Win32Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(() => developerResult.Text = "DEVELOPER_CHANNEL_UNAVAILABLE · 本机 CLI 通道未开启；界面工具仍可使用。请关闭再开启开发模式重试。");
        }
    }
    private async Task<DeveloperPreviewResponse> HandleDeveloperRequestAsync(DeveloperPreviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!DeveloperModeEnabled) return new(false, "DEVELOPER_MODE_DISABLED");
        if (developerBusy || aboutOpen || accountOperation) return new(false, "DEVELOPER_BUSY");
        developerBusy = true; RefreshDeveloperButtons();
        try
        {
            if (request.Command != "preview") return await ApplyDeveloperDebugAsync(request.Command, request.SessionId, cancellationToken);
            await PreviewDeveloperPackageAsync(request.PackagePath!, cancellationToken, request.Sha256);
            cancellationToken.ThrowIfCancellationRequested();
            developerResult.Text = "CLI 请求的包已由用户确认，在独立内部预览中运行。调试仅作用于这个预览。";
            return await ApplyDeveloperDebugAsync("status", developerPreviewSessionId, cancellationToken);
        }
        finally { developerBusy = false; RefreshDeveloperButtons(); }
    }
}
