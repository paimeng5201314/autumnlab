# Build04 pending-permission driver review

2026-10-18, static review while Build04 was being prepared. No tracked edits, product run or test result are supplied by this review. Later fixes/results should be kept separately from this snapshot.

Verified source changes:

- `CreateRuntimeServicesCore` passes `session().Instance.Id.Value` directly into `RequestApplicationPermissionAsync`; the permission UI requires that exact foreground instance. The preview-modal exception is bound to that exact instance and current CLI session.
- Developer IPC allows only close/status in the captured preview's own permission modal; other modal operations remain busy. Closed/Crashed preview debug sessions are rejected by `ApplyDeveloperDebugAsync`.
- The fixture registers raw received response IDs before the pending-promise lookup, so unmatched old IDs are visible. Pending permission IDs include the full per-document nonce. The fixture's pending-permission timer is 60 seconds, while the actual gateway timeout remains 30 seconds; a timer cannot be mistaken for successful host cancellation.
- The script observes the real native permission dialog, confirms current foreground debug session, closes it, requires proof-false `response_invalidated`, and creates a different document that receives its own capability response. Scope is correctly limited to canceled pending permission responses, not delayed successful data/profile replies or revoke/epoch races.

Two evidence limitations and one timing issue were sent to the integration owner:

1. **No-success-delivery is not necessarily no-grant-commit.** `permissions_request_ok` is logged only after final native response delivery. Its absence proves that no such success was logged; it does not independently inspect the permission store's internal decision. Name the assertion accordingly, or supply actual permission-state evidence. The preview overlay is disposed on close, so a new overlay being prompt is useful nonleak evidence but not a historical inspection of the discarded overlay.
2. **Positive control should exercise the same pending fixture action.** The earlier normal native refusal gives an existing `permissions.request` response-path control. For an exact counterpart, first use the new pending button in a fresh instance, click Allow, and require the actual matching request ID to return `ok:true/state:granted`; close it, then reopen for the close-while-pending negative case. This needs no new SDK or delay hook.
3. **Modal completion can race the old-session status query.** Close records `closed` and clears `developerPreviewHost` synchronously. Token cancellation queues the ContentDialog Hide and its async finally that clears `aboutOpen`. Between these events, the IPC modal guard can return `DEVELOPER_BUSY` for the now-old session before `ApplyDeveloperDebugAsync` reaches its expiry check. The current generic `Close-IsolationPackage` immediately asserts the next status is expired. Wait for the specific owned dialog to finish cancellation, or tolerate BUSY only as a bounded wait state before requiring `DEVELOPER_PREVIEW_SESSION_EXPIRED`. Never count BUSY or timeout as a pass.

Waiting for a hidden UI alone does not establish the async prompt callback's return. If the report claims that return was observed, add bounded host metadata for the captured instance's prompt-completed event. The present accurate claim can instead stay at “accepted pending gateway canceled and its completion response discarded,” supported by the explicit response-invalidated event.

Build04 is an intermediate diagnostic artifact running controlled fixtures. The separately identified clipboard admission defect remains: a delivered build must reject arbitrary untrusted packages, including developer previews, until an implemented runtime clipboard boundary exists. This review does not approve an arbitrary-preview exception in the final delivery.
