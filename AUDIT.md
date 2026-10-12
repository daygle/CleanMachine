# CleanMachine audit — October 2026

## Scope and acceptance criteria

Reviewed the Windows app's service/catalog layer, file deletion and path checks,
registry backup/export/import boundaries, settings/statistics/activity persistence,
background-agent lifecycle, scheduling/process helpers, native UI/tray plumbing,
existing tests, manifests and CI/release workflows. This is a source audit with
focused repairs, **not a certification that all vulnerabilities are absent**.

The app remains WinUI 3/.NET 10, Windows-only, unelevated and per-user. Existing
public service signatures, settings JSON fields, durable data locations, catalog
IDs and Store packaging are preserved. Backups remain `.reg` files, but new names
use a timestamp plus a unique ID. No new runtime dependencies or services were
added. No real user caches, registry data, scheduled tasks or power actions were
operated on in the Linux audit environment. No commit, push or deployment was made.

Safety corrections intentionally change destructive behavior: session/bookmark
recovery is opt-in; Firefox history/download database deletion is refused;
Office pending-upload data and the Windows Media Player library are not temp
files; failed schedules must not trigger shutdown/restart/sleep.

## Findings repaired

| Priority | Finding / failure mode | Repair and coverage |
| --- | --- | --- |
| High | Leaf-only reparse checks allow a normal file reached through a linked ancestor to escape the intended cleanup area. Root enumeration and wildcard discovery can also follow redirected profiles. | Check all existing ancestors, fail closed, reject linked enumeration roots/profiles/wildcard segments, recheck Windows candidates immediately before deleting; scratch symlink tests prove external data survives. Migration uses the same guard. |
| High | Registry cleanup accepts any well-formed backup header, even without provenance or coverage of all findings. | Verify actual recorded hashes/scopes and require coverage of every eligible finding before any deletion. Review preparation uses the complete safety gate, deduplicates findings, and shared Run/RunOnce/Fonts keys require named-value findings. Header-only, wrong-scope and tampered backup tests refuse cleanup. |
| High | Backup names use seconds and sanitized leaf names: same-leaf keys, long paths and overlapping runs can overwrite restore points. The destination probe truncates existing files. | Bounded unique names per native attempt; `CreateNew` probe never truncates existing files. Exact finding scopes replace duplicated special-case export loops. Tests cover 1,000 unique names and preserving existing backup bytes. |
| High | Restore verification and import open the file separately, and restore bypasses the cleanup coordinator. | Serialize restore against in-process cleaners and hold a read-only file lock across hashing/import. Use trusted system `reg.exe`; cancel its child on cancellation. Native import/locking verification remains Windows-only. |
| High | Browser defaults call session/tab restore and bookmark backups non-destructive; automatic defaults can erase recovery data. | Mark both destructive in Chromium and Firefox catalogs. Explicit choices are preserved; unconfigured defaults leave recovery data alone. Updated risk tests and docs. |
| High | Firefox history/download items delete `places.sqlite`, which also contains bookmarks. | Refuse these whole-file operations and report how to use Firefox's own controls. Keep IDs/descriptions for existing selections, never silently delete bookmarks. |
| High | OfficeFileCache can hold unuploaded document edits; CurrentDatabase WMDB is the media library, not a generic temp file. | Remove these targets from the automatically selected app catalog; add regression assertions. Other cache targets remain available. |
| High | Browser-exit clean delays before deletion but never checks whether the browser reopened, including destructive opt-ins. | Recheck selected browsers after obtaining the cleanup gate; exit cleaning never closes browsers. Cache-only Quick Clean/scheduled flows still allow running browsers. |
| Medium | Preference edits bypass Settings exclusions and can delete unrelated Firefox preferences containing matching text. Predictable backup/temp targets risk link redirection. | Respect exclusions and ancestor links, match exact Firefox preference keys, use unique scratch names and reject linked backup targets. Tests check preserved unrelated JSON/preferences and backup bytes. |
| Medium | Removing empty cache directories can delete explicitly excluded empty directories. | Preserve excluded/link directories during bottom-up cleanup. Tested through actual scratch-folder deletion. |
| Medium | App/browser recursive file deletion ignores cancellation inside large trees; browser selection may be enumerated twice with different results. | Check cancellation per file/directory, snapshot browser input once, canonicalize item IDs; regression tests cover cancellation and one-shot enumerable inputs. |
| Medium | Scheduled browser/registry skips disappear from the run result; power actions run unconditionally even after errors. | Collect skips and gate power actions on no reported issues, configured work and no cancellation. Tests cover failed/cancelled/empty schedules. This is deliberately conservative: even a reported locked/recent file blocks a power action. |
| Medium | Process stdout/stderr limits apply only after unbounded `ReadToEndAsync` allocations. Bare helper names can resolve from working directory/PATH. | Capture at most 128 KiB per stream while continuing to drain; pin Windows registry/scheduling/DNS/shutdown helpers to system locations. Test large streams and real child cancellation in the cloud harness. |
| Medium | Background `Task.Run` reads mutable agent/CTS fields after they may have been cleared, causing startup/shutdown races. | Capture owned locals and dispose the external CTS under the agent lock after completion. Native agent/UI runtime verification is pending. |
| High | Uninstall sweeps temporary fallback registry backups even when the user chooses “Keep data”; Recycle Bin auto-empty scans other accessible users' SID directories. | Keep the fallback backup folder unless deletion is requested, reject linked uninstall data roots, and select the current Windows identity's SID for age-based emptying. Source invariants checked; native execution pending. |
| Medium | Tray helper reports success even when `Shell_NotifyIcon` fails: hiding the window can make the app unreachable. | Expose actual tray visibility, return that result before hiding, retain shell recreation failure state, make Dispose idempotent. Requires Windows UI smoke testing. |
| Medium | Persistence loads accept unbounded history/statistics or null records; oversized explicit nullable selections become default-all. Comparers are lost on settings load. | Bound opened streams and retained entries/runs, filter null records, clamp negative totals, saturate additions, restore ordinal-insensitive sets, keep invalid explicit nullable selections empty, deduplicate schedule IDs. Settings IO failures now surface on the Settings page. |
| Low | Startup page flags offline-drive entries as orphaned; unquoted vendor executable paths with spaces and no arguments fail to launch; early process return leaks other enumerated handles. | Reuse Registry Care missing-file policy, recognize existing whole executable paths, check launch result/dispose handles, dispose all polled Process objects. Windows integration checks pending. |
| Medium | CI's two restore invocations can mask the first native failure; package smoke uses extension-sensitive archive extraction for `.msixupload`/`.msixbundle`. | Assert each restore exit status and use ZipFile extraction for the ZIP containers. Windows packaging execution pending. |

Removed unused imports, the obsolete permissive `.txt` backup validation path,
redundant post-allocation process truncation and duplicate registry export routing.
Kept legacy migration/catalog IDs that still serve compatibility; they are not
removed merely because they look old.

## Follow-up repairs for items 1–10

The follow-up pass also addressed the previously listed design items:

1. **Cross-process coordination:** settings, activity, statistics, backup provenance,
   and cleanup read-modify-write operations use per-user interprocess file gates;
   crash releases the OS file lock. Settings merge field/map/schedule edits against
   the latest disk snapshot so an older page does not overwrite unrelated changes.
2. **Stale page settings:** regression tests verify edits from separate snapshots merge
   rather than clobber newer unrelated values. Exact whole-array custom structures
   remain compatibility-sensitive and need the Windows UI suite.
3. **Registry detection false positives:** missing uninstall commands are report-only;
   file existence is considered missing only when positively established, while
   inaccessible/invalid/offline targets fail closed.
4. **Registry restore payloads:** parse and validate every section/value directive
   against the recorded allowed scope before `reg.exe` import, in addition to the
   existing provenance/hash verification.
5. **Cancellation totals:** file and registry mutation paths journal completed work;
   cancellation records partial totals/activity before propagating. Mutations are
   still not transactional and cannot be rolled back.
6. **UI/resource bounds:** backup listing and app detail enumeration use worker tasks;
   browser/file/registry traversals enforce cancellation and entry/depth/time budgets;
   preference reads are size-limited. These are bounded paths, not a global quota on
   every Windows API or individual very large file operation.
7. **Temp root:** cleanup resolves LocalAppData using the Windows known-folder API,
   not process-controlled `TEMP`/`TMP`; its Windows behavior has a native test but
   is not run on this host.
8. **Path-swap race:** Windows cleanup authorizes and deletes entries by native handle,
   holds ancestor handles against rename/reparse replacement, rejects hardlinks and
   unsupported/nonlocal paths, and has native race tests. These tests still require
   Windows CI; non-Windows fallback exists only for portable scratch tests.
9. **Browser-close safety:** user-requested close assistance calls `CloseMainWindow`
   and refuses the clean if browser processes remain; it does not force-kill browser
   processes. Forced termination remains limited to app-owned helper/process
   cancellation or the separate single-instance shutdown path.
10. **Migration/uninstall:** migration now publishes an atomic completion marker
    only after bounded copies succeed; staged files are content-verified, existing
    durable files win, and completed migrations do not re-import stale package-local
    files. A keep-data uninstall refuses to launch package removal unless migration
    completes and returns an explanation when it cannot. The package-removal helper
    itself remains an asynchronous Windows operation; native success/failure behavior
    must be exercised on Windows before release.

## Verification

- Installed .NET SDK 10.0.401 in the cloud account's home; restored existing pinned
  packages without upgrades (there is no checked-in NuGet lockfile).
- Production project build attempted and **blocked** on Linux by Windows
  `XamlCompiler.exe` (`Exec format error`, missing `output.json`). This is not a pass.
- An isolated, ignored `.freebuff/audit-tests` harness compiles **actual non-XAML
  production source and all checked-in test sources** with the project's exact
  pinned packages/Windows reference assemblies. No production dependency stubs,
  new ignores or assertion suppressions were used. Compiler: **0 warnings/errors**.
- Portable tests run via `dotnet test` from that harness. Full Windows test suite,
  WinUI generated source, registry/Task Scheduler/native DLL/UI operations cannot
  be exercised here. **116 tests passed, 0 failed, 0 skipped**, across 11 selected
  portable test classes. All test sources compile in the harness. The normal
  project `dotnet test` was also attempted and blocked by the XAML compiler.
- `dotnet list ... package --vulnerable --include-transitive` checks NuGet's current
  advisory sources; no known vulnerabilities were reported. This does not prove
  absence of unpublished vulnerabilities. No speculative package upgrades made.
- `git diff --check` passed. All app XAML/project/manifest XML and repository YAML
  parsed successfully. Real helper-process tests checked exit/output capture and
  proved cancellation prevented a delayed scratch-file mutation.

Windows follow-up commands (the existing CI also exercises native scheduling):

```powershell
dotnet restore CleanMachine.Windows/CleanMachine.Windows.csproj
dotnet restore CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj
dotnet build CleanMachine.Windows/CleanMachine.Windows.csproj -c Release -p:Platform=x64 --no-restore
dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -c Release -p:Platform=x64 --no-restore
```

Cloud validation logs and disposable harness/benchmark programs are retained under
ignored `.freebuff/`. They are not production artifacts or a replacement for the
normal Windows project/test interfaces.

### Measured allocation refactor

10,000 scratch cache files, 128 bytes each; nine measured iterations after warmup,
Release build on this Linux VM, same final enumeration guard in both paths:

| Implementation | Verified totals | Median time | Managed allocation |
| --- | --- | --- | --- |
| Previous array-materialization sizing loop | 10,000 files / 1,280,000 bytes | 82.88 ms | 4,001,360 bytes |
| Final production streaming sizing loop | 10,000 files / 1,280,000 bytes | 78.96 ms | 3,921,360 bytes |

Streaming avoids retaining the path array (80,000 measured allocated bytes saved).
The timing difference is small and storage-dependent; this is **not** a general
Windows speedup claim. Ancestor safety checks add filesystem work; their whole-app
cost was not benchmarked on Windows. Browser UI drill-down remains lazy/capped.

## Remaining risks and checks

1. **Windows acceptance testing required:** run both existing Windows CI jobs;
   exercise cleanup confirmation/cancel, exclusions and junctions, real registry
   export → delete → restore, multi-scope coverage, scheduled tasks, browser reopen,
   tray failure/recreation, startup/shutdown and MSIX uninstall/keep-data behavior.
   This Linux host cannot validate native handle semantics, registry APIs, WinUI, or
   actual Store package removal.
2. **Transactional behavior:** cancellation reporting records completed changes, but
   deletes/imported registry changes are not rolled back. A process crash between a
   mutation and journal persistence can still leave activity/statistics incomplete.
3. **Registry heuristics and resource boundaries:** Windows registry APIs may report
   access failures ambiguously; heuristic scans remain conservative but not infallible.
   Entry/depth/time budgets cover major walkers, not every platform call or a single
   unusually large file/hash operation. Backup hashing is asynchronous but remains
   proportional to the input size.
4. **Handle-based deletion limits:** native code and race tests compile but have not
   run on Windows in this session. Same-user code with equivalent rights can still
   modify the data or invoke the application; this is not an isolation boundary.
5. **MSIX removal helper:** helper launch can be checked before app exit, but the
   deferred PowerShell package-removal result occurs after the initiating process
   closes. Confirm retry/error behavior and preserved data on a real packaged install.
6. **Release signature checks:** verification checks the outer upload container,
   not every inner package signature. No signing/deployment operation was performed.
## Sources for data-safety corrections

- [Mozilla Places documentation](https://firefox-source-docs.mozilla.org/browser/places/index.html)
  and [backup resources](https://firefox-source-docs.mozilla.org/browser/components/backup/docs/backup-resources.html).
- [Chromium SessionService source](https://chromium.googlesource.com/chromium/src/+/b9c46de8f28afac43e46c37b48bf1d1378823f33/chrome/browser/sessions/session_service.h).
- [Microsoft Office document cache guidance](https://support.microsoft.com/en-au/office/collab-files/managing-office-document-cache-size).
- [Microsoft Windows Media Player library documentation](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/wmp/about-the-library).
- [libyal Recycle Bin format specification](https://github.com/libyal/dtformats/blob/main/documentation/Windows%20Recycle.Bin%20file%20formats.asciidoc): v1/v2 use eight-byte version and deletion FILETIME at offset 16; unknown formats now fail closed.
- [NuGet package auditing](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).
