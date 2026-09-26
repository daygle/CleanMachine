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
publish on the Store. The workflow defaults to building an **unsigned** package
for exactly this reason. The only money involved is the one-off Partner Center
developer account fee, which Microsoft waives for individual and company
accounts.

Signing the package yourself is *optional*. It buys you nothing user-visible -
the package you upload is a build artifact, not something users ever run - so
the recommended setup is to leave the certificate secrets unset. If you do set
them, the workflow signs the package and then verifies that the signature chains
to a CA in the Microsoft Trusted Root Program, failing the build early if it does
not.

| Secret | Required? | Value |
|---|---|---|
| `STORE_SIGNING_CERTIFICATE_BASE64` | No | Base64 of a PFX whose subject chains to a trusted root |
| `STORE_SIGNING_CERTIFICATE_PASSWORD` | No | Password of that PFX |

The certificate's subject must contain the package publisher, because the
publisher is part of the package identity.

## Prerequisites

1. **Reserve an app identity in Partner Center** (Product → Identity) and put the
   exact `Publisher` string in the `STORE_PUBLISHER` repository variable. The
   workflow refuses to guess it: a wrong publisher uploads cleanly and then fails
   certification.
2. **A Partner Center developer account.** Free for individual and company
   accounts.

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
| Signing | unsigned (the Store signs it) - optional to self-sign |
| Publisher | an identity **reserved in Partner Center** (`STORE_PUBLISHER`) |

## `runFullTrust` capability justification

`Package.appxmanifest` declares the restricted capability
`rescap:Capability Name="runFullTrust"`, which Partner Center requires a written
justification for. Paste the following into the submission's restricted-capability
declaration field:

> CleanMachine is a desktop cleanup utility built with the Windows App SDK
> (WinUI 3). It declares `runFullTrust` because its core purpose cannot be
> implemented through the sandboxed WinRT APIs.
>
> Specifically, the app enumerates and removes temporary files and browser cache
> files located outside the app container (for example `%TEMP%`, browser profile
> cache directories, and Microsoft Store app cache folders), manages the Recycle
> Bin via the Shell API, and reads per-user registry keys for its read-only
> Registry Care view. These operations require file-system and registry access
> that is only available to a full-trust desktop process. The app does not use
> `runFullTrust` to download, install or execute code, to access the network
> beyond the Windows package deployment APIs, or to elevate privileges - it runs
> entirely in the current user context and never requests administrator rights.
>
> The app is distributed solely through the Microsoft Store and contains no
> advertising, no telemetry and no third-party analytics.

Because the Store listing is public, destructive features that attract
certification scrutiny have been removed from the app entirely: there is no
secure-delete/secure-erase tool and no drive wiper. Cleanup remains
review-first - every deletion is shown to the user before it happens, protected
and recently modified paths are refused, and the Windows component store is
never touched.

## Submission checklist

- [ ] `STORE_PUBLISHER` set to the Partner Center-reserved publisher string
- [ ] Tag pushed as `vX.Y.Z`; `store-package` job green
- [ ] `CleanMachine-StoreUpload.msixupload` uploaded in Partner Center
- [ ] Listing: description, screenshots, privacy policy URL, support contact
- [ ] `runFullTrust` justification entered (see above)
- [ ] Age rating completed
- [ ] Notes for certification entered (optionally referencing the justification above)

## Smart App Control

A Store-distributed package is signed by Microsoft and carries full SmartScreen
and Smart App Control reputation from the moment it is installed, so nothing in
this document applies any more. Smart App Control used to block the previous
self-signed sideload package outright, with no "Run anyway" override; that is
precisely one of the problems shipping through the Store solves.
