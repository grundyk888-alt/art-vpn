# Code signing policy

Status: **preparation only; not approved or signed by SignPath Foundation**.
No certificate or signing entitlement is asserted by this repository.

The project is preparing an application for
[SignPath Foundation](https://signpath.org/terms.html). Admission is discretionary.
The certificate publisher would be SignPath Foundation, not an arbitrary ARTSPORT
publisher string. Publishing source alone does not grant a certificate.

Before a signing request can be enabled:

1. Finish the portable installer build and verify its source/dependency provenance.
2. Agree the eligibility of the modified Throne/sing-box component with SignPath.
   Preserve upstream notices and clearly identify the modified snapshot; do not
   request an ART VPN signature on third-party binaries without approval.
3. Complete required account/MFA, maintainer/reviewer/approver and repository-app
   configuration. Do not invent role holders or claim independent review that
   did not happen.
4. Build on a supported GitHub-hosted workflow, bind the request to the immutable
   commit/artifact, and require release approval. Untrusted pull requests must
   not gain access to signing credentials or release permissions.
5. Verify Authenticode, timestamp and archive checksums on the signed output;
   test the final downloadable artifact before replacing the website version.

Current CI is source verification, not the complete signing/release pipeline.
No signing API token or private key is committed. No arbitrary uploaded executable
is eligible simply because a maintainer supplied it.

Signing does not guarantee immediate SmartScreen reputation. The installer must
not disable Windows protection, strip download-origin metadata as a workaround,
or install a private root certificate to disguise missing public trust.
