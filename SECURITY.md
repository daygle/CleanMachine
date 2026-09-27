# Security Policy

## Supported versions

Security fixes are applied to the latest state of the `main` branch and shipped through Microsoft Store releases. Only the latest Store version receives security updates; older builds are not guaranteed to receive them. Keep the app current by letting the Store update it (Store -> Library -> Get updates).

Only use CleanMachine on systems where its cleanup and registry export/restore behavior have been validated for your environment.

## Reporting a vulnerability

Please report suspected security vulnerabilities privately to the repository maintainers before opening a public issue. Use the repository's GitHub **Security** tab and choose **Report a vulnerability** when private vulnerability reporting is available.

If that option is unavailable, open a minimal issue requesting a private contact channel. Do not include exploit details, personal data, credentials, registry exports, browser profile contents, or potentially sensitive files in a public issue.

Please include:

- A short description and security impact
- The affected version, commit, or workflow
- Reproduction steps or a minimal proof of concept
- Required privileges and environmental assumptions
- Any suggested mitigation

We will acknowledge reports as soon as practical, investigate responsibly, and coordinate disclosure and fixes with the reporter where appropriate. Please allow reasonable time for remediation before public disclosure.

## Security-sensitive areas

Reports are especially important for:

- Package publisher, version stamping, and submission validation
- MSIX, Authenticode, and release workflow configuration
- Registry backup, restore, and future mutation paths
- The restore-point provenance record and the scope allow-list on restore
- Protected-path and reparse-point validation
- Browser profile discovery and cleanup boundaries
- Startup registration and background-agent execution
- Recycle Bin auto-empty and scheduled-task registration
- Settings loading, which decides what the app is allowed to delete
- Accidental collection or disclosure of local data

## Threat model

CleanMachine runs as a normal, unelevated, per-user desktop app: `asInvoker`,
HKCU-only registry writes, scheduled tasks registered `/RL LIMITED`. Most of what
it does is destructive by design, so the question that matters is *what it treats
as untrusted input*.

**Untrusted, and checked as such:**

- **`settings.json`.** It decides which categories run, which paths are
  excluded, and whether a schedule shuts the machine down afterwards. It is a
  plain file in the user's own profile, so it cannot be made secret and is not
  treated as authentic. It is read with a size bound, and every value is clamped
  back into range before anything acts on it - including schedule ids, which are
  filtered with the same rule the command-line builders enforce.
- **Anything in a backups folder that CleanMachine did not just export.** A
  `.reg` file is a script, and the backup directories include `%TEMP%`. So a
  restore point must be in the app's own provenance record - file name, key root,
  time, and a hash of the bytes - and must still hash to what was recorded. The
  record deliberately lives in one place outside the backup directories, so
  dropping a file next to the backups cannot drop a matching record next to it.
  The Backups page labels a restore point from its recorded key root, never from
  its file name, which is the one thing a planted file's writer fully controls.
- **Restore scope.** `reg import` applies everything in the file, so restoring is
  bounded by the same allow-list as deleting rather than a wider one.

**Explicitly not defended against:** code already running as this user. Such code
can write `HKCU` directly, can edit `settings.json`, and can rewrite the
provenance record. No file-based scheme changes that, and the app does not claim
otherwise. What the controls above buy is that a file dropped in a folder is not
silently treated as a trusted restore point, and that a corrupted or hand-edited
settings file degrades to "not understood" rather than to an out-of-range
schedule or an unbounded exclusion list.

## Current limitations

CleanMachine is Windows-specific and requires validation on each supported Windows version. Registry Care only cleans findings that pass its low-risk confidence gate (always after a backup). CleanMachine ships no secure-erase or drive-wiping tool and makes no claim of sanitizing storage; use device encryption for sensitive data. Do not interpret the presence of a UI option as proof of production-grade protection.

The project does not request vulnerability reports containing secrets. Remove API keys, certificates, passwords, browser data, registry exports, and other private information before sharing diagnostics.
