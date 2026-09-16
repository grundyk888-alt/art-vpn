# Release 1.0.40 checks — 2026-09-16

These are bounded engineering checks, not an independent certification or a
promise of uninterrupted connectivity under every provider/OS/network condition.

The 1.0.40 change removes subscription-discovery UI, implementation and tests;
negative UI regression checks verify that those controls are absent. No routing
or channel-selector logic was rewritten for this release.

Release-operator checks in a disposable Windows 10 VM passed:

- 18 service regression families, 182 installer checks and 27 update-transfer cases.
- Main UI, four scaling configurations and two compact layouts.
- 17 embedded payload files; the VM system proxy remained unchanged during fixtures.
- A clean build of the complete installer from the separately published source.
- Public ZIP download and SHA-256 equality, including the installer inside it.

An earlier isolated `scenario:busy` run timed out after 180 seconds. A full rerun
of the same binaries passed. Its intermittent cause remains unknown: do not call
this a proven runtime fix or discard the earlier failure.

Prior 1.0.36/1.0.38 acceptance separately exercised installation with HAPP TUN,
manual HAPP/ART round trips, return to HAPP on uninstall, external fallback and
upgrade preserving settings. These are dated earlier-version checks, not a fresh
installation/reboot/authenticated Codex test of 1.0.40. The release was approved
after user testing, but no claim of every possible environment is made.

GitHub Actions builds components and runs 14 groups of bounded fixture entry
points from the current commit. CI does not install a VPN, use live subscriptions,
change the runner network, sign a release or demonstrate an authenticated Codex
conversation. Look at the actual run result for the relevant commit.

Private VM logs, account details and subscriptions are not published. Never
re-label an old receipt as proof of a newly built or signed installer.
