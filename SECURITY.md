# Security policy

## Supported versions

Security fixes are applied to the latest published release.

## Reporting

Do not open a public issue containing credentials, internal addresses, logs, diagnostics archives, or vulnerability details. Report privately through GitHub Security Advisories for this repository.

## Deployment requirements

- Keep the default loopback bind unless remote access is required.
- Require authentication and trusted HTTPS before LAN exposure.
- Restrict the application and Wowza REST ports to explicit management networks.
- Run under a dedicated, non-administrator Windows account.
- Protect the `data` directory with NTFS permissions and back it up securely.
- Never publish a production database or diagnostics package; DPAPI ciphertext and internal topology are still sensitive.
- Use exact, trusted FFmpeg binaries and verify their signatures/checksums according to your organization's policy.

Wowza credentials are protected with Windows DPAPI for the runtime account. Copying the database to another account or machine does not make those credentials portable.
