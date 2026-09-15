# Privacy and network behaviour

ART VPN is a VPN/proxy client. It forwards selected application traffic through
the VPN provider chosen by the user; the provider's own terms and privacy policy
apply. Russian-site/local-network bypass rules affect which traffic goes directly.
ART VPN does not make the provider anonymous or promise that all applications
obey the Windows system proxy.

The application retrieves the user-supplied subscription URL and uses its
credentials to connect to the provider. Provisioned secrets are stored using
Windows DPAPI and restricted local access. A subscription URL is a secret:
do not put it in GitHub issues, screenshots or diagnostic attachments.

Channel checks make requests to configured connectivity endpoints, including
OpenAI/ChatGPT. Rule updates access selected public GitHub rule repositories.
Users' network peers can observe network metadata such as IP addresses and
request times. This is not a claim of zero network data processing.

Local status/diagnostic records include version, mode, country, latency/health,
event codes and transition times. The optional support report includes those
fields and a **pseudonymous installation identifier** derived from an installation
ID. It is not guaranteed anonymous merely because the identifier is hashed.
The support-report schema still uses the historic name `AnonymousInstallId`.
The report is intended to exclude subscription URLs, traffic contents, passwords,
IP/MAC addresses and user-profile paths. Scrubbing is defence in depth, not a
promise that any arbitrary free-form log is safe to publish.

The HTTP support-report path requires explicit user confirmation and a configured
HTTPS endpoint. The source snapshot does not include a private support-endpoint
configuration. Review any report before sharing it. There is no consent to upload
arbitrary files or browsing contents.

The optional Quattro recommendation opens a registration/referral link only when
clicked. The author may benefit from referrals. It is not required for installation;
a compatible existing subscription may be used. Opening the link sends normal
browser request data to that third party.

Do not submit credentials or raw configuration in public issues. Privacy/security
contact: grundyk888@proton.me.
