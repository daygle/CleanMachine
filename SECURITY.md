# Security Policy

## Supported versions

CleanMachine is currently a prototype. Security fixes are applied to the latest state of the `main` branch. Older builds and unreleased artifacts are not guaranteed to receive security updates.

Do not use the prototype on systems where cleanup, registry export/restore, secure deletion, or update behavior has not been independently validated for your environment.

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

- Update manifest, package hash, publisher, and rollback validation
- MSIX, Authenticode, and release workflow configuration
- Registry backup, restore, and future mutation paths
- Protected-path and reparse-point validation
- Browser profile discovery and cleanup boundaries
- Startup registration and background-agent execution
- Secure Delete path selection and overwrite behavior
- Accidental collection or disclosure of local data

## Current limitations

CleanMachine currently has Windows-specific functionality that requires validation on supported Windows versions. Registry mutation is intentionally disabled, and secure-delete overwrite methods cannot guarantee sanitization of SSDs or modern storage. Do not interpret the presence of a UI option or prototype service as proof of production-grade protection.

The project does not request vulnerability reports containing secrets. Remove API keys, certificates, passwords, browser data, registry exports, and other private information before sharing diagnostics.
