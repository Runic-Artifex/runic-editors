# Releasing the editor extensions

A runic-editors release is a GitHub release with the two VSIX files that CI built
from the released commit and a `SHA256SUMS` file:

| Asset | Extension |
| --- | --- |
| `runic-devkit.vsix` | VS Code, **Runic DevKit**, `runic-artifex.runic-devkit` (Runic SDK and Translations; 0.20–0.21 `runic.vsix`, `runic-artifex.runic`; until 0.11 `runic-translations.vsix`, `runic-artifex.runic-translations`) |
| `runic-devkit-vs.vsix` | Visual Studio 2026, **Runic DevKit**, `RunicArtifex.DevKit.VisualStudio` (until 0.21 `runic-translations-visualstudio.vsix`, `Runic.Artifex.Translations.Rmf2`) |
| `SHA256SUMS` | `sha256sum` lines of both VSIX files |

After the GitHub release, the same files are published to the VS Code
Marketplace and, for final versions, the Visual Studio Marketplace through trusted
publishing ([Stores](#stores)). Runic DevKit is not on Open VSX for now: users of
VSCodium, Cursor, Windsurf and other Open VSX editors install the VSIX from the
GitHub release.

Up to 0.11.0 both extensions were released with Runic Translations
(runic-translations-sdk), as `0.11.1000` (VS Code) and `0.11.0.1000` (Visual
Studio). A release from this repository must map above that:
`eng/release/release.mjs` refuses `0.11.0` and older versions. Since 0.20 the
VS Code extension is the one Runic extension. Since 0.22 both extensions are
**Runic DevKit** with new identities (D070.5); the editors install them next to the
former ones, which users uninstall ([VS Code](../../vscode/README.md#moving-from-runic-or-runic-mf2-translations),
[Visual Studio](../../visualstudio/README.md#moving-from-runic-mf2-translations)).

The store identities are fixed before the first marketplace listing and are
permanent once published: VS Code Marketplace `runic-artifex.runic-devkit`,
Visual Studio Marketplace `runic-artifex.runic-devkit-vs`
(`publishManifest.json` `internalName` `runic-devkit-vs`). `MARKETPLACE` in
[`release.mjs`](release.mjs) holds them, and `release.test.mjs` pins them and
checks that `vscode/package.json` and `visualstudio/publishManifest.json` agree.

## Versioning

The extensions are released in lockstep with the Runic release train (D066.32): each
release takes the train's version — 0.22.0 next (0.20.0 was the first, 0.21.0 the latest), and the train's
`1.0.0-rc.N` and `1.0.0` — and is released last, after Runic Command Line, Runic
Translations and Runic SDK (runic-sdk). The version says which Runic SDK and
Translations releases the extensions belong to: the merged Runic extension
(runic-editors#2) follows the SDK's development features, such as the `dotnet runic dev`
session file, and the language server comes from the same train's Translations
release. A train without extension changes still releases them with its version.

After a release, `main` moves to the next train's version in `eng/version.json`, as
the other repositories do (D063.6, D066.13).

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
(`release-check`). It then checks each store selected by the **stores** input and
prints its exact publish command (`store-publish <stores> <version> <vsix>
--dry-run`, see [Stores](#stores)). A dry run ends there.

Without dry-run, the `publish` job, the only job with `contents: write`,
`id-token: write` and `attestations: write`, runs in the `release` environment
(create it with required reviewers before the first real release). It downloads
the same artifacts, checks the candidate files by hash and the VSIX files against
the inventory (`verify`), attests the build provenance of both VSIX files and
`SHA256SUMS`, and creates the GitHub release at the dispatched commit
(`release`), a prerelease for every `0.y.z` and every `-preview.N`/`-rc.N`
version. A rerun keeps a release of this tag and commit, finishes a draft, and
never modifies a published release. `release.test.mjs` pins this contract.

## Stores

The publish jobs follow D070.11–D070.14. Open VSX is deferred.

| Store | Job | Identity | Versions | Tool |
| --- | --- | --- | --- | --- |
| VS Code Marketplace | `marketplace-vscode` (ubuntu) | `runic-artifex.runic-devkit` | every release; `x.y.z-rc.N`/`-preview.N` with `--pre-release` | `@vscode/vsce` 4.1.1 (`vscode/bun.lock`): `vsce publish --packagePath <vsix> --oidc --skip-duplicate [--pre-release]` |
| Visual Studio Marketplace | `marketplace-visualstudio` (windows-2025) | `runic-artifex.runic-devkit-vs` | final versions only | `VsixPublisher.exe publish -payload <vsix> -publishManifest visualstudio/publishManifest.json -oidc` from `Microsoft.VSSDK.BuildTools` 18.12.1606 |

The version mapping is the one in [IDE extension versions](#ide-extension-versions):
0.22.0 is VS Code `0.22.1000` (a normal Marketplace release with the Preview label)
and Visual Studio `0.22.0.1000`; `1.0.0-rc.N` is VS Code `1.0.(500+N)` as a
Marketplace pre-release, which only users who opt in receive, and is not published
to Visual Studio, which has no pre-release channel and would offer it to everyone.
`1.0.0` (`1.0.1000`) then sorts above every release candidate.

**How a store job runs.** Both jobs need `publish` (the GitHub release), so they
run only without dry-run, after the release exists. Each runs in the
`marketplace` environment with `contents: read`, `id-token: write` and
`actions: read`, downloads the same CI artifacts by id, checks them against the
candidate job's hashes and inventory (`verify`), then runs `release.mjs
store-publish <store>`:

1. `store-check`: reads the store's public listing. A version listed with the same
   bytes and pre-release flag is skipped (so reruns are safe, and VsixPublisher,
   which overwrites a listed version, never runs twice); a version listed with
   other bytes or flag, or a newer listed version, fails the job.
2. It prints and runs the publish command. Both tools exchange the job's GitHub
   OIDC token for a short-lived Marketplace credential; the workflow stores no
   token and removes `VSCE_PAT` from the tools' environment.
3. `store-verify` waits up to 20 minutes for the store's scan and checks that the
   store lists the version with the right pre-release flag and serves the release's
   bytes.

The **stores** input (`all`, `vscode`, `visualstudio`, `none`) selects the stores;
after a partial failure, dispatch again with the failed store: the GitHub release
and a store that already lists the version are kept. Use `none` while the store
accounts do not exist.

**Dry run.** The candidate job runs `store-publish <stores> <version> <vsix>
--dry-run` in every run: it checks the VSIX identities (the VS Code VSIX against
`publisher.name`, the Visual Studio identity in `publishManifest.json`) and the
stores' public listings, and prints the exact commands. It reads only the stores'
public query API and has no OIDC permission. `release.mjs store-check <version>
<vsix> [store...]` and `store-verify <version> <vsix> [store...] [--wait <s>]`
also run locally.

**Listing.** `vscode/package.json` (icon, `homepage`, `bugs`, keywords,
`galleryBanner`, `qna: false`, `preview`), `vscode/README.md` and `CHANGELOG.md`
are the VS Code listing; `package.mjs` packs them with relative links pointing at
the release tag (`--baseContentUrl`/`--baseImagesUrl`) and checks the icon (PNG,
at least 128 px), at most 30 keywords, and no SVG images or `http:` links. The
Visual Studio listing is `visualstudio/publishManifest.json`, `overview.md` and the
manifest's `MoreInfo`, `ReleaseNotes`, `Icon`, `PreviewImage`, `Tags` and
`<Preview>true</Preview>`, checked by `package.py`. The icons are rendered from the
Runic Artifex brand mark by [`eng/brand/render-icons.sh`](../brand/render-icons.sh).
Q&A is off on both stores; questions go to GitHub issues (D070.14).

**VsixPublisher.** The extension still builds with `Microsoft.VSSDK.BuildTools`
18.9.820 (`visualstudio/RunicArtifex.DevKit.VisualStudio.csproj`). The publish job
restores 18.12.1606, the first version whose VsixPublisher has `-oidc`, from the
restore-only project [`vsixpublisher/`](vsixpublisher/VsixPublisher.csproj) in
locked mode, so publishing does not move the build toolchain.

**One-time setup (maintainer).** Before the first publish:

1. Create the Microsoft publisher `runic-artifex` ("Runic Artifex") at
   <https://marketplace.visualstudio.com/manage>; it serves both Marketplaces.
2. Add its Trusted Publishing policy: owner `Runic-Artifex`, repository
   `runic-editors`, workflow `release.yml`, environment `marketplace`.
3. Create the GitHub environment `marketplace` with required reviewers and
   deployment branches `main` only, and no secrets.
4. Approve the `marketplace` jobs of the first release run.

Until then dispatch with **stores** `none`, or keep the dry run: without the
policy the OIDC exchange fails and nothing is published.

### Publishing to the Visual Studio Marketplace

The Visual Studio extension, **Runic DevKit** `runic-artifex.runic-devkit-vs`, goes
to the Visual Studio Marketplace from 0.22.0, the release Visual Studio parity
landed in (D070.11). The rules:

- **Final versions only** (D070.11). The job runs only for a version without `-`:
  `1.0.0-rc.N` and `-preview.N` versions reach Visual Studio users through the
  GitHub release only, because the store has no pre-release channel and would offer
  them to everyone.
- **Publisher** `runic-artifex`, display name "Runic Artifex", shared with the
  VS Code Marketplace (D070.12). The Microsoft account connected to the
  maintainer's GitHub account owns it; there is no second owner for now (D070.16).
- **Trusted publishing only** (D070.14). The `marketplace` environment has
  required reviewers, deployment branch `main` only and no secrets; the job
  exchanges its GitHub OIDC token for a short-lived credential
  (`VsixPublisher.exe publish -oidc`). Never add a personal access token.
- **Visual Studio 2026 x64 only** (D070.14): the manifest's single installation
  target is `[18.0,19.0)` with product architecture `amd64`, which `package.py`
  enforces. There is no ARM64 test machine, so no ARM64 target.
- **Listing**: icons from the brand mark, `overview.md`, categories and tags as in
  [Stores](#stores) (Listing); Q&A off, questions go to GitHub issues (D070.14).
- **Open VSX is deferred indefinitely** (D070.15): the Eclipse Foundation account
  it needs asks for too much information. VSCodium, Cursor, Windsurf and other
  Open VSX editors install `runic-devkit.vsix` from the GitHub release. Not a
  Visual Studio concern, but it means the store jobs are the two Microsoft ones.

Steps for a final release (the maintainer approves each release first; agents never
publish on their own):

1. Before the release PR merges, run the
   [native Visual Studio check](../../visualstudio/test/README.md) on Windows with
   the `visualstudio-vsix` artifact of the candidate commit's CI run, and work
   through its manual checklist. Record the result in the release PR.
2. Dispatch **Release** from `main` with the version and **dry-run** checked. In the
   candidate job's log, `store-publish visualstudio … --dry-run` must show the
   `runic-artifex.runic-devkit-vs` identity from `publishManifest.json`, report the
   version as not yet listed (a listed version with other bytes, or a newer listed
   version, fails here), and print the `VsixPublisher.exe publish -payload … -publishManifest
   visualstudio/publishManifest.json -oidc` command.
3. Dispatch again without dry-run (**stores** `all`, or `visualstudio` to publish to
   this store alone after the GitHub release exists). The `publish` job waits for
   the `release` environment's reviewers, the store jobs for the `marketplace`
   environment's reviewers.
4. `marketplace-visualstudio` (windows-2025) checks the VSIX against the candidate
   hashes and inventory, skips a version that the store already lists with the same
   bytes (VsixPublisher would overwrite it), publishes, and `store-verify` waits up to
   20 minutes for the Marketplace scan, then checks that the listing serves the
   release's bytes.
5. After the first publish, open
   <https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit-vs>:
   the page must show the Preview label (VsixPublisher has dropped it before:
   microsoft/vsmarketplace#905), the icon, the overview and no Q&A tab. Then install
   it in Visual Studio 2026 through Extensions > Manage Extensions and check that
   Tools > Runic appears in a Runic solution.
6. If the job failed after the GitHub release, fix the cause and dispatch with
   **stores** `visualstudio`: the GitHub release and a VS Code listing stay as they
   are.

To withdraw a broken version, publish a fixed patch release (D026: patches only for
security and severe regressions); unlisting on the publisher's manage page is the
maintainer's decision.

## Verifying a release

```sh
sha256sum --check SHA256SUMS
gh attestation verify runic-devkit.vsix -R Runic-Artifex/runic-editors \
  --signer-workflow Runic-Artifex/runic-editors/.github/workflows/release.yml
```

The attestation shows that the release workflow of this repository, which runs
only on `main`, released those bytes. The workflow does not build them: it takes
them from the successful CI run of the same commit and checks them by hash first.
After publishing, `store-verify` checks that each Marketplace serves the same bytes.
