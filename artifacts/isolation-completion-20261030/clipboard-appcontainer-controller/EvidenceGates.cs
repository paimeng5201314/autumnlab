namespace AutumnOS.OwnedBrowserParent;

// The runtime and no-native self-tests call these same pure gates. They do
// not create evidence; callers supply facts from held OS handles/protocol.
internal sealed record EvidenceGateFacts
{
    internal bool child_failure { get; init; }
    internal bool observation_gap { get; init; }
    internal bool job_query_failure { get; init; }
    internal bool job_membership_failure { get; init; }
    internal bool original_group_exit { get; init; }
    internal bool host_exit_zero { get; init; }
    internal bool stdout_drained { get; init; }
    internal bool atomic_job { get; init; }
    internal bool job_empty { get; init; }
    internal bool retired { get; init; }
    internal bool job_closed { get; init; }
    internal bool browser { get; init; }
    internal bool browser_normal { get; init; }
    internal bool sdk_empty { get; init; }
    internal bool host_complete { get; init; }
    internal bool preload_verified { get; init; }
    internal bool restricted { get; init; }
    internal TokenFacts?[] restricted_tokens { get; init; } = [];
    internal bool paste_summary_matches { get; init; }
    internal bool paste_performed { get; init; }
    internal bool paste_capture_complete { get; init; }
    internal bool paste_final_snapshot { get; init; }
    internal bool paste_events_expected { get; init; }
    internal bool paste_final_dom_expected { get; init; }
}
internal static class EvidenceGates
{
    internal static void RequireHealthy(EvidenceGateFacts f)
    {
        Require(!f.child_failure, "CHILD_FAILURE_HISTORY_PRESENT");
        Require(!f.observation_gap, "OS_PROCESS_OR_TOKEN_OBSERVATION_GAP");
        Require(!f.job_query_failure && !f.job_membership_failure, "EXACT_JOB_HISTORY_HAS_FAILURE");
    }
    internal static T? TakeHealthyEvent<T>(EvidenceGateFacts facts, Func<T?> take) where T : struct
    { RequireHealthy(facts); return take(); }
    internal static void RequireCompleted(EvidenceGateFacts f, bool final)
    {
        RequireHealthy(f);
        Require(f.original_group_exit && f.host_exit_zero && f.stdout_drained, "FINAL_ORIGINAL_GROUP_ZERO_EXIT_AND_STDIO_PROOF_REQUIRED");
        Require(f.atomic_job && f.job_empty, "FINAL_ATOMIC_JOB_ACTIVE_ZERO_REQUIRED");
        if (final) Require(f.retired && f.job_closed, "FINAL_ORIGINAL_RESOURCES_NOT_RETIRED");
        if (!f.browser) return;
        Require(f.browser_normal && f.sdk_empty && f.host_complete && f.preload_verified, "FINAL_NORMAL_SDK_AND_PRELOAD_TOKEN_PROOF_REQUIRED");
        if (f.restricted) RequireRestrictedTokens(f.restricted_tokens);
        Require(f.paste_summary_matches, "FINAL_PASTE_REALTIME_AND_TERMINAL_SUMMARY_MUST_MATCH");
        if (!f.paste_performed) return;
        Require(f.paste_capture_complete && f.paste_final_snapshot, "FINAL_PASTE_CAPTURE_NOT_COMPLETE");
        Require(f.paste_events_expected && f.paste_final_dom_expected, "FINAL_REALTIME_PASTE_EVENTS_AND_DOM_BOUNDARY_REQUIRED");
    }
    internal static void RequireRestrictedTokens(IReadOnlyList<TokenFacts?> tokens)
    {
        Require(tokens.Count > 1 && tokens.All(t => t is { is_appcontainer: 1, expected_package_sid_matches: true, integrity_rid: <= 4096, capability_count: 0 }),
            "FINAL_ALL_RESTRICTED_BROWSER_RENDERER_UTILITY_OS_TOKENS_REQUIRED");
    }
    internal static void RequireDurableReport(string? error) => Require(error is null, "DURABLE_REPORT_REQUIRED");
    private static void Require(bool condition, string code) { if (!condition) throw new OwnedAssertionException(code); }
}
