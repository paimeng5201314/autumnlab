using System.Text;
using AutumnOS.NativeHost.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AutumnOS.Shell;

internal sealed partial class RemoteApplicationView
{
    private readonly Grid viewPanel = new();
    private readonly TextBox ime = new() { PlaceholderText = "中文输入 · 先点应用内的文本框", MaxLength = 256,
        AllowDrop = false, CanDrag = false, AcceptsReturn = false, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
    private readonly NativeCompositionTransaction composition = new();
    private bool clearing, settleQueued;
    private long ending;

    private void AttachSurface()
    {
        viewPanel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        viewPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(ime, 1); viewPanel.Children.Add(surface); viewPanel.Children.Add(ime); container.Children.Add(viewPanel);
        ime.Paste += (_, e) => e.Handled = true;
        ime.CopyingToClipboard += (_, e) => e.Handled = true;
        ime.CuttingToClipboard += (_, e) => e.Handled = true;
        ime.ContextFlyout = new MenuFlyout();
        viewPanel.ContextRequested += (_, e) => e.Handled = true;
        viewPanel.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.None; e.Handled = true; };
        viewPanel.Drop += (_, e) => { e.AcceptedOperation = DataPackageOperation.None; e.Handled = true; };

        void Observe(Action read)
        {
            if (disposed || clearing) return;
            try { if (!displayed || !isForeground() || ime.FocusState == FocusState.Unfocused) throw new InvalidDataException(); read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { composition.InvalidateBoundary(); crashed("native_composition_binding_lost"); }
        }
        void Send(string action, string text)
        {
            if (text.Length > 256 || text.Any(char.IsControl) || Encoding.UTF8.GetByteCount(text) > 1024 ||
                !Input(NativeViewKind.Composition, new { action, text })) throw new InvalidDataException();
        }
        void ClearOwnBuffer() { clearing = true; try { ime.Text = ""; } finally { clearing = false; } }
        void Settle()
        {
            if (settleQueued || !composition.CanSettle(ending)) return;
            settleQueued = true;
            if (!ime.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                settleQueued = false;
                if (!composition.CanSettle(ending)) return;
                Observe(() =>
                {
                    var native = composition.Settle(ending, ime.Text);
                    Send(native.command == "ime_commit" ? "commit" : "cancel", native.text ?? "");
                    composition.ConfirmDispatchQueued(native); ClearOwnBuffer(); composition.ConfirmOwnBufferCleared(ime.Text);
                });
            })) throw new InvalidDataException();
        }
        ime.TextCompositionStarted += (_, _) => Observe(() => { composition.Begin(ime.Text); Send("start", ""); });
        ime.TextCompositionChanged += (_, e) => Observe(() =>
        {
            composition.RequireActive();
            string value = ime.Text.Substring(e.StartIndex, e.Length);
            if (value.Length > 0) Send("update", value);
        });
        ime.TextCompositionEnded += (sender, e) => Observe(() =>
        {
            ending = composition.End(e.StartIndex, e.Length, ime.SelectionStart, ime.SelectionLength, ime.Text);
            Settle(); _ = ExpireUnsettledCompositionAsync(ending);
        });
        ime.TextChanging += (_, _) => { if (composition.HasPendingEnd) Observe(() => { composition.ObservePostEndNativeChange(ime.Text); Settle(); }); };
        // A standalone native text change might be a paste, accessibility replacement or
        // suggestion. It carries no composition provenance and must not enter the child.
        ime.TextChanged += (_, _) => { if (!clearing && !disposed && !composition.BlocksPlainText && ime.Text.Length != 0) ClearOwnBuffer(); };
        ime.LostFocus += (_, _) => { if (!disposed && composition.BlocksPlainText) crashed("native_composition_focus_lost"); };
    }
    private async Task ExpireUnsettledCompositionAsync(long token)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (!disposed && composition.IsCurrentPending(token)) crashed("native_composition_unsettled");
    }
}
