using System.Runtime.ExceptionServices;

namespace AutumnOS.NativeHost.Windows;

// Local cleanup orchestration only. A failed operation never becomes a Closed
// acknowledgement, and this helper cannot assert process-group retirement.
internal sealed class BrowserCloseSequence(NativeBrowserCloseObservation observation)
{
    private Exception? firstFailure;
    private NativeBrowserCloseStep firstFailureStep;

    internal async Task<bool> RunAsync(Action revoke, Action completeGate, Func<Task> drain,
        Action closeController, List<Stream> pendingStreams, Func<Task<bool>> observeBrowserExit, Action releaseOriginal)
    {
        Attempt(NativeBrowserCloseStep.Revoke, revoke);
        Attempt(NativeBrowserCloseStep.Revoke, completeGate);
        await AttemptAsync(NativeBrowserCloseStep.DeliveryDrain, drain);
        Attempt(NativeBrowserCloseStep.Controller, closeController);
        // Dispose can fail after doing partial work. Retain every failed entry,
        // attempt every independent stream, and never Clear the owned collection.
        foreach (Stream stream in pendingStreams.ToArray())
            if (Attempt(NativeBrowserCloseStep.Streams, stream.Dispose)) pendingStreams.Remove(stream);
        bool clean = false;
        await AttemptAsync(NativeBrowserCloseStep.Observe, async () => { clean = await observeBrowserExit(); });
        Attempt(NativeBrowserCloseStep.Release, releaseOriginal);
        if (firstFailure is not null)
        {
            observation.Step = firstFailureStep;
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
        observation.Step = NativeBrowserCloseStep.Observe;
        return clean;
    }

    private bool Attempt(NativeBrowserCloseStep step, Action operation)
    {
        observation.Step = step;
        try { operation(); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { Remember(step, error); return false; }
    }
    private async Task AttemptAsync(NativeBrowserCloseStep step, Func<Task> operation)
    {
        observation.Step = step;
        try { await operation(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Remember(step, error); }
    }
    private void Remember(NativeBrowserCloseStep step, Exception error)
    { if (firstFailure is null) { firstFailure = error; firstFailureStep = step; } }
}
