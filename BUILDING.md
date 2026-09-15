# Building the reviewed source snapshot

Use Windows x64, PowerShell 7 and .NET SDK 10.0.400 (or its latest patch).
The source does not need administrator privileges to compile. Use a fresh output
directory on a drive with sufficient space; the script never deletes an existing
output directory and does not install/start the service or change system routing.

```powershell
pwsh -File ./build/Build-Source.ps1 -OutputRoot D:/ARTVPN-Build/source-check-001
```

This compiles the service, UI and maintenance/setup shell and runs isolated
regression fixtures. **The setup shell has no embedded installation payload.**
Do not publish these component binaries as a complete consumer installer.
The original internal packaging scripts were intentionally not copied: they
depended on private build paths and previous candidate payloads. A fully portable
installer packaging/signing workflow is still being prepared.

The core's exact corresponding source is included in
`third_party/core/ARTVpnCore-corresponding-source.zip`, together with a source
manifest, provenance, dependency SBOM and original license notices. It is a
modified Throne/sing-box snapshot, not an unmodified upstream release.

```powershell
# Install Go 1.25.5 for Windows amd64 first.
pwsh -File ./build/Build-Core.ps1 -OutputRoot D:/ARTVPN-Build/core-001
```

The core script checks the archive hash, uses the archive's build tags and
verifies the resulting binary against the recorded hash. Dependencies are
resolved through Go's module checksum mechanism. No service is installed.
If the exact hash does not match, stop: do not bypass the runtime verifier.

GitHub Actions builds/checks the .NET source on a GitHub-hosted Windows runner.
It does not publish a release, install on a user's machine, submit signing
requests or prove authenticated Codex/Office connectivity. There are no signing
keys, subscriptions or private deployment credentials in this repository.
