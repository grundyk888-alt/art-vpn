# Candidate 1.0.19 acceptance notes — 2026-09-15

These are bounded engineering checks, not an independent certification or a
promise of uninterrupted connectivity under every provider/OS/network condition.

Tested in an isolated Windows 10 VM using the frozen candidate:

- Install, repeat-install/idempotence, registered uninstall and reinstall.
- Fresh subscription provisioning through the product UI event path; automatic
  connection after a valid subscription; invalid-subscription rejection.
- Unavailable external HAPP proxy rejected without discarding the working route.
- Owned core-process failure recovery (about 6 seconds) and service restart
  recovery (about 8 seconds) in that test environment.
- Subscription refresh with 92/92 continuity probes successful while refreshing;
  fresh final requests confirmed after activation. Some intermediate probes reused
  HTTP connections; they are not proof of 92 distinct newly established sessions.
- Real bypass-rule downloads to a verified candidate; active rules/configuration
  preserved until normal generation qualification.
- Unavailable update feed produces a bounded error without breaking routing.
  This is **not** a working automatic product-update acceptance result.

Programmatic UI/event tests are not equivalent to native mouse interaction or
successful signed-in Codex usage. Authenticated Codex/Office flows, native Codex
updates, remote-session continuity, a complete third-party TUN takeover and reboot
acceptance of this exact frozen candidate remain outside those passed checks.

Private VM logs, account details, subscription URLs and credentials are not
published here. CI receipts are separate fresh fixture results from public source.
Never re-label an old receipt as proof of a newly built/signed installer.
