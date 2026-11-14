namespace AutumnOS.NativeHost.Windows;

// Deterministic model of the qa05 sequential cleanup ordering, with the same
// test seam as the new sequencer. This is not a run of historical native bytes.
internal sealed class BrowserCloseSequence(NativeBrowserCloseObservation observation)
{
    internal async Task<bool> RunAsync(Action revoke, Action completeGate, Func<Task> drain,
        Action closeController, List<Stream> pendingStreams, Func<Task<bool>> observeBrowserExit, Action releaseOriginal)
    {
        observation.Step = NativeBrowserCloseStep.Revoke; revoke(); completeGate();
        observation.Step = NativeBrowserCloseStep.DeliveryDrain; await drain();
        observation.Step = NativeBrowserCloseStep.Controller; closeController();
        observation.Step = NativeBrowserCloseStep.Streams;
        foreach (Stream stream in pendingStreams) stream.Dispose();
        pendingStreams.Clear();
        observation.Step = NativeBrowserCloseStep.Observe; bool clean = await observeBrowserExit();
        observation.Step = NativeBrowserCloseStep.Release; releaseOriginal();
        observation.Step = NativeBrowserCloseStep.Observe; return clean;
    }
}
