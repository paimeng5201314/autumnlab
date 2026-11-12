using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;

namespace AutumnOS.NativeHost.Windows;

/// <summary>
/// Actual STA dispatch/Post mechanics. Construction requires an already owned
/// native controller/document. The private browser owner must supply its native
/// document admission check; this object cannot issue startup or OS evidence.
/// It owns neither the controller nor the HWND, and cannot retire either one.
/// </summary>
internal sealed class NativeWebViewDeliveryTarget : IRuntimeNativeDeliveryDispatcher
{
    private readonly Control originalWindow;
    private readonly CoreWebView2 originalCore;
    private readonly Thread originalThread;
    private readonly IntPtr originalWindowHandle;
    private readonly RuntimeDeliveryBinding documentBinding;
    private readonly string documentUri;
    private readonly Func<bool> requireAdmittedNativeDocument;
    private bool guardsAttached, documentInvalidated;

    internal NativeWebViewDeliveryTarget(Control originalWindow, CoreWebView2 originalCore,
        RuntimeDeliveryBinding binding, string documentUri, Func<bool> requireAdmittedNativeDocument)
    {
        ArgumentNullException.ThrowIfNull(originalWindow);
        ArgumentNullException.ThrowIfNull(originalCore);
        ArgumentNullException.ThrowIfNull(requireAdmittedNativeDocument);
        RuntimeDeliveryWireProtocol.ValidateBinding(binding);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || originalWindow.InvokeRequired ||
            !originalWindow.IsHandleCreated || originalWindow.IsDisposed || originalWindow.Disposing)
            throw new InvalidOperationException("NATIVE_DELIVERY_ORIGINAL_STA_WINDOW_REQUIRED");
        if (!Uri.TryCreate(documentUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            documentUri != uri.AbsoluteUri || originalCore.Source != documentUri)
            throw new InvalidDataException("NATIVE_DELIVERY_EXACT_DOCUMENT_SOURCE_REQUIRED");
        this.originalWindow = originalWindow; this.originalCore = originalCore;
        documentBinding = binding; this.documentUri = documentUri;
        this.requireAdmittedNativeDocument = requireAdmittedNativeDocument;
        originalThread = Thread.CurrentThread;
        originalWindowHandle = originalWindow.Handle;
    }

    // Publish the target to its owner before attaching native callbacks. Any
    // partial attachment failure remains owned and permanently invalidates it.
    internal void AttachDocumentGuards()
    {
        if (!IsDispatcherThread || guardsAttached || documentInvalidated)
            throw new InvalidOperationException("NATIVE_DOCUMENT_GUARDS_ONCE_ON_ORIGINAL_STA");
        try
        {
            originalCore.NavigationStarting += (_, _) => documentInvalidated = true;
            originalCore.ContentLoading += (_, _) => documentInvalidated = true;
            originalCore.SourceChanged += (_, _) => documentInvalidated = true;
            guardsAttached = true;
        }
        catch { documentInvalidated = true; throw; }
    }

    internal void InvalidateDocument()
    {
        if (!IsDispatcherThread) throw new InvalidOperationException("NATIVE_DOCUMENT_ORIGINAL_STA_REQUIRED");
        documentInvalidated = true;
    }

    public bool IsDispatcherThread => ReferenceEquals(Thread.CurrentThread, originalThread) &&
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        // A false return means BeginInvoke was never called. An exception from
        // BeginInvoke remains ambiguous to the actor and must quarantine it.
        if (!originalWindow.IsHandleCreated || originalWindow.IsDisposed || originalWindow.Disposing) return false;
        originalWindow.BeginInvoke(action);
        return true;
    }

    public bool IsCurrentDocument(RuntimeDeliveryBinding binding)
    {
        if (!NativeBindingIsCurrent()) return false;
        // Channel generations are changed by the actor's ordered STA fence.
        // All other fields stay bound to this original native document owner.
        if ((binding with { ChannelGeneration = documentBinding.ChannelGeneration }) != documentBinding) return false;
        // The owner's predicate must be synchronous and side-effect free. Even
        // if it reenters native navigation/close, recheck the original objects
        // and monotonically invalidated native document after it returns.
        return requireAdmittedNativeDocument() && NativeBindingIsCurrent();
    }

    public void PostJson(string json)
    {
        if (!NativeBindingIsCurrent()) throw new InvalidOperationException("NATIVE_POST_ORIGINAL_DOCUMENT_REQUIRED");
        // Returning from this call is the sole Posted fact. Queuing a callback,
        // flushing IPC, JavaScript messages and timers cannot reach this fact.
        // Any exception is Unknown because the native call may have queued it.
        originalCore.PostWebMessageAsJson(json);
    }

    private bool NativeBindingIsCurrent()
    {
        if (!IsDispatcherThread || !guardsAttached || documentInvalidated || originalWindow.IsDisposed ||
            originalWindow.Disposing || !originalWindow.IsHandleCreated || originalWindow.Handle != originalWindowHandle)
            return false;
        bool sameSource = originalCore.Source == documentUri;
        return sameSource && !documentInvalidated && !originalWindow.IsDisposed && !originalWindow.Disposing &&
            originalWindow.IsHandleCreated && originalWindow.Handle == originalWindowHandle;
    }
}
