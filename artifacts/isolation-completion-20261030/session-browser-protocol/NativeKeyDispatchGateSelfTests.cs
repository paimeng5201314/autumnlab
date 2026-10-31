namespace AutumnOS.SessionBrowserProtocol;

internal static class NativeKeyDispatchGateSelfTests
{
    internal static void Append(Action<string, Action> Case, string reportPath)
    {
        _ = reportPath; // These cases execute only the actual managed routing gate.
        Case("native_key_capture_surface_allowlist_repeat_and_single_matched_up", () =>
        {
            var gate = new NativeKeyDispatchGate(); int queued = 0; bool Send() { queued++; return true; }
            Require(gate.TryDispatch(true, "surface", "Space", true, false, false, false, Send));
            Require(gate.TryDispatch(true, "surface", "Space", true, false, false, false, Send) && gate.HeldCount == 1);
            Require(gate.TryDispatch(false, "surface", "Space", true, false, false, false, Send) && gate.HeldCount == 0);
            Require(!gate.TryDispatch(false, "surface", "Space", true, false, false, false, Send) && queued == 3);
            Require(gate.TryDispatch(true, "surface", "A", true, false, false, false, Send));
            Require(gate.TryDispatch(false, "surface", "A", true, false, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "F12", true, false, false, false, Send));
        });
        Case("native_key_capture_proxy_idle_navigation_only", () =>
        {
            var gate = new NativeKeyDispatchGate(); int queued = 0; bool Send() { queued++; return true; }
            Require(gate.TryDispatch(true, "proxy", "ArrowLeft", true, false, false, false, Send));
            Require(gate.TryDispatch(false, "proxy", "ArrowLeft", true, false, false, false, Send));
            foreach (string key in new[] { "A", "1", "Space", "Control" }) Require(!gate.TryDispatch(true, "proxy", key, true, false, false, false, Send));
            Require(queued == 2 && gate.HeldCount == 0);
        });
        Case("native_key_capture_active_IME_candidate_letters_and_pending_boundaries_are_not_reinjected", () =>
        {
            var gate = new NativeKeyDispatchGate(); int queued = 0; bool Send() { queued++; return true; }
            foreach (string role in new[] { "surface", "proxy" })
                foreach (string key in new[] { "A", "1", "Space", "Enter", "ArrowLeft" })
                {
                    Require(!gate.TryDispatch(true, role, key, true, false, true, false, Send));
                    Require(!gate.TryDispatch(false, role, key, true, false, true, false, Send));
                }
            Require(queued == 0 && gate.HeldCount == 0);
        });
        Case("native_key_capture_other_focus_unknown_role_notready_and_closing_fail_closed", () =>
        {
            var gate = new NativeKeyDispatchGate(); int queued = 0; bool Send() { queued++; return true; }
            foreach (string role in new[] { "other", "Surface", "surface ", "unknown", "" })
                Require(!gate.TryDispatch(true, role, "ArrowRight", true, false, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "ArrowRight", false, false, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "ArrowRight", true, true, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "Runtime.evaluate", true, false, false, false, Send));
            Require(queued == 0 && gate.HeldCount == 0);
        });
        Case("native_key_capture_only_owned_keyup_survives_focus_change_and_clipboard_blocking", () =>
        {
            var gate = new NativeKeyDispatchGate(); int queued = 0; bool Send() { queued++; return true; }
            Require(!gate.TryDispatch(false, "other", "C", true, false, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "C", true, false, false, true, Send));
            Require(gate.TryDispatch(true, "surface", "C", true, false, false, false, Send));
            Require(!gate.TryDispatch(true, "surface", "C", true, false, false, true, Send));
            Require(!gate.TryDispatch(false, "unknown", "C", true, false, false, true, Send));
            Require(gate.TryDispatch(false, "other", "C", true, false, true, true, Send));
            Require(!gate.TryDispatch(false, "proxy", "C", true, false, false, true, Send));
            Require(queued == 2 && gate.HeldCount == 0);
        });
        Case("native_key_capture_transport_acceptance_controls_down_and_release_ownership", () =>
        {
            var gate = new NativeKeyDispatchGate(); int accepted = 0; bool Send() { accepted++; return true; }
            Require(!gate.TryDispatch(true, "surface", "ArrowRight", true, false, false, false, () => false) && gate.HeldCount == 0);
            Require(!gate.TryDispatch(false, "surface", "ArrowRight", true, false, false, false, Send));
            Require(gate.TryDispatch(true, "surface", "ArrowRight", true, false, false, false, Send));
            Require(!gate.TryDispatch(false, "proxy", "ArrowRight", true, false, false, false, () => false) && gate.HeldCount == 1);
            Require(!gate.TryDispatch(false, "other", "ArrowRight", true, true, false, false, Send) && gate.HeldCount == 1);
            Require(gate.TryDispatch(false, "proxy", "ArrowRight", true, false, false, false, Send));
            Require(accepted == 2 && gate.HeldCount == 0);
        });
    }
    private static void Require(bool value) { if (!value) throw new InvalidDataException("NATIVE_KEY_CAPTURE_SELF_TEST_ASSERTION_FAILED"); }
}
