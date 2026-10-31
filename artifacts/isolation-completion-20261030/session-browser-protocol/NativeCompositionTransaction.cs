using System.Security.Cryptography;
using System.Text;

namespace AutumnOS.SessionBrowserProtocol;

// TextCompositionEnded's range describes the closing native composition, not
// the final committed text. In particular Length0 does not mean cancellation.
internal sealed record NativeCompositionSettledFact(long transaction_id, int end_start_index, int end_length,
    int end_selection_start, int end_selection_length, int notification_buffer_length, int settled_length,
    string settled_sha256, string decision, int text_changed_notifications_suppressed)
{
    public string notification_buffer_sha256 { get; init; } = "";
    public int post_end_text_change_revision { get; init; }
    public int text_changed_after_end_suppressed { get; init; }
    public bool initial_buffer_empty { get; init; }
}
// Only this transient dispatch carries text. Reports store Fact, never Dispatch.
internal sealed record NativeCompositionDispatch(string command, string? text, NativeCompositionSettledFact fact);

internal sealed class NativeCompositionTransaction
{
    private enum Phase { Idle, Active, PendingEnd, AwaitClear, Faulted }
    private Phase phase;
    private long transaction;
    private int endStart, endLength, endSelectionStart, endSelectionLength, notificationBufferLength, suppressed;
    private bool dispatchQueued;
    private string notificationHash = "", postEndHash = "";
    private int postEndLength, postEndRevision, suppressedAfterEnd;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal bool IsActive => phase == Phase.Active;
    internal bool HasPendingEnd => phase == Phase.PendingEnd;
    internal bool BlocksPlainText => phase != Phase.Idle;
    internal bool CanRequestNormalClose => phase is Phase.Idle or Phase.Faulted;
    internal long Begin(string actualNativeBuffer)
    {
        if (phase != Phase.Idle) throw new InvalidDataException("NATIVE_COMPOSITION_START_REQUIRES_IDLE");
        ValidateText(actualNativeBuffer);
        if (actualNativeBuffer.Length != 0) throw new InvalidDataException("NATIVE_COMPOSITION_EMPTY_INITIAL_BUFFER_REQUIRED");
        transaction = checked(transaction + 1); suppressed = 0; suppressedAfterEnd = 0; postEndRevision = 0; dispatchQueued = false; phase = Phase.Active; return transaction;
    }
    internal void RequireActive()
    { if (phase != Phase.Active) throw new InvalidDataException("NATIVE_COMPOSITION_UPDATE_WITHOUT_ACTIVE_START"); }
    internal long End(int start, int length, int selectionStart, int selectionLength, string actualNotificationBuffer)
    {
        if (phase is Phase.PendingEnd or Phase.AwaitClear) throw new InvalidDataException("NATIVE_COMPOSITION_DUPLICATE_END");
        if (phase != Phase.Active) throw new InvalidDataException("NATIVE_COMPOSITION_END_WITHOUT_START");
        ValidateText(actualNotificationBuffer); int actualNotificationBufferLength = actualNotificationBuffer.Length;
        if (start < 0 || length < 0 || start > actualNotificationBufferLength ||
            length > actualNotificationBufferLength - start || selectionStart is < 0 or > 1024 || selectionLength < 0 || selectionLength > 1024 - selectionStart)
            throw new InvalidDataException("NATIVE_COMPOSITION_END_BUFFER_BOUNDARY_REJECTED");
        // Selection is retained as an End notification fact. It need not lie in
        // the now-empty active range or match the later settled buffer length.
        endStart = start; endLength = length; endSelectionStart = selectionStart; endSelectionLength = selectionLength;
        notificationBufferLength = actualNotificationBufferLength; notificationHash = Hash(actualNotificationBuffer);
        phase = Phase.PendingEnd; return transaction;
    }
    internal bool ShouldInsertPlainText(string currentNativeBuffer)
    {
        if (phase != Phase.Idle)
        { suppressed = checked(suppressed + 1); if (phase is Phase.PendingEnd or Phase.AwaitClear) suppressedAfterEnd = checked(suppressedAfterEnd + 1); return false; }
        ValidateText(currentNativeBuffer); return currentNativeBuffer.Length != 0;
    }
    internal void ObservePostEndNativeChange(string actualNativeBuffer)
    {
        if (phase != Phase.PendingEnd) return;
        ValidateText(actualNativeBuffer); postEndLength = actualNativeBuffer.Length; postEndHash = Hash(actualNativeBuffer);
        postEndRevision = checked(postEndRevision + 1);
    }
    internal bool IsCurrentPending(long token) => token == transaction && phase == Phase.PendingEnd;
    internal bool CanSettle(long token) => IsCurrentPending(token) && postEndRevision > 0;
    internal NativeCompositionDispatch Settle(long token, string currentSettledNativeBuffer)
    {
        if (!CanSettle(token)) throw new InvalidDataException("NATIVE_COMPOSITION_SETTLE_REQUIRES_CURRENT_PENDING_END");
        ValidateText(currentSettledNativeBuffer);
        if (currentSettledNativeBuffer.Length != postEndLength || Hash(currentSettledNativeBuffer) != postEndHash)
            throw new InvalidDataException("NATIVE_COMPOSITION_UNOBSERVED_SETTLED_BUFFER_VERSION");
        bool cancel = currentSettledNativeBuffer.Length == 0;
        var fact = new NativeCompositionSettledFact(transaction, endStart, endLength, endSelectionStart, endSelectionLength,
            notificationBufferLength, currentSettledNativeBuffer.Length, Hash(currentSettledNativeBuffer), cancel ? "cancel" : "commit", suppressed)
        { notification_buffer_sha256 = notificationHash, post_end_text_change_revision = postEndRevision, text_changed_after_end_suppressed = suppressedAfterEnd, initial_buffer_empty = true };
        phase = Phase.AwaitClear;
        return new(cancel ? "ime_cancel" : "ime_commit", cancel ? null : currentSettledNativeBuffer, fact);
    }
    internal void ConfirmOwnBufferCleared(string actualNativeBuffer)
    {
        if (phase != Phase.AwaitClear || !dispatchQueued || actualNativeBuffer.Length != 0) throw new InvalidDataException("NATIVE_COMPOSITION_OWN_BUFFER_CLEAR_UNPROVEN");
        phase = Phase.Idle;
    }
    internal void ConfirmDispatchQueued(NativeCompositionDispatch dispatch)
    {
        if (phase != Phase.AwaitClear || dispatch.fact.transaction_id != transaction || dispatchQueued)
            throw new InvalidDataException("NATIVE_COMPOSITION_SINGLE_DISPATCH_QUEUE_PROOF_REQUIRED");
        dispatchQueued = true;
    }
    internal void InvalidateBoundary() => phase = Phase.Faulted;
    private static void ValidateText(string text)
    {
        if (text.Length > 1024 || text.Contains('\0')) throw new InvalidDataException("NATIVE_COMPOSITION_BOUNDED_BUFFER_REQUIRED");
        _ = Utf8.GetByteCount(text);
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
}
