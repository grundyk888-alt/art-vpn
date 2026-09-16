# ART VPN 1.0, internal build 1.0.40

This archive contains the exact C# compiler inputs and corresponding core source
for the standalone 1.0.40 release. `source-inputs.json` records the C# file hashes.
It contains no subscriptions, private signing keys, deployment credentials or Git history.
GitHub source follows the same release inputs; public documentation is maintained separately.

Use Windows x64, PowerShell 7, .NET SDK 10.0.400 and Go 1.25.5. Build as an ordinary
user; neither command installs services or switches the computer's connection.
Use a fresh output directory on a drive with sufficient space.

1. Run `./build/Build-Core.ps1 -OutputRoot D:/ARTVPN-Build/core-140`.
   The exact corresponding source and licenses are in `third_party/core`.
   The script checks the source and resulting core hashes.
2. Run `./build/Build-Package.ps1 -OutputRoot D:/ARTVPN-Build/package-140 -CoreBinary <path-to-built-ARTVpnCore.exe>`.
   This builds the service, UI, maintenance shell and complete embedded-payload
   installer from the supplied source and payload template. It does not download
   a previous ART VPN installer or require private build directories.

The resulting installer is unsigned. Build paths, SDK patch and generated metadata
can affect .NET binary hashes; no byte-identical .NET installer claim is made.
The release manifest records the published binaries, not hashes of a future local build.
The public update-channel key verifies ARTSPORT releases; it is not a signing key
and cannot authorize a modified build. No Windows publisher certificate is supplied.

The consumer ZIP deliberately contains only setup, instructions and checksums.
Corresponding source is offered separately next to the download, under GPL-3.0.
Individual bundled components retain their notices in `payload-template/legal`.

Release checks: isolated service/installer/UI fixtures are recorded by the release
operator. They are not proof of every real provider, every reboot or an authenticated
Codex conversation. Test runtime operations on a disposable Windows VM first.

For component builds and bounded fixtures only, run `./build/Build-Source.ps1`
with `-OutputRoot <fresh-directory>`. This is also the GitHub Actions check; it
does not produce the complete consumer installer or install a VPN.
