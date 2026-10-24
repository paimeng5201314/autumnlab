# Native isolation driver review — 2026-10-18

Scope: read-only review of the current local product and driver. No tracked source, test assertion, product data or policy was changed by this review. No new product/UAC/clipboard run was performed. Build03 results must be recorded separately; the observations below are not a pass for that build.

## Navigation: preserve the failure, test the repaired contract

Actual evidence is `../navigation-cancel-01/report.json` and `RESULT.md`. The fixed 154.0.4258.53 engine was tested with five separate browsers: unrestricted positive control, production response headers plus Cancel, plus error-page disabled, plus Stop, and both APIs. All four policy cases delivered no target request to the owned receiver, but all replaced the old document. The native source became the rejected target and the renderer document became `chrome-error://chromewebdata/`. Disabling the built-in page only removed its body. `Stop()` did not preserve the old document. The unrestricted browser positive and the host controls reached the receiver.

`../native-08/runtime-isolation-native.json` remains a failed historical product run: build `T06-20261018-isolation-02`, 13 recorded checks passed, followed by loss of the observation document at external top navigation. That result must not be rewritten. Its later network, permission-dialog, close, account and storage phases are not_run.

Root's current `scripts/Test-RuntimeIsolationNative.ps1:283`–304 correctly puts external HTTP top navigation in a new final instance. The required new contract is:

1. The fresh instance first completes the real challenge, loads its fixture, runs SDK capability lookup, and has a known document nonce and unsaved marker.
2. The actual click produces `navigation_denied` for that exact instance.
3. `document_binding_lost` or `error_document_blocked` is recorded with network proof false; a subsequent `closed` has proof false and no maintenance lease.
4. The old CLI session returns `DEVELOPER_PREVIEW_SESSION_EXPIRED`; there is only the original successful package `navigation_completed`, and no `resource_release_failed`.
5. Every owned receiver's connection/datagram/request count remains at baseline, and end-of-run positive controls still succeed.

Do not accept a missing observation element, a timeout, a vanished window, or a generic exception as this result. The supported outcome is close/revoke; retention of the previous unsaved document is not claimed.

The existing popup, blob download, unregistered protocol and CSP form tests at script lines 196–208 must retain `Assert-IsolationPreserved`. `navigation-cancel-01` tested only external HTTP top navigation: it does not prove whether blob download or external-scheme navigation also commits an engine error page. The fixture's download is a `blob:` link with a download attribute (`probe.js:332`); the protocol uses a unique unregistered scheme (`:338`). Neither is equivalent to the tested HTTP target.

If either produces an error document in Build03, preserve that failed assertion and create its own instance/test case. Capture native event order, actual source and renderer document identity in an independent artifact probe, then require an explicit close/revoke contract only for that evidenced operation. Do not globally permit document loss in `Assert-IsolationPreserved`, and do not suppress a download/protocol assertion to let later tests run. A separate per-operation runner can collect later cases while still retaining failed status.

The random unregistered scheme proves native cancellation of that particular attempted scheme. It does not prove that an installed external handler was prevented from starting. Keep the report's existing limited scope. Do not register handlers or invoke unrelated applications merely to improve this report.

## SDK coverage: what the current real driver establishes

| Case | Existing path | Actual scope / remaining limit |
| --- | --- | --- |
| Undeclared permission, app identity forgery, bad protocol, unknown native method, replay | `Test-RuntimeIsolationNative.ps1:185`–191; fixture `probe.js:228`–251 | Real WebView-to-host gateway requests. native-08 reached this phase. Does not itself test a frame-sourced message or late response. |
| User refuses declared save permission | same driver `:239`–249 | Real native permission dialog; repeat remains denied and save is rejected. native-08 did not reach it. This is initial denial, not revoke-after-grant. |
| Revoke already granted permission | `Test-T03FeaturesSmoke.ps1:162`–194 | Existing driver grants notification permission, uses it, revokes through native settings, then a new renderer request receives `PERMISSION_REVOKED`. This is an existing test design; it must be run on the intended build for new evidence. It does not hold a request across revocation. |
| Controlled account change | `Test-T06DeveloperSmoke.ps1:136`–162; isolation driver `:233`–238 | Real preview close/reopen, old CLI session expiry, old host resources closed before new navigation, separate profile namespaces and browser markers. Explicit test identities, not real Logto. Does not keep a known SDK response pending across epoch change. |
| Close after a request was sent | isolation driver `:250`–252; fixture `probe.js:340`–347 | Fixture fires `lifecycle.getState` every 50 ms. Driver waits only for `sent > 0`, then closes and checks old CLI session expiry. The request may already have replied. No entered/pending barrier, no correlated post-close response observation. |
| Top-document replacement | new isolation driver `:283`–304 | New product guard will be tested for proof revocation, close and old debug-session expiry. Does not by itself prove rejection of a known delayed SDK reply. |

The current field `close_inflight_readonly='passed_native_close_and_expired_session'` should not be described as a proven in-flight response race. Suggested honest labels:

```text
close_after_sdk_request = passed_native_close_and_expired_session
known_pending_sdk_response_at_close = not_run
late_sdk_response_after_close = not_run
late_sdk_response_after_permission_revocation = not_run
late_sdk_response_after_account_epoch_change = not_run
late_sdk_response_after_document_replacement = not_run
native_frame_sdk_forgery = not_run
```

These are reporting corrections, not removal of checks. Keep the current actual close/session-expiry assertions.

## What only the C# tests currently prove

`tests/AutumnOS.Tests/T03RuntimeTests.cs:152` and `:160` hold test profile tasks and revoke permission/change a synthetic current-account predicate before completion. `:185` changes a test identity epoch and checks session/event shutdown. `:285`–337 check final response revisions and usage/identity leases, including revoke/regrant. `:352` holds a test data task; `:370` holds a queued write before its commit lease. `RuntimeIsolationSessionTests.cs:49`–85 checks completed legacy responses after close/save revocation. These are useful deterministic component tests, but their post callbacks are delegates, not `CoreWebView2.PostWebMessageAsJson` reaching a real renderer.

Current production final-delivery points are `src/AutumnOS.Shell/WebAppHost.cs:285`–305 and `src/AutumnOS.Runtime/RuntimeSession.Managed.cs:61`–146. The WebView handler checks native source/current document/session before and after await, then uses `DeliverResponse`; the latter revalidates permission/epoch/revision under final leases. Source review is not native delivery evidence.

## Minimal deterministic addition; proposed, not executed

Use a new harmless fixture and independent data. Its response recorder must log every raw bridge response (including unknown request IDs) and unsolicited event, rather than recording only currently pending promises. The current fixture drops an unknown late `requestId` and records only event names, so it cannot prove a new page did not receive an old payload.

For each of permission revoke, close/reopen, controlled account switch and forbidden-navigation replacement:

1. Positive control: start a real supported SDK read of a synthetic marker, prove the host accepted this request by correlation ID and prove the actual renderer receives the marker on release. An arbitrary timeout is not an accepted-request barrier.
2. Negative: independently start the same request, record it as definitely pending after the host has captured the originating instance/account/revision. Perform the real native transition. Record its committed native event before releasing the response.
3. Finish the underlying operation, then prove an explicit host outcome: permission error or suppressed expired delivery. For close/reopen and account change, verify the new real page receives its own positive ping/result, has a new nonce/session, and receives neither the old synthetic marker nor old request response. Observe the old operation's completion, not merely a period of silence.
4. For queued writes also verify actual isolated bytes were not committed after the revoke boundary. For subscriptions, stimulate a real owned host event after old session teardown and check the new recorder and host subscription disposition.

No existing product action found in this review provides a deterministic safe hold/release barrier for a read after acceptance. Repeating fast calls and racing sleeps is insufficient. A component harness can use the existing `RuntimeSessionServices` hooks with a controlled task and a real WebView to test native delivery, but must be labelled integration-harness evidence, not final delivered-EXE proof. If production-path diagnostics are added later, keep them test-scoped, metadata-only and unable to authorize capabilities or provide a third-party delay/bypass API. Do not block the UI dispatcher or use a test that holds the revocation thread itself.

Build03 can accurately demonstrate document-binding closure and the existing actual SDK cases without claiming these outstanding deterministic late-delivery cases passed. The clipboard EmptyClipboard counterexample remains an independent unresolved boundary and is not affected by this navigation review.
