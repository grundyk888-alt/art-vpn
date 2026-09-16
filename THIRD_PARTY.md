# Third-party components

ART VPN's own source in this repository is GPL-3.0-only, see `LICENSE`.
Original third-party notices and terms are not replaced by that statement.

## Network core

`third_party/core/ARTVpnCore-corresponding-source.zip` contains the corresponding
source of the modified core and its manifest. Upstream:
https://github.com/Throneproj/sing-box (derived from SagerNet/sing-box).
Original copyright, GPL-3.0-or-later notice and name/association notice are
preserved in the archive and `ARTVpnCore.LICENSE.txt` / `SING-BOX-LICENSE.txt`.
ART VPN is not represented as an endorsed upstream release.

`ARTVpnCore.provenance.json` records the original local build evidence and exact
hashes; these historical statements are not evidence of a GitHub Actions build.
`ARTVpnCore.sbom.cdx.json` lists Go module dependencies from the recorded binary.
The archive's `go.mod`/`go.sum` pin module dependencies, whose own licenses apply.

## .NET

Projects use Microsoft .NET/Windows Forms and the NuGet packages
`System.ServiceProcess.ServiceController`,
`System.Security.Cryptography.ProtectedData` and their transitive dependencies.
Consult their upstream packages/repositories for licensing notices. Final
redistribution must retain the runtime/package notices applicable to bundled
self-contained .NET binaries; source publication alone is not that final bundle.

## Routing data and optional clients

The source references rule data maintained by MetaCubeX/meta-rules-dat,
hiddify/hiddify-geo and runetfreedom/russia-v2ray-rules-dat. The release's exact
rule data and manifest are in `payload-template/runtime/rules`. The installer
includes its payload manifest and component notices; maintain checksums and
applicable upstream terms in each release.

Throne and HAPP are optional separately installed external clients, not implied
to be ART VPN-owned code. Their availability, terms and functionality are separate.
