# Releasing the editor extensions

A runic-editors release is a GitHub release with the two VSIX files that CI built
from the released commit and a `SHA256SUMS` file:

| Asset | Extension |
| --- | --- |
| `runic-translations.vsix` | VS Code, `runic-artifex.runic-translations` |
| `runic-translations-visualstudio.vsix` | Visual Studio 2026, `Runic.Artifex.Translations.Rmf2` |
| `SHA256SUMS` | `sha256sum` lines of both VSIX files |

Nothing is published to the VS Code Marketplace, Open VSX or the Visual Studio
Marketplace (D066.25 #4); install a VSIX from the release.

Up to 0.11.0 both extensions were released with Runic Translations
(runic-translations-sdk), as `0.11.1000` (VS Code) and `0.11.0.1000` (Visual
Studio). A release from this repository must map above that:
`eng/release/release.mjs` refuses `0.11.0` and older versions.

## IDE extension versions

`eng/version.json` holds the release version. Neither VSIX format accepts a SemVer
prerelease label, so packaging stamps each extension with a version mapped from it
([`ide-versions.mjs`](ide-versions.mjs), unchanged from runic-translations-sdk).
The committed manifests carry no release number: `vscode/package.json` keeps a
`0.0.1` placeholder and `visualstudio/source.extension.vsixmanifest` carries the
`|%CurrentProject%;GetRunicVsixVersion|` build token.

| Release | Visual Studio (`N.N.N.N`) | VS Code (`x.y.z`) | VS Code pre-release |
| --- | --- | --- | --- |
| `0.12.0` | `0.12.0.1000` | `0.12.1000` | no |
| `0.12.1` | `0.12.1.1000` | `0.12.2000` | no |
| `1.0.0-preview.N` | `1.0.0.N` | `1.0.N` | yes |
| `1.0.0-rc.N` | `1.0.0.(500 + N)` | `1.0.(500 + N)` | yes |
| `1.0.0` | `1.0.0.1000` | `1.0.1000` | no |

Only a plain `x.y.z` and `x.y.z-preview.N` or `x.y.z-rc.N` with N from 1 to 499
can be packaged, and each of major, minor and patch is at most 65534 (Visual Studio
reads each component as a 16-bit `System.Version` part). Visual Studio's fourth
component is the preview number, 500 plus the release-candidate number, or 1000 for
a final release; VS Code folds it into the patch (`patch × 1000 + revision`) and
packages a prerelease with `vsce package --pre-release`. Every release carries the
VS Code `"preview": true` field (the Marketplace "Preview" label): the extensions
are a preview product until the production language server (2.0).

- `bun run package` in `vscode` (`package.mjs`) stages the files `vsce ls` lists,
  stamps the staged `package.json` and packs it, then checks the VSIX identity,
  version, pre-release flag and Preview label. `node package.mjs <release> [output]`
  packages another release; CI packages `0.8.0` this way to check a plain release.
- The Visual Studio project replaces the manifest token with its
  `GetRunicVsixVersion` result during the Windows build, and `package.py` fails
  unless the built VSIX has the mapped version. `-p:RunicIdeReleaseVersion=<version>`
  builds another version.
- `ide-versions.test.mjs` checks that `ide-versions.mjs`, `package.py` and the
  Visual Studio project agree.

## Language server

The extensions do not bundle a language server: they run the workspace's
`dotnet-runic-translations` tool (`runic-translations lsp`). This repository pins
the Translations release it builds and tests against in
[`.config/dotnet-tools.json`](../../.config/dotnet-tools.json); the VS Code host
test restores exactly that version from NuGet. The VS Code extension bundles that
release's `runic.json` schema ([`eng/translations/project-v1.schema.json`](../translations/project-v1.schema.json));
`bun eng/translations.mjs --check` (CI) compares it with the schema in the
release's `Runic.Translations.Tooling` package, and `bun eng/translations.mjs`
refreshes it. To move to a new Translations release, change the tool version,
run `dotnet tool restore` and `bun eng/translations.mjs`, run the host test, and
update the supported versions in the repository README.

## Release workflow

The [Release](../../.github/workflows/release.yml) workflow is a draft: it is
dispatch-only, runs only from `main` and is a **dry run by default**.

1. Set the version in `eng/version.json`, optionally add notes in
   `eng/release/notes/<version>.md`, merge to `main` and wait for that commit's
   CI push run to succeed.
2. Dispatch **Release** from `main` with that exact version, first with
   **dry-run** checked (the default).

The `candidate` job (`contents: read`, `actions: read`) validates the version
against `eng/version.json`, finds the newest successful `ci.yml` push run on `main`
for the dispatched commit (`release.mjs find-ci`), downloads its `vscode-vsix`
and `visualstudio-vsix` artifacts by id, checks both files' identities and
mapped versions (`prepare`), writes `candidate.json` and `SHA256SUMS`, and
checks that neither the tag `v<version>` nor a release exists for another commit
(`release-check`). A dry run ends there.

Without dry-run, the `publish` job, the only job with `contents: write`,
`id-token: write` and `attestations: write`, runs in the `release` environment
(create it with required reviewers before the first real release). It downloads
the same artifacts, checks the candidate files by hash and the VSIX files against
the inventory (`verify`), attests the build provenance of both VSIX files and
`SHA256SUMS`, and creates the GitHub release at the dispatched commit
(`release`), a prerelease for every `0.y.z` and every `-preview.N`/`-rc.N`
version. A rerun keeps a release of this tag and commit, finishes a draft, and
never modifies a published release. `release.test.mjs` pins this contract.

## Verifying a release

```sh
sha256sum --check SHA256SUMS
gh attestation verify runic-translations.vsix -R Runic-Artifex/runic-editors \
  --signer-workflow Runic-Artifex/runic-editors/.github/workflows/release.yml
```

The attestation shows that the release workflow of this repository, which runs
only on `main`, released those bytes. It does not build them: it takes them from
the successful CI run of the same commit and checks them by hash first.
