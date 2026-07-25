# Security policy

## Supported versions

Security fixes are applied to the latest published release.

## Reporting

Do not open a public issue containing credentials, internal addresses, logs, diagnostics archives, or vulnerability details. Report privately through GitHub Security Advisories for this repository.

## Deployment requirements

- Keep the default loopback bind unless remote access is required.
- Require authentication and trusted HTTPS before LAN exposure.
- Configure exact trusted reverse-proxy IP addresses before accepting forwarded client/protocol headers; CIDR proxy trust is rejected.
- Restrict the application and Wowza REST ports to explicit management networks.
- Run under a dedicated, non-administrator Windows account.
- Protect the `data` directory with NTFS permissions and back it up securely.
- Never publish a production database or diagnostics package; DPAPI ciphertext and internal topology are still sensitive.
- Sanitized diagnostics omit the database, credentials, internal addresses, media paths, and topology names. Treat even sanitized packages as operationally sensitive.
- Use exact, trusted FFmpeg binaries and verify their signatures/checksums according to your organization's policy.

Wowza credentials are protected with Windows DPAPI for the runtime account. Copying the database to another account or machine does not make those credentials portable.

The host refuses anonymous non-loopback binding. Route-control commands independently require the Administrator role, the status hub requires authentication, login attempts are rate-limited per effective client address, and proxied loopback claims never receive the direct-loopback shortcut.
