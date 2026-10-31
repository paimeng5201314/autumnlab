using AutumnOS.Shell;

namespace AutumnOS.Tests;

internal static class RuntimeViewBackendEventTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("runtime.view_backend_events.source_getters_stay_behind_disposed_guard", SourceGuard);
        yield return ("runtime.view_backend_events.navigation_getters_stay_behind_disposed_guard", NavigationGuard);
        yield return ("runtime.view_backend_events.content_and_completion_getters_stay_behind_disposed_guard", DocumentGuards);
        yield return ("runtime.view_backend_events.frame_cancel_does_not_read_URI_or_navigation", FrameCancel);
        yield return ("runtime.view_backend_events.active_reads_are_lazy_and_original_exceptions_propagate", ActiveReads);
        yield return ("runtime.view_backend_events.webmessage_getters_execute_inside_parent_try", WebMessageTry);
    }

    private static void SourceGuard()
    {
        int calls = 0; var failure = new InvalidOperationException("FIXTURE_NATIVE_GETTER_FAILURE");
        string? Source() { calls++; throw failure; } bool NewDocument() { calls++; throw failure; }
        var args = new RuntimeViewSourceChangedEventArgs(Source, NewDocument);
        Check(calls == 0);
        static void Handler(bool disposed, RuntimeViewSourceChangedEventArgs value)
        { if (disposed) return; _ = value.Source; _ = value.IsNewDocument; }
        Handler(true, args); Check(calls == 0);
    }
    private static void NavigationGuard()
    {
        int calls = 0; var failure = new InvalidOperationException("FIXTURE_NATIVE_GETTER_FAILURE");
        string Uri() { calls++; throw failure; } ulong Navigation() { calls++; throw failure; }
        bool Cancel() { calls++; throw failure; } void SetCancel(bool _) { calls++; throw failure; }
        var args = new RuntimeViewNavigationStartingEventArgs(Uri, Navigation, Cancel, SetCancel);
        Check(calls == 0);
        static void Handler(bool disposed, RuntimeViewNavigationStartingEventArgs value)
        { if (disposed) return; _ = value.Uri; _ = value.NavigationId; }
        Handler(true, args); Check(calls == 0);
    }
    private static void DocumentGuards()
    {
        int calls = 0; var failure = new InvalidOperationException("FIXTURE_NATIVE_GETTER_FAILURE");
        ulong Navigation() { calls++; throw failure; } bool Flag() { calls++; throw failure; }
        var content = new RuntimeViewContentLoadingEventArgs(Navigation, Flag);
        var completed = new RuntimeViewNavigationCompletedEventArgs(Navigation, Flag);
        Check(calls == 0);
        static void Content(bool disposed, RuntimeViewContentLoadingEventArgs value)
        { if (disposed) return; _ = value.IsErrorPage; _ = value.NavigationId; }
        static void Complete(bool disposed, RuntimeViewNavigationCompletedEventArgs value)
        { if (disposed) return; _ = value.NavigationId; _ = value.IsSuccess; }
        Content(true, content); Complete(true, completed); Check(calls == 0);
    }
    private static void FrameCancel()
    {
        int reads = 0, sets = 0; bool cancelled = false;
        string Uri() { reads++; throw new InvalidOperationException("UNEXPECTED_URI_READ"); }
        ulong Navigation() { reads++; throw new InvalidOperationException("UNEXPECTED_NAVIGATION_READ"); }
        var frame = new RuntimeViewNavigationStartingEventArgs(Uri, Navigation, () => { reads++; return cancelled; }, value => { sets++; cancelled = value; });
        frame.Cancel = true; Check(cancelled && sets == 1 && reads == 0);
        Check(frame.Cancel && reads == 1);
    }
    private static void ActiveReads()
    {
        int sourceReads = 0, documentReads = 0;
        var actual = new RuntimeViewSourceChangedEventArgs(() => { sourceReads++; return "https://fixture.autumn.invalid/"; }, () => { documentReads++; return true; });
        Check(sourceReads == 0 && documentReads == 0);
        Check(actual.Source == "https://fixture.autumn.invalid/" && actual.IsNewDocument && sourceReads == 1 && documentReads == 1);
        _ = actual.Source; Check(sourceReads == 2 && documentReads == 1); // No eager or cached native read.
        var failure = new InvalidOperationException("FIXTURE_NATIVE_GETTER_FAILURE");
        string Text() => throw failure; ulong Number() => throw failure; bool Flag() => throw failure;
        var source = new RuntimeViewSourceChangedEventArgs(Text, Flag);
        var navigation = new RuntimeViewNavigationStartingEventArgs(Text, Number, Flag, _ => throw failure);
        var content = new RuntimeViewContentLoadingEventArgs(Number, Flag);
        var completed = new RuntimeViewNavigationCompletedEventArgs(Number, Flag);
        SameFailure(() => _ = source.Source, failure); SameFailure(() => _ = source.IsNewDocument, failure);
        SameFailure(() => _ = navigation.Uri, failure); SameFailure(() => _ = navigation.NavigationId, failure);
        SameFailure(() => _ = navigation.Cancel, failure); SameFailure(() => navigation.Cancel = true, failure);
        SameFailure(() => _ = content.NavigationId, failure); SameFailure(() => _ = content.IsErrorPage, failure);
        SameFailure(() => _ = completed.NavigationId, failure); SameFailure(() => _ = completed.IsSuccess, failure);
    }
    private static void WebMessageTry()
    {
        int calls = 0; bool insideTry = false, caught = false;
        var failure = new InvalidOperationException("FIXTURE_NATIVE_GETTER_FAILURE");
        var sourceFailure = new RuntimeViewWebMessageEventArgs(() => { calls++; Check(insideTry); throw failure; }, () => throw new InvalidOperationException("JSON_MUST_NOT_BE_READ"));
        Check(calls == 0);
        try { insideTry = true; _ = sourceFailure.Source; }
        catch (Exception error) { Check(ReferenceEquals(error, failure)); caught = true; }
        finally { insideTry = false; }
        Check(caught && calls == 1);
        caught = false;
        var jsonFailure = new RuntimeViewWebMessageEventArgs(() => "https://fixture.autumn.invalid/", () => { calls++; Check(insideTry); throw failure; });
        Check(calls == 1);
        try { insideTry = true; _ = jsonFailure.Source; _ = jsonFailure.WebMessageAsJson; }
        catch (Exception error) { Check(ReferenceEquals(error, failure)); caught = true; }
        finally { insideTry = false; }
        Check(caught && calls == 2);
    }
    private static void SameFailure(Action read, Exception expected)
    {
        try { read(); }
        catch (Exception actual) { Check(ReferenceEquals(actual, expected)); return; }
        throw new InvalidOperationException("EXPECTED_ORIGINAL_GETTER_EXCEPTION");
    }
    private static void Check(bool value)
    { if (!value) throw new InvalidOperationException("RUNTIME_VIEW_BACKEND_EVENT_ASSERTION_FAILED"); }
}
