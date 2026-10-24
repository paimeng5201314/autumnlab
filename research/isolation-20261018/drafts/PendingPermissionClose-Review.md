# Real pending-permission close probe — proposed design, not executed

2026-10-18. Static review only. Reuses the actual product permission dialog, developer preview IPC, WebAppHost and SDK gateway. No fake bridge, invented SDK method or arbitrary delay is required. A product build and actual positive/negative runs are still required.

## A real accepted-request barrier exists

`RuntimeSession.HandleMessageAsync` validates and admits the request, serializes its handling, then calls managed `permissions.request`. `PermissionService.RequestAsync` / `DeveloperPreviewPermissions.RequestAsync` reserve the permission, create the native prompt and await its result with the linked request/session cancellation token. Thus a visible native permission ContentDialog for the known fixture, app source and permission proves this request reached a pending operation. It is stronger than the existing `closeQueue.sent > 0` observation.

`RuntimeServicesUi.RequestApplicationPermissionAsync` registers the request token to hide the dialog, and returns false when canceled. Closing the preview cancels the runtime session before the response can be posted. That gives a genuine pending-permission cancellation test, not a test of an already computed successful profile/save response crossing a revocation boundary.

The original DeveloperPipe guard rejected every command while `aboutOpen`; it would return DEVELOPER_BUSY at this barrier. Root is implementing a narrow exception for only `status` and `close`, the exact current preview session and the dialog's owning instance. All account switching, permission mutation, preview launch and unrelated modal management must remain busy. Prefer carrying the originating runtime instance into the prompt callback directly from `CreateRuntimeServicesCore`'s captured `session()` function, rather than reconstructing ownership from app ID/source/foreground state. Closing a matching debug session is an existing developer capability; the exception must not authorize any package or change its isolation policy.

## Fixture and receiver

Use a separate harmless package with only `saves` declared, fresh isolated data and the real standard SDK envelope. Register the raw `chrome.webview` message listener immediately in the fixture script. Record all response IDs and status/error metadata, including IDs absent from its pending map; the current general isolation fixture drops unknown late response IDs.

Generate a fresh document nonce and include it in each request ID, e.g. `perm_<32hexnonce>_1`, within the actual 64-character ID limit. Do not reuse the current fixture's token-plus-sequence IDs across documents, since a reopened document otherwise starts the same sequence. No user files, identity profile or secret payload is needed.

The UI observation should expose `documentNonce`, sent IDs, all raw response IDs, request completion state and capabilities. It must remain a normal fixture UI accessible through the current native helper. A missing observation field or promise timeout is not a pass.

## Sequential A/B and reopened-page control

1. **A positive**: fresh preview → actual host isolation challenge succeeds → fixture capability lookup succeeds → click its permission button → exact native permission dialog visible → user-input helper clicks Allow → renderer records `ok:true`, `state:granted` for that document's request ID. This proves the complete permission request and real native response path. Close A normally.
2. **B pending negative**: open a new preview of the same package; the preview overlay permissions are fresh. Verify initial query is prompt. Click the same request button and observe the exact dialog title/source plus a host/fixture request ID. Do not click Allow or Refuse. While the dialog is still present, issue official CLI `status` for this exact preview and confirm it is the captured live instance, then CLI `close`.
3. Require real `closed` event with no maintenance lease, network proof false, the old debug ID expired, and the permission dialog gone because its token was canceled. Root's proposed `response_invalidated` event after the awaited gateway returns can show the host actually discarded the old response. Correlate it to this instance and use a fixture with only one outstanding request, or add bounded correlation metadata. A generic event from unrelated traffic must not count.
4. Reopen fresh C only after the old dialog has completed cancellation and resources are released. The new document nonce/session/origin must differ. Its own capability query must reach the renderer; permission query must be prompt, not old granted state. Request permission again and explicitly accept as a new positive control. The C raw-message log must contain neither B's request ID nor an old permission response. Do not treat absence alone as enough: also require B's explicit host completion/invalidation and C's positive response.

Use the actual product event and CLI results; never catch timeout or DEVELOPER_BUSY and relabel it as isolated. Retain both A and B failure detail and screenshots if any prerequisite fails. Wait for native modal closure before reopening because `aboutOpen` is cleared in the prompt callback's finally, not synchronously by the CLI close response.

## Exact conclusion permitted

If the full A/B/C sequence passes, it establishes that a real accepted pending permission request is canceled on host close, its old session response is discarded, the old debug handle is invalidated, and a reopened real page receives only its own request results. It does not by itself establish delayed successful storage/profile payload rejection, revoke/regrant while such a payload is computed, or an account-epoch race. Those distinct cases remain not_run unless separately executed.

The dialog callback may be canceled and the gateway's `WaitAsync` may complete before the dialog continuation finishes. Therefore `response_invalidated` alone is not proof that the dialog callback returned; require actual modal closure and, if the report claims callback completion, a bounded native prompt-completed event bound to the captured instance. Do not fabricate a delayed successful callback or change production cancellation semantics solely to create a test.
