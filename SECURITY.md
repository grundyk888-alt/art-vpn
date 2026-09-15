# Reporting security issues

Please send security reports privately to **grundyk888@proton.me**. Do not include
working subscriptions, passwords, tokens or customer traffic. Start with version,
expected/observed behaviour and a redacted minimal reproduction. There is no
promised response-time SLA and no claim of an independent security certification.

For ordinary defects, use GitHub issues with a minimal, non-secret reproduction.
Fixes should add a targeted regression case for the affected invariant, document
the outcome and limitations, and avoid changing unrelated stable routing logic.

Release checks must distinguish unit/fixture tests from installed acceptance,
network checks from authenticated application tests, and a running process from
working connectivity. Do not describe an untested reboot or third-party TUN
handoff as passed. Preserve a verified rollback download until its replacement
has passed the relevant checks.
