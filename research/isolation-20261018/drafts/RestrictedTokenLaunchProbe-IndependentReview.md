# RestrictedTokenLaunchProbe — independent static review

Completed 2026-10-18 after the integration owner had already received authorization and executed `restricted-token-launch-01`. This is not a claimed pre-run approval or an additional execution. Reviewer did not launch the candidate, create a token/process, change station/desktop association, touch a clipboard or modify the candidate.

Reviewed `RestrictedTokenLaunchProbe/Program.cs`, SHA-256 `1c03b831a49e5f0fb2383e9785d35fcdd116bf55cca1ee0b1c2301217f614f9d`, plus its REVIEW.md and the retained actual report `../restricted-token-launch-01/report.json`.

## Conclusion

The fixed source stays within the stated narrow mechanism experiment. No hidden elevation, credential login, token impersonation, global ACL/policy mutation, station switching, clipboard call, WebView launch, breakaway or forced process termination was found. The actual failed B case must remain failed and is not clipboard-isolation evidence.

The principal token and launch checks are appropriate for this scope:

- Parent and child require Medium, non-elevated primary tokens; the child reports actual token SID hash, session, restriction SID and enabled privileges. Parent compares the reported facts against its own captured token and the actual native child PID.
- The original token handle requests QUERY, DUPLICATE and ASSIGN_PRIMARY. `CreateRestrictedToken` keeps handle access/type; restricted SID attributes are zero, fixed SID is `S-1-5-12`, and policy is exactly `DISABLE_MAX_PRIVILEGE`. Existing restriction-list intersections fail the expected-list check rather than silently relaxing the token. These requirements match [CreateRestrictedToken](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-createrestrictedtoken).
- The explicit application name is the current compiled apphost; command-line quoting handles backslashes/quotes. No alternate binary or shell is selected. `STARTUPINFOEX` and `EXTENDED_STARTUPINFO_PRESENT` agree. Only the two new anonymous-pipe child endpoints are listed as inheritable; there is no token/station/desktop handle in the list. This agrees with the [HANDLE_LIST contract](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).
- Native launch success is separate from actual CLR metadata completion, identity checks and natural process exit. A fails closed before B; B errors are recorded without retry/fallback. Exact PID/process handle is retained through the wait; process/thread and pipe handles are disposed without terminating a live child.
- The station operations only open/query/close handles with requested masks 2 and 4. Their presence is not treated as content access or proof that an already inherited handle or another broker cannot bypass a future restriction.

`CreateProcessAsUser` can temporarily enable necessary privileges already present for the call; the source does not explicitly adjust the caller token, acquire credentials or elevate. Own restricted primary tokens have a documented assign-primary exception, but that alone does not guarantee initialization. The API returns before initialization finishes and explicitly directs callers to inspect termination status after a required DLL fails. See [CreateProcessAsUserW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw).

## Actual result reviewed, not rerun

On Windows 10.0.26200, A completed its managed metadata and natural exit. Parent-side restricted token inspection passed. B returned native creation success for PID 24008, then naturally exited `-1073741502` (`0xC0000142`) with no JSON/child metadata. The failed result cannot identify which DLL, runtime initialization or station/desktop access caused termination. In particular, no B-side station access observation exists. No browser or clipboard acceptance test was reached.

The report's `JsonReaderException` is a secondary parse failure after the already recorded abnormal exit; it is not the root native failure. Preserve the original report. For a separately authorized future revision, diagnostics could classify nonzero exit plus empty output as `child_terminated_without_metadata` before JSON parsing, and distinguish WAIT_FAILED from WAIT_TIMEOUT with captured native error. These improve attribution only; they do not justify a rerun or weakening the restriction.

The fixed, trusted child is the only code launched. Because output is read to completion before its size limit is checked, this collector is not an untrusted-process output sandbox and should not be reused as one without bounded streaming. This does not expand the present controlled-child experiment.

No blocking scope violation was found in this reviewed source. There is also no demonstrated viable restricted WebView host: B did not complete metadata, and all browser, GUI, clipboard and application-compatibility items remain not_run.
