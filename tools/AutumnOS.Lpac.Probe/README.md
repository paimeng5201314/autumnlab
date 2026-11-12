# LPAC process and clipboard-handle mechanism probe

制作人：派蒙。This is an independent diagnostic console tool, not a product host or an application admission path. Product third-party admission remains unchanged.

The tool creates one unique, temporary AppContainer profile and runs only its own staged child in three sequential cases:

| Case | Actual required token | Expected WinSta0 clipboard handle access |
| --- | --- | --- |
| A | Ordinary, unfiltered Medium primary token | Explicit `OpenWindowStation(4)` allowed with bit 4 read back, positive control |
| B | LPAC, no capabilities | Denied with `ERROR_ACCESS_DENIED`; existing process-station handle must also lack access bit 4 |
| C | LPAC, only `lpacClipboard` | Explicit `OpenWindowStation(4)` allowed with bit 4 read back, capability positive control |

The parent checks actual child PID, nonce, user identity hash, session, elevation, integrity, AppContainer SID, LPAC status and exact capability SID list. Native process creation success alone is not a pass. `0xC0000142`, nonzero exit, no metadata, token mismatch, missing positive controls and timeouts remain failures. A must complete before B/C. A failed B may be followed by C only when B has actually exited; any live child stops the sequence.

Token class 29 is queried first. Classes 31 (AppContainer SID) and 46 (LPAC) are queried only for an actual AppContainer. For an ordinary token, `is_lpac: false` and a null AppContainer SID are derived from non-applicability; the separate `*_query` fields explicitly say `not_applicable_non_appcontainer`. B/C require an actual successful class 46 query yielding true; unsupported/error 87 is never converted to a false value or a pass. Size/query errors include the failing numeric token class. Capability class 30 is independently queried, including the valid count-only empty buffer.

Fixed classes 8/12/20/29/46 use a direct four-byte-buffer query rather than a null, zero-length size probe. The actual call must succeed and its returned length must fit that allocation. Boolean classes are parsed only from a legal one-byte or four-byte result containing exactly 0 or 1; type/session DWORD results require four bytes. For variable-sized classes, the zero-length probe accepts either `ERROR_INSUFFICIENT_BUFFER` (122) or `ERROR_BAD_LENGTH` (24) only when the returned required length is positive and bounded, followed by a required successful real query.

If the actual Win32 class 46 query fails, the same token is additionally queried through system `NtQueryInformationToken`, using fixed four-byte buffers for class 29 as a positive control and class 46 as the actual LPAC query. `lpac_diagnostic` preserves the original Win32 error and each raw NTSTATUS, returned length and safely readable value. Only status zero, valid one/four-byte 0/1 values, and class 29 equal to 1 permit use of the real class 46 kernel value. Its source is explicitly recorded as `NtQueryInformationToken`; there is no token-flag inference or AccessCheck substitute. If that query is unavailable or its control fails, `is_lpac` is null and the remaining token/station facts are still returned, but the parent strictly fails the case. The mere presence of metadata never counts as a pass.

A nonzero owned-child exit includes a separate `controlled_child_diagnostic`: at most 4 KiB of UTF-8 output after exact known stage/workspace/Windows/user-profile prefixes and SID-shaped values are replaced. It also records the formatted exit code, bounded HRESULT-formatted tokens and whether output mentions hostfxr, coreclr or hostpolicy. Original output length/hash remain available. This narrowly scoped tool runs only its own fixed console child with a controlled environment and no account/third-party inputs; this diagnostic collector must not be reused for arbitrary processes or product logs.

The current process-station handle's actual granted mask is recorded separately. A/C do not require that already-associated handle to contain bit 4: Windows does not guarantee the access mask with which it was originally opened. B conservatively requires both a denied new clipboard-access handle and no bit 4 in its existing process-station handle. These handle checks still do not establish complete clipboard isolation.

No `OpenClipboard`, `EmptyClipboard`, clipboard content read/write, WebView, external application, UAC, desktop switch, existing window-station/desktop ACL change, network capability, privilege adjustment or sandbox-disabling argument is used. The tool only opens and queries WinSta0 handles. Handle metadata is not full clipboard acceptance: it does not test all broker, inherited or browser paths.

Each run copies the executable and matching .NET runtime into a newly named `lpac-stage-*` next to the report. Only that new copy receives the unique AppContainer SID's read/execute grant; its separate temp directory receives write access. The SDK and existing worktree ACLs remain unchanged. The controlled child environment excludes inherited credentials, proxies, CLR profilers and startup hooks. Only the two new pipe endpoints are inherited. Output collection is bounded. The unique profile is deleted only after all owned children have naturally exited. A timeout retains the profile and stage and reports `pending_live_child`; no process is force-terminated. Staged bytes remain as evidence.

Trusted app/.NET code and their stage directories carry a **Medium, no-write-up** mandatory label. The unique AppContainer SID retains only read/execute there; only its separate temp directory is **Low, no-write-up** with write access. This fixes a verified staging defect: on the current machine, the same staged apphost failed with `CurrentHostFindFailure` (`0x80008085`) at Low, but a label-only change to Medium let it enter .NET. The control preserved binary bytes and the DACL and added no SYSTEM ACE (`artifacts/isolation-20261103/staged-medium-no-system-control.json`). The separate SYSTEM-grant experiment did not fix the failure. No SYSTEM grant or change to an existing path, window station or desktop is part of this staging policy. This correction is not LPAC/browser/clipboard acceptance.

The child environment is an explicit whitelist, not the inherited environment. Windows/known-folder values supply `SystemRoot`, `WINDIR`, `SystemDrive`, `USERPROFILE`, `LOCALAPPDATA`, `APPDATA`, `ProgramData`/`ALLUSERSPROFILE`, Program Files and Common Program Files variants. `PATH` contains only Windows System32 and Windows; `ComSpec` is the Windows System32 command interpreter path. Fixed OS/architecture identifiers are included. .NET roots and temp remain inside the new owned stage. Missing required values fail before launch; no credential, proxy, CLR profiler or startup-hook values are copied. The report includes only names and presence, never the personal folder values. These environment strings provide runtime configuration, not filesystem, registry, network or clipboard permissions. The whitelist addresses the observed AppContainer `CreateProcess` error 203, but a new successful launch must be demonstrated before that cause is considered resolved.

Build with the project-local SDK, without adding this project to the product solution:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/nuget/packages'
& ./.tools/dotnet/dotnet.exe build ./tools/AutumnOS.Lpac.Probe/AutumnOS.Lpac.Probe.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true
```

After source review, execute from an ordinary, unfiltered, non-elevated Medium process, using a new report path:

```powershell
& ./tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe --report ./artifacts/isolation-20261103/lpac-native-01/report.json
```

The default agent shell may itself have a restricted token. Its result cannot stand in for an ordinary-user positive control. The parent rejects elevated, AppContainer and already restricted execution environments and records its actual token. Never weaken that check just to obtain a pass.

For an explicitly synthetic LUA Medium diagnostic launch, append `--baseline synthetic-lua-medium`. This is caller-supplied provenance only; it changes no token, capability, permission, control or lifecycle requirement. The default report label is `ordinary-user`; the synthetic label must be selected explicitly, with no automatic fallback. Both modes record `real_user_desktop_acceptance: "not_run"`. A synthetic token result must not be described as a real ordinary-user desktop acceptance result.

```powershell
& ./tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe --report ./artifacts/isolation-20261103/lpac-synthetic-01/report.json --baseline synthetic-lua-medium
```

All browser compatibility, real clipboard copy/cut/paste and complete product isolation fields remain `not_run`, even if all three mechanism cases pass. This tool has not been executed by its authoring agent.

Official implementation references:

- [Chromium LPAC design](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/docs/design/sandbox.md#Less-Privileged-App-Container_LPAC): LPAC's capability distinctions include `lpacClipboard`.
- [Chromium process startup helper](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/sandbox/win/src/startup_information_helper.cc): LPAC requires both security capabilities and the all-application-packages opt-out process attribute.
- [Microsoft process attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute): attribute memory lifetime and explicit handle inheritance.
- [Microsoft token information classes](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class): actual AppContainer, capabilities and LPAC queries.
- [Microsoft capability SID derivation](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-derivecapabilitysidsfromname): returned array and SID ownership.
- [Microsoft environment blocks](https://learn.microsoft.com/en-us/windows/win32/procthread/changing-environment-variables): case-insensitive sorted Unicode entries and double-null termination.
- [Microsoft user environment](https://learn.microsoft.com/en-us/windows/win32/api/userenv/nf-userenv-createenvironmentblock): user-profile environment values depend on the loaded user profile. This tool does not load another profile or inherit an entire generated block.
