# Third-party admission after the clipboard counterexample

2026-10-18, read-only source review. No tracked source was changed and no product or browser was run by this review. This document is a repair recommendation, not an editable admission certificate.

## Current source does not fail closed for the known gap

- `src/AutumnOS.Shell/StoreApplicationsUi.cs:25` admits web packages with github, local or bundled source; the controlled test path is a further entry. Desktop and store subsequently share `OpenInstalledApplicationAsync`.
- `src/AutumnOS.Shell/WebAppHost.cs:99`–145 verifies/installs the package, creates its runtime session and loads its resource bytes. The later native initialization verifies exact engine/profile, settings and the network startup challenge. It never requires a working clipboard boundary before navigating to the third-party entry page.
- `src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs:24`–27 denies asynchronous clipboard APIs via Permissions Policy but explicitly excludes legacy editing from its certification. This comment is accurate; it is not a runtime restriction.
- `RuntimeIsolationProof` attests the native network challenge, not all browser capabilities. `networkSandboxVerified` must not be used as a whole-runtime authorization proof.
- The actual UAC02 private-station A/B showed that Job UI mask 6 still permits B copy/cut to clear the synthetic clipboard. Therefore adding that Job to the host would not supply the missing boundary. Restricted-token launch B also did not complete initialization, so it supplies no substitute boundary.

Consequently, the present local candidate can run an ordinary third-party page while a prohibited legacy editing surface remains reachable. A manifest that omits clipboard permission, a source classification, developer confirmation or a passing network challenge does not remove this browser capability.

## Minimum compliant fail-closed repair

Enforce the required capability configuration in the shared native host, before any third-party document/script/resource can execute. Put the check after actual package identity/integrity is established and before constructing a WebView or navigating a package page. A UI-level precheck may display the same reason, but it must not be the enforcement point.

The current implementation has no valid clipboard-confined untrusted-web configuration. Therefore the production untrusted-web configuration must reject startup with a concrete error such as `RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE`. This is a check of missing runtime implementation, not of a report file, editable progress status, package `sandboxVerified` field, developer flag or manual test waiver.

The requirement applies to every untrusted web document, even packages that declare no clipboard capability. Apply it to GitHub, local installation, arbitrary developer previews and any future deep link that constructs this host. Do not leave `IsDeveloperPreview` or controlled-store mode as a bypass in the delivered application. Preserve install/update/data management; reject execution without rewriting or deleting the package or saves.

In the current revision a direct failure for the unsupported configuration is sufficient. Do not add a public `clipboardVerified` Boolean or a setting that callers/apps can set. A future working boundary must be created by the host from actual browser/process resources and checked for the current instance, process identity, engine/policy version and lifetime. Only then may that configuration supply the native requirement. There is no supported configuration today merely because a prototype exists.

Preserve the existing first-party product game through its actual host-owned provenance, not by accepting arbitrary source text `bundled`, a matching appId, a PM/SQ category or a third-party recognition list. If introducing an explicit distinction is necessary, bind it to the real shipped product asset and verified installed bytes. `OpenInstalledApplicationAsync` routes the sample appId to `OnSample`, so an appId-only bypass would be the wrong layer. Current installer source-conflict checks should remain intact; never reclassify ChemLeap or another external package to enter this path.

The source and tests may continue investigation in a separately identified diagnostic artifact. A generic arbitrary-package preview exception in the final executable would recreate the rejected developer-mode workaround. Intermediate experimental builds and their reports must not be represented as the fixed delivery.

## Evidence for the repaired rejection

Use a harmless third-party fixture from ordinary install/desktop/store entry and a different source identity. Assert the concrete clipboard-boundary error, no package script/SDK marker, no package `navigation_completed`, and complete release of the attempted instance/maintenance lease. Test a normal developer preview of the same fixture also rejects; package integrity and source-conflict failures still retain their original distinctions. If the check occurs before controller creation, its absence is a useful additional event assertion.

Keep normal game/save regression on the actual first-party asset and verify existing third-party installation/data remains intact. Preserve prior network positives/negatives and clipboard failures; do not replace the old clipboard assertion with “app blocked” and declare the boundary implemented.

Report the two outcomes separately: rejection of an unsupported unsafe runtime is a safety fix; normal ChemLeap launch remains blocked by the concrete clipboard boundary until an actual working configuration is implemented and validated. Neither this review nor that rejection completes T01 or the user's requested ordinary third-party launch outcome.
