# Releasing CleanMachine

Releases are built and published by the [`Release Windows app`](.github/workflows/release-windows.yml) workflow, which triggers on pushed tags matching `v*.*.*` (e.g. `v1.0.6`). The tag is the single source of truth for the version: the workflow stamps the MSIX identity, the assembly version, and the installer name from it.

## Release checklist

1. **Bump the in-repo version defaults** to `X.Y.Z`:
   - `CleanMachine.Windows/Package.appxmanifest` → `Version="X.Y.Z.0"`
   - `CleanMachine.Windows/app.manifest` → `version="X.Y.Z.0"`
   - `src/App.tsx` → the two display strings (`CleanMachine vX.Y.Z · …` and `Privacy first · vX.Y.Z`)
   - `update-manifest.example.json` → version and package URL placeholders
   - The installer `.iss` needs no change; its version is injected by the workflow.

2. **Verify**: `dotnet test CleanMachine.Windows.Tests/CleanMachine.Windows.Tests.csproj -c Release -p:Platform=x64` (all tests must pass).

3. **Tag and push**:
   ```bash
   git commit -m "Bump version to X.Y.Z"
   git tag vX.Y.Z
   git push origin main
   git push origin vX.Y.Z
   ```
   The tag push starts the workflow. Tags like `v1.0.0.1` or `v1.2.3-rc1` are rejected by the workflow on purpose.

4. **Verify the release**:
   - The run completes green (build x64, build ARM64, installer, release jobs).
   - A GitHub release is published with `CleanMachine-x64-vX.Y.Z.msix`, `CleanMachine-ARM64-vX.Y.Z.msix`, both `.sha256` files, `CleanMachine-Setup-X.Y.Z.exe`, and `update-manifest.json`.
   - The MSIX signer thumbprint matches the stable cert (below).

## Code signing

### Secrets
The workflow signs with a PFX provided through two repository secrets:

| Secret | Value |
|---|---|
| `WINDOWS_SIGNING_CERTIFICATE_BASE64` | Base64 of the signing PFX |
| `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` | Password of that PFX |

The certificate's subject must contain the package publisher (`CN=CleanMachine Publisher` by default, overridable via the `WINDOWS_PUBLISHER` repo variable) — the workflow and the in-app updater both validate the signer against it.

### Stable self-signed cert (current setup)
A long-lived self-signed code-signing cert is used so users trust it **once** instead of after every release:

- Subject `CN=CleanMachine Publisher`, code-signing EKU, RSA-3072
- Valid until **Sep 2036** (regenerate before then and update the secrets)
- Thumbprints:
  - SHA-1: `FF954B01644555350E9411FEC586896BA4EF267D`
  - SHA-256: `9A8153067595DC6ED90408335AD2799534476772F81546371488101DC4364580`
- Local material lives in `.signing-cert/` (gitignored — never commit it):
  - `CleanMachine-signing-credentials.txt` — the secret values
  - `CleanMachine-signing.cer` — public cert for user trust import
  - `CleanMachine-signing.pfx` — offline backup of the key pair

### Fallback behavior
If the secrets are not configured, the workflow mints a **throwaway self-signed cert per build**. The packages still sign, but the thumbprint changes every release, so users must re-trust the cert on each update. Avoid this; keep the secrets set.

### Trusting on a user machine
1. Double-click `CleanMachine-signing.cer` → Certificate Import Wizard → **Trusted Root Certification Authorities** → tick **"Trust for certificate authentication"** → Finish.
2. Enable sideloading: Settings → Apps → Advanced app settings → **"Install apps from unknown sources"**.

### Verifying a release signature
```powershell
Get-AuthenticodeSignature "CleanMachine-x64-vX.Y.Z.msix"
# Status should be Valid; SignerCertificate.Thumbprint must equal the SHA-1 thumbprint above
```

## Update manifest

The workflow generates `update-manifest.json` for each release (version, per-architecture package URLs, SHA-256 hashes, publisher) and attaches it to the GitHub release. The in-app updater fetches it over HTTPS, verifies hash and publisher, then sideloads the MSIX. `update-manifest.example.json` in the repo is just a template.