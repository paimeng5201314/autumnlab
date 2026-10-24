# Build05 candidate host refusal — static review

2026-10-18. No product build or GUI was run by this reviewer. `Test-RuntimeBoundaryUnavailable.ps1` is prepared separately and remains not_run until the integration owner schedules it.

The updated shared host now checks `required_boundaries` after actual package installation/integrity verification and before session services, resource enumeration, profile creation or `new WebView2`. Every developer preview is untrusted regardless of developer mode. Installed external packages cannot pass by app ID, a declared offline mode, report contents, source category or network proof.

The earlier draft would also reject the bundled game because `OnSample` supplies a registered package. Root revised the check without discarding that registry path: content is treated as program-owned only when preview is absent, the host source is exactly `bundled:cn.labchronicles.elementpairs`, and its verified installed package digest and manifest ID/version/entry equal the actual fixed distribution `Samples/element-pairs.autumn`. Inspection holds a read-only FileShare.Read lease on those asset bytes. GitHub/local content is not relabelled, even if it has the same ID or digest. A differing selected version is retained and rejected; it is not silently replaced. This preserves the existing OnSample directory, registration and runtime lease.

Source coverage at this snapshot:

| Entry | Native host construction | Enforcement |
| --- | --- | --- |
| Installed store / desktop / desktop context menu | `StoreApplicationsUi.OpenInstalledApplicationAsync` | Registered package enters the same required-boundaries check. UI's `HasSupportedApplicationSource` is only a source/runtime filter. |
| Element-pairs desktop entry | `MainWindow.OnSample` | Registered bytes must match the actual distribution asset; app ID routing alone cannot bypass the check. |
| Developer UI / official developer CLI preview | `DeveloperPreviewUi.StartDeveloperPreviewHostAsync` | Preview path always fails the required untrusted boundary. |
| Existing host resume / background continuation | Same already constructed host | No second WebView construction path was found. |

The only application `new WebView2` in Shell is in WebAppHost, after this check. No alternate package navigation/renderer constructor was found. This is source coverage, not a claim that every UI/deep-link entry was exercised. Existing deep-link/account activation does not grant an alternative application renderer through the reviewed construction sites.

Failure release is coherent for these sites: StartAsync records the failing stage and clears startInProgress in finally. Developer preview catch calls CloseDeveloperPreview/Dispose; ordinary installed catch removes/disposes the failed host; OnSample catch disposes its host. Dispose closes the placeholder runtime session, clears view/content/resources, then releases runtime and maintenance leases before its closed audit. A cleanup exception retains leases and registers retry, rather than falsely advertising successful release. At this early rejection point, no application service subscription, browser profile/controller or package SDK execution has been created.

The prepared native refusal driver confirms a real package through native UI twice, requires the official CLI's exact missing-boundary error and the matching startup_failed → closed native records, checks no browser/profile/proof/document/script marker, and repeats to exercise release of the failed attempt. It does not claim that clipboard confinement now exists. Its success status is `passed_developer_entry_scope_only`; ordinary local/store/deep-link rejection and first-party game/save regression remain explicit not_run in that report. Reuse `Test-WindowsSmoke.ps1` for the latter on the intended final build.

Normal ChemLeap execution is not solved by this repair: the runtime now refuses the configuration with the demonstrated clipboard gap. The native clipboard counterexample and restricted-token launch failure remain intact.
