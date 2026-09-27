# Releasing CleanMachine

CleanMachine is distributed **only through the Microsoft Store**. There is no
self-signed sideload package, no `.exe` installer and no in-app updater - the
Store installs, updates and signs the app.

The submission package is built by the [`Release Windows app`](.github/workflows/release-windows.yml)
workflow, which triggers on pushed tags matching `v*.*.*` (e.g. `v1.0.6`). The
tag is the single source of truth for the version: the workflow stamps the
`Package.appxmanifest` identity version and `app.manifest` assembly version from it.

## You do not need to buy a code-signing certificate

This is the most common misconception about Store submission, so it is worth
being explicit:

> **Partner Center accepts an unsigned `.msixupload` and signs it with
> Microsoft's own certificate as part of ingestion.**

There is therefore no need to purchase a commercial (OV/EV) code-signing
certificate, and no need for a paid artifact-signing subscription, in order to
publish on the Store. The workflow builds an **unsigned** package for exactly
this reason, and asserts that the result really is unsigned before uploading it.
The only money involved is the one-off Partner Center developer account fee,
which Microsoft waives for individual and company accounts.

There is no optional self-signing path. The retired sideload channel it served
no longer exists, and keeping it would only invite the question of why a Store
submission needs a certificate at all.

## Prerequisites

1. **Reserve an app identity in Partner Center** (Product → Identity) and set three
   repository **variables** (Settings → Secrets and variables → Actions →
   Variables — not the Secrets tab) from that page:

   | Variable | Value |
   |---|---|
   | `STORE_PUBLISHER` | the exact **Publisher** string, e.g. `CN=...` |
   | `STORE_PUBLISHER_DISPLAY_NAME` | the **publisher display name**, e.g. `Contoso` |
   | `STORE_APP_NAME` | the reserved **identity name**, e.g. `Contoso.CleanMachine` |

   These are three different strings and they are easy to confuse. Note in
   particular that the identity name is *not* the app name: Partner Center issues
   it as `<publisher display name>.<app name>`, so an app called `CleanMachine`
   under publisher `daygle` is reserved as `daygle.CleanMachine`. The app's
   user-facing name (`DisplayName`, Start menu, listing) stays `CleanMachine`;
   only the package identity is prefixed.

   Partner Center fails ingestion when any of the three differ, and the rejection
   only appears *after* the whole package has uploaded. The workflow stamps all
   three per run, re-checks them in the built package, and refuses to build if any
   is unset.
2. **A Partner Center developer account.** Free for individual and company
   accounts.
3. **Approve `runFullTrust`.** The manifest declares it, and Partner Center warns
   that it needs approval on every submission. It is completed in the
   submission's *Submission options → Restricted capabilities* field, using the
   justification below.

## Release checklist

1. **No version edits to make.** The tag is the only version input: at build time
   the workflow stamps `Package.appxmanifest` (Identity version and publisher),
   `app.manifest` (assembly identity) and the assembly/file version (via
   `ApplicationDisplayVersion`) from the tag.

2. **Verify**: `dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -c Release -p:Platform=x64` (all tests must pass).

3. **Tag and push**:
   ```bash
   git commit -m "Bump version to X.Y.Z"
   git tag vX.Y.Z
   git push origin main
   git push origin vX.Y.Z
   ```
   The tag push starts the workflow. Tags like `v1.0.0.1` or `v1.2.3-rc1` are rejected by the workflow on purpose.

4. **Upload the submission**: download the `CleanMachine-StoreUpload` workflow
   artifact and upload `CleanMachine-StoreUpload.msixupload` in Partner Center
   (Product → Packages → New package).

5. **Submit for certification** and fill in the listing details (screenshots,
   description, privacy policy URL, support contact, age rating). Screenshots and
   the privacy policy are **not** part of the package.

6. **Verify the run**: it completes green in a single `store-package` job, the
   bundle covers x64 and ARM64, and the package manifest validated.

## What the submission package is

A single `.msixupload` containing one **multi-architecture bundle** (x64 +
ARM64) plus crash symbols.

| Requirement | Value in this repo |
| --- | --- |
| Package artifact | `*.msixupload` (package + crash symbols) |
| Build mode | `UapAppxPackageBuildMode=StoreUpload` |
| Bundling | `AppxBundle=Always`, `AppxBundlePlatforms=x64\|arm64` |
| Signing | unsigned, and verified unsigned - the Store signs on ingestion |
| Publisher | an identity **reserved in Partner Center** (`STORE_PUBLISHER`) |
| Publisher display name | the publisher display name from the same page (`STORE_PUBLISHER_DISPLAY_NAME`) |
| Identity name | the reserved identity name, `<publisher>.<app>` (`STORE_APP_NAME`) |

## `runFullTrust` capability justification

`Package.appxmanifest` declares the restricted capability
`rescap:Capability Name="runFullTrust"`, which Partner Center requires a written
justification for. The field asks one question - "why do you need it, and how
will it be used" - so answer both halves in order.

**Paste this (499 characters).** It answers why and how, and includes the
assurances a reviewer looks for:

> CleanMachine is a WinUI 3 desktop cleanup utility. Its entire purpose -
> inspecting and deleting temporary files, browser caches, Recycle Bin contents
> and registry values that lie outside the app container - cannot be done
> through the sandboxed WinRT APIs, so it runs as a normal full-trust desktop
> process. It writes only to per-user (HKCU) registry keys, never requests
> administrator rights (requestedExecutionLevel is asInvoker), makes no network
> connections, and never downloads or executes code.

If the field accepts more and you want to be thorough, expand it with the
detailed version below. If it truncates or rejects, use the short one - it
already covers everything that gets a `runFullTrust` request approved.

### Detailed version (use only if the field allows it)

> CleanMachine is a Windows desktop cleanup utility built with the Windows App
> SDK (WinUI 3). It requires `runFullTrust` because its entire purpose is
> inspecting and removing files, folders and registry values that lie outside
> the app container, which the sandboxed WinRT APIs cannot reach. A packaged
> WinUI 3 desktop app declares `runFullTrust` in order to run as a normal
> full-trust desktop process, and the manifest's
> `desktop:Extension Category="windows.fullTrustProcess"` entry requires it.
>
> How it is used:
> - Enumerating and deleting temporary files, thumbnail and icon caches, error
>   reports, internet cache and jump-list files, including browser profile cache
>   directories and Microsoft Store app cache folders.
> - Emptying the Recycle Bin through the Shell API (SHEmptyRecycleBin).
> - Reading per-user (HKCU) registry keys for Registry Care, and writing only to
>   HKCU: registry backups are taken with reg.exe before any value is removed.
> - Reading HKLM in a strictly read-only manner to list all-users startup
>   entries and installed applications. The app never writes to HKLM; removing
>   an all-users startup entry is refused with a message stating that
>   administrator rights would be required.
> - Registering per-user scheduled tasks and reading Explorer StartupApproved
>   values in HKCU.
>
> What it does not do: it does not download, install or execute code, does not
> access the network at all, does not communicate with any server, and does not
> request administrator rights. app.manifest declares
> `requestedExecutionLevel level="asInvoker"`, and scheduled tasks are created
> with /RL LIMITED, so the app runs entirely at standard user privilege. It does
> not read or modify files belonging to other users' accounts, and it never
> touches the Windows component store.
>
> All cleanup is review-first: every deletion is displayed to the user before it
> happens, protected, recently modified, locked and reparse-point paths are
> refused, and registry changes can always be restored from the backup the app
> writes first. The app ships no secure-erase or drive-wiping tool, contains no
> advertising, no telemetry and no third-party analytics, and makes no network
> connections. Its only persistent storage is a settings, statistics, history
> and backup folder under %LOCALAPPDATA%\CleanMachine, outside the package.

Every claim above is checkable against the source: `app.manifest` sets
`asInvoker`; all registry writes open `RegistryHive.CurrentUser`; the HKLM paths
in `InstalledAppsService` and `StartupAppsService` open without `writable: true`
and the all-users removal path returns before any write; and the project has no
`HttpClient`, `WebClient` or socket usage and only two package references, both
Microsoft build/runtime tooling. Keep it accurate - an inaccurate justification
is far more likely to fail certification than a plain one.

Because the Store listing is public, destructive features that attract
certification scrutiny have been removed from the app entirely: there is no
secure-delete/secure-erase tool and no drive wiper. Cleanup remains
review-first - every deletion is shown to the user before it happens, protected
and recently modified paths are refused, and the Windows component store is
never touched.

## Submission checklist

- [ ] `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY_NAME` and `STORE_APP_NAME` set
      from the Partner Center Identity page (three *variables*, not secrets)
- [ ] Tag pushed as `vX.Y.Z`; `store-package` job green
- [ ] `CleanMachine-StoreUpload.msixupload` uploaded in Partner Center
- [ ] Listing: description, screenshots, privacy policy URL, support contact
- [ ] `runFullTrust` justification entered and approved (see above)
- [ ] Age rating completed
- [ ] Notes for certification entered (optionally referencing the justification above)

## Smart App Control

A Store-distributed package is signed by Microsoft and carries full SmartScreen
and Smart App Control reputation from the moment it is installed, so nothing in
this document applies any more. Smart App Control used to block the previous
self-signed sideload package outright, with no "Run anyway" override; that is
precisely one of the problems shipping through the Store solves.
