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
- Protected-path and reparse-point validation
- Browser profile discovery and cleanup boundaries
- Startup registration and background-agent execution
- Recycle Bin auto-empty and scheduled-task registration
- Accidental collection or disclosure of local data

## Current limitations

CleanMachine is Windows-specific and requires validation on each supported Windows version. Registry Care only cleans findings that pass its low-risk confidence gate (always after a backup). CleanMachine ships no secure-erase or drive-wiping tool and makes no claim of sanitizing storage; use device encryption for sensitive data. Do not interpret the presence of a UI option as proof of production-grade protection.

The project does not request vulnerability reports containing secrets. Remove API keys, certificates, passwords, browser data, registry exports, and other private information before sharing diagnostics.
