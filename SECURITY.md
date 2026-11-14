# Security Policy

## Supported versions

Only the latest release of the editor extensions is supported. The extensions
run the workspace's `dotnet-runic-translations` tool; report problems in the
language server itself to
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk/security/advisories/new).

## Reporting a vulnerability

Report vulnerabilities privately through GitHub:

**[Report a vulnerability](https://github.com/Runic-Artifex/runic-editors/security/advisories/new)**

Do not open a public issue, pull request or discussion for a security report.

Include the extension and IDE versions, the `dotnet-runic-translations` version,
the operating system, steps to reproduce and the impact you expect.

## Verifying releases

Each GitHub release attaches the VSIX files with a `SHA256SUMS` file and a signed
build-provenance attestation of each. Verify a file with
`gh attestation verify <file> -R Runic-Artifex/runic-editors --signer-workflow Runic-Artifex/runic-editors/.github/workflows/release.yml`
(see [verifying a release](eng/release/README.md#verifying-a-release)).
