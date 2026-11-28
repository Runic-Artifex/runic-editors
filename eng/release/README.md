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
publishing ([Stores](#stores)). Runic DevKit is not on Open VSX (deferred, D070.15): users of
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
release takes the train's version — 0.24.0 next (0.20.0 was the first, 0.23.1 the latest), and the train's
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
packages a prerelease with `vsce package --pre-release`. Since 0.23 no release
carries the Marketplace "Preview" label (D070.2, D077.4): `package.mjs` stamps the
VS Code `"preview": false` field, and the Visual Studio manifest has no `<Preview>`.

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

The `candidate` job (`contents: read`, `actions: read`, `attestations: read`)
first decides what the dispatch does (`release.mjs release-check <version>
<stores>`, read-only):

- **Release** (the normal case): the version must be the one in
  `eng/version.json`, and neither the tag `v<version>` nor a release may exist for
  another commit. The job finds the newest successful `ci.yml` push run on `main`
  for the dispatched commit (`find-ci`), downloads its `vscode-vsix` and
  `visualstudio-vsix` artifacts by id, checks both files' identities and mapped
  versions (`prepare`), and writes `candidate.json` and `SHA256SUMS`.
- **Store-only** ([Retrying a store](#retrying-a-store)): **stores** is
  `vscode` or `visualstudio`, the GitHub release `v<version>` is published, and
  its tag commit is the dispatched commit or an ancestor of it. The job downloads
  the release's files and checks them (`fetch-release`); no GitHub release is
  created.

It then checks each store's VSIX against the store's own offline upload checks
(`store-rules <stores> <version> <vsix>`, see [Store rules](#store-rules)),
checks each store selected by the **stores** input and prints its exact
publish command (`store-publish <stores> <version> <vsix> --dry-run`, see
[Stores](#stores)). A dry run ends there.

Without dry-run, the `publish` job (release only), the only job with `contents: write`,
`id-token: write` and `attestations: write`, runs in the `release` environment
(create it with required reviewers before the first real release). It downloads
the same artifacts, checks the candidate files by hash and the VSIX files against
the inventory (`verify`), attests the build provenance of both VSIX files and
`SHA256SUMS`, and creates the GitHub release at the dispatched commit
(`release`), a prerelease for every `0.y.z` and every `-preview.N`/`-rc.N`
version. A rerun keeps a release of this tag and commit, finishes a draft, and
never modifies a published release. `release.test.mjs` pins this contract.

## Checklist on the runic-sdk candidate

The Windows checklist (the [native Visual Studio check and its manual
checklist](../../visualstudio/test/README.md)) needs the train's runic-sdk release: its
template, `dotnet-runic` and npm packages. It runs against the runic-sdk **release
candidate**, not after the runic-sdk publish (D077.24): the package artifact of the
runic-sdk release commit's passed CI run, which is exactly what its publish ships.
runic-sdk's `eng/ci/candidate-feed.mjs` serves it to the Windows machine (runic-sdk
`eng/release/README.md`, "The editors checklist on the candidate").

| When | What |
| --- | --- |
| The runic-sdk release PR's merge queue run has passed (every Windows and macOS leg) | The coordinator runs `candidate-feed.mjs prepare --commit <runic-sdk release commit> --editors-run <this release PR's CI run>`, then `push` and `serve`. The machine gets `runic-candidate\` in the test user's home: the packages, both VSIX files, `NuGet.config`, `candidate.npmrc`, `bunfig.toml` and `Use-Candidate.ps1` |
| Right after, in parallel with runic-sdk's dry run, publish and indexing | The whole checklist on the candidate: the native check with `-NuGetConfig <home>\runic-candidate\NuGet.config -RunicToolVersion <runic-sdk version>` (and once with the default tool version), and the manual rows 1–16 on `runic-candidate\Use-Candidate.ps1 -New <name> -TemplateArguments '--frontend','react','--package-manager','bun'` |
| runic-sdk's `indexed` job has passed | The **check on the published packages** below, then the release PR records both and merges |
| After that | `candidate-feed.mjs clean` |

A project made with `Use-Candidate.ps1` restores the candidate's NuGet packages from its
folder feed into `runic-candidate\packages` and its `@runic-artifex` npm packages from
the registry `serve` forwards to `127.0.0.1:48731`, with npm's and Bun's caches inside
`runic-candidate\`. Nothing global on the machine changes, so a candidate never stands
in for a published package later, and `clean` removes it all.

**Check on the published packages** (about 5 minutes, after the runic-sdk publish is
indexed): it covers what only the real registries show. In an empty folder outside
`runic-candidate\`:

```powershell
$hive = Join-Path $PWD hive  # --debug:custom-hive needs an absolute path
dotnet new install Runic.Application.Templates::<version> --debug:custom-hive $hive
dotnet new runic-app -n Published --frontend react --package-manager bun --debug:custom-hive $hive
cd Published; dotnet build
```

A relative hive (`.\hive`) fails the install with "The template package location
…\hive\packages\….nupkg is not supported" and "no templates found".

The restore and the frontend install come from nuget.org and npm (the machine's caches
never saw the candidate). Then F5 once in each IDE: in Visual Studio (the experimental
instance with the VSIX) F5, **Yes**, and the Window shows the page; in VS Code (with
`runic-devkit.vsix`) F5 (**Runic: Debug the Window**), and the Window shows the page.
Open one help link of the Doctor section. Remove the folder afterwards.

**What cannot run on the candidate**, and goes into that check:

| Item | Why |
| --- | --- |
| The restore from nuget.org and npm (the package signatures nuget.org adds, the indexed versions, npm `latest`) | The candidate comes from a folder feed and a local registry |
| The template from nuget.org (`dotnet new install Runic.Application.Templates::<version>`) | It is on nuget.org only after the publish |
| Help links of `dotnet-runic` and the packages (row 13's help link to the tool's README) | They name runic-sdk's `v<version>` tag, which the publish creates; before it they answer 404 |

Every other row of the checklist runs on the candidate. The Translations rows use the
train's Translations release, which publishes before runic-sdk.

## Stores

The publish jobs follow D070.11–D070.14. Open VSX is deferred.

| Store | Job | Identity | Versions | Tool |
| --- | --- | --- | --- | --- |
| VS Code Marketplace | `marketplace-vscode` (ubuntu) | `runic-artifex.runic-devkit` | every release; `x.y.z-rc.N`/`-preview.N` with `--pre-release` | `@vscode/vsce` 4.1.1 (`vscode/bun.lock`): `vsce publish --packagePath <vsix> --oidc --skip-duplicate [--pre-release]` |
| Visual Studio Marketplace | `marketplace-visualstudio` (windows-2025) | `runic-artifex.runic-devkit-vs` | final versions only | `release.mjs` exchanges the job's GitHub OIDC token as vsce does, then `VsixPublisher.exe publish -payload <vsix> -publishManifest visualstudio/publishManifest.json -personalAccessToken <exchanged token>` from `Microsoft.VSSDK.BuildTools` 18.11.1098 ([VsixPub0047](#vsixpublisher-oidc-workaround-vsixpub0047), [VsixPub0031](#vsixpublisher-oidc-workaround-vsixpub0047)) |

The version mapping is the one in [IDE extension versions](#ide-extension-versions):
0.23.0 is VS Code `0.23.1000` (a normal Marketplace release; 0.22.0 still had the Preview label)
and Visual Studio `0.23.0.1000`; `1.0.0-rc.N` is VS Code `1.0.(500+N)` as a
Marketplace pre-release, which only users who opt in receive, and is not published
to Visual Studio, which has no pre-release channel and would offer it to everyone.
`1.0.0` (`1.0.1000`) then sorts above every release candidate.

**How a store job runs.** Both jobs run only without dry-run, after the GitHub
release exists: after the `publish` job of a release, or right after the
candidate job for store-only. Each runs in the `marketplace` environment with
`contents: read`, `id-token: write` and `attestations: read` and publishes
exactly the files of the GitHub release: `release.mjs fetch-release` downloads
both VSIX files and `SHA256SUMS` from the published release `v<version>`, checks
that the tag is at the candidate job's release commit, that the files match the
release's `SHA256SUMS` and the hashes the candidate job checked, and that each
file has a build-provenance attestation by this repository's `release.yml`, run on
`main` at the tag commit on a GitHub-hosted runner (`gh attestation verify
--repo Runic-Artifex/runic-editors --signer-workflow
Runic-Artifex/runic-editors/.github/workflows/release.yml --source-ref
refs/heads/main --source-digest <tag commit> --deny-self-hosted-runners`), and
checks the VSIX identities and versions. Then it runs `release.mjs store-publish
<store>`:

1. `store-check`: reads the store's public listing. A version listed with the same
   bytes and pre-release flag is skipped (so reruns are safe, and VsixPublisher,
   which overwrites a listed version, never runs twice); a version listed with
   other bytes or flag, or a newer listed version, fails the job.
2. It prints and runs the publish command. The job's GitHub OIDC token is
   exchanged for a short-lived Marketplace credential: by vsce itself
   (`--oidc`), and for Visual Studio by `release.mjs`, which hands the masked
   credential to VsixPublisher because VsixPublisher's own `-oidc` exchange is
   broken ([VsixPub0047](#vsixpublisher-oidc-workaround-vsixpub0047)). The
   workflow stores no token and removes `VSCE_PAT` from the tools' environment.
3. `store-verify` waits up to 20 minutes for the store's scan and checks that the
   store lists the version with the right pre-release flag and serves the release's
   bytes.

The **stores** input (`all`, `vscode`, `visualstudio`, `none`) selects the stores;
after a partial failure, dispatch again with the failed store
([Retrying a store](#retrying-a-store)): the GitHub release and a store that
already lists the version are kept. Use `none` while the store accounts do not
exist.

### Retrying a store

A store job that failed after the GitHub release is retried with a **store-only**
dispatch, also after a fix to the release tooling has landed on `main`:

1. Merge the fix to `main` (eng/version.json may already name the next version).
2. Dispatch **Release** from `main` with the released **version**, **stores**
   `visualstudio` (or `vscode`), and **dry-run** checked. The candidate job's log
   must show `release-check`'s `Store-only: GitHub release v<version> (…) is
   published at <tag commit>, an ancestor of <dispatched commit>; …`, then
   `fetch-release`'s line that the files of `v<version>` were downloaded, match
   its `SHA256SUMS` and are attested by `release.yml` on main at the tag commit,
   with the two `sha256sum` lines, then the store check and the exact publish
   command. For `visualstudio` that is the `Dry run, would exchange the job's
   GitHub OIDC token …` line and a `VsixPublisher.exe publish … -personalAccessToken
   <marketplace-token-from-github-oidc>` command: the placeholder, because a dry
   run requests no OIDC token.
3. Dispatch again without dry-run with the same inputs. The `publish` job is
   skipped (no `release` approval, no new release or attestation); the store job
   waits for the `marketplace` reviewers and publishes the release's files with the
   dispatched commit's workflow code.

Store-only is refused unless the release is published (not a draft) and its tag
commit is the dispatched commit or an ancestor of it; `all` and `none` are never
store-only, so a full release still refuses a tag at another commit. The store
job refuses files whose hashes differ from `SHA256SUMS` or the candidate job's, or
that lack the release workflow's attestation for the tag commit. A version the
store already lists with other bytes still fails `store-check`.

**Dry run.** The candidate job runs `store-rules <stores> <version> <vsix>`
([Store rules](#store-rules)) and `store-publish <stores> <version> <vsix>
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
manifest's `MoreInfo`, `ReleaseNotes`, `Icon`, `PreviewImage` and `Tags`, checked by
`package.py`, which also refuses `<Preview>true</Preview>` (no Preview label since 0.23).
VsixPublisher refuses a manifest `<Description>` that is missing or longer than
280 characters (`VsixPub0024`, which failed the Visual Studio publish of 0.23.0, released
there as 0.23.1); `package.py` and `store-publish visualstudio` refuse 280 too, as
VsixPublisher's message ("less than 280") says. Every other rule either store checks
at upload is in [Store rules](#store-rules). The icons are rendered from the
Runic Artifex brand mark by [`eng/brand/render-icons.sh`](../brand/render-icons.sh).
Q&A is off on both stores; questions go to GitHub issues (D070.14).

**VsixPublisher.** The extension still builds with `Microsoft.VSSDK.BuildTools`
18.9.820 (`visualstudio/RunicArtifex.DevKit.VisualStudio.csproj`). The publish job
restores 18.11.1098, not the newer 18.12.1606, whose VsixPublisher cannot
authenticate at all (VsixPub0031, see
[the workaround](#vsixpublisher-oidc-workaround-vsixpub0047)), from the
restore-only project [`vsixpublisher/`](vsixpublisher/VsixPublisher.csproj) in
locked mode, so publishing does not move the build toolchain. The project
references `Microsoft.NETFramework.ReferenceAssemblies` explicitly: the .NET SDK
adds it implicitly only where the .NET Framework targeting pack is missing (Linux,
not the Windows runner), so a lock file written on Linux failed the first Visual
Studio publish of 0.22.0 with `NU1004`. CI's "Visual Studio extension" job
(windows-2025) restores the project in locked mode on every change.

### Store rules

Both stores refuse an upload for rules their publish tools check before they send
anything, and the release runs those tools only in the store jobs, after the
GitHub release exists. So CI and the release workflow's candidate job (also in a
dry run) run the same checks offline, with the stores' own tools and without a
token: [`store-rules.mjs`](store-rules.mjs).

| Where | VS Code Marketplace | Visual Studio Marketplace |
| --- | --- | --- |
| CI, every PR | `vscode/package.mjs` on both packages it builds (the artifact, and the final-release check) | "Visual Studio extension" job (windows-2025) on the built VSIX: `release.mjs check-vsix visualstudio <vsix>` |
| Release workflow, candidate job (dry run included) | `release.mjs store-rules <stores> <version> artifacts/vsix`, on the files the stores would get | the same |
| Locally | `bun eng/release/release.mjs check-vsix vscode <vsix> [--pre-release]` (after `bun install` in `vscode`) | `bun eng/release/release.mjs check-vsix visualstudio <vsix>` (after `dotnet restore eng/release/vsixpublisher --locked-mode`) |

**VS Code Marketplace: vsce's own checks.** `vsce package` already checks the
source; `store-rules.mjs` calls `@vscode/vsce` 4.1.1 from `vscode/bun.lock` (the
version the publish job runs) on the packaged VSIX, with the package.json the
release stamped:

| Rule | Source in vsce 4.1.1 | Could ours break it? |
| --- | --- | --- |
| A readable VSIX: `extension/package.json` and `extension.vsixmanifest`; `@types/vscode` not newer than `engines.vscode` | `out/zip.js` `readVSIXPackage` | the stamping in `package.mjs` |
| `engines.vscode` present and valid; `name` and `publisher` match `^[a-z0-9][a-z0-9-]*$`; `version` is semver; `pricing` is Free or Trial; `main` with `activationEvents`; no SVG `icon`; badges over https, SVG badges only from trusted hosts; no `vscode` dependency; valid `extensionKind`, `sponsor`, `extensionDependencies`, `extensionPack` | `out/package.js` `validateManifestForPackaging` | yes, `vscode/package.json` |
| A pre-release needs `engines.vscode` >= 1.63 | `out/package.js` `ManifestProcessor` | yes, for `-rc.N` |
| Not the `vscode-samples` publisher; a `repository`; no `*` activation (vsce prompts, which aborts a non-interactive publish) | `out/package.js` `ManifestProcessor.onEnd` | yes, `vscode/package.json` |
| `--pre-release` only for a VSIX packaged as a pre-release | `out/publish.js` `publish` | yes, the version mapping |
| No proposed API (`enableProposedApi`, `enabledApiProposals`); no semver prerelease version (`1.0.0-rc.1`); a valid publisher | `out/publish.js` `validateManifestForPublishing` (not exported, restated) | yes, the version mapping |
| At most 30 keywords; a square PNG icon of at least 128 px; `qna: false`; https `homepage`, `bugs`, `repository`; README and CHANGELOG without SVG images or `http:` links | [Publishing extensions](https://code.visualstudio.com/api/working-with-extensions/publishing-extension) and the extension manifest reference; `listing.mjs` | yes, `vscode/package.json`, README, CHANGELOG |

**Visual Studio Marketplace: VsixPublisher's own code.** VsixPublisher has no
offline mode: `publish` authenticates before it checks anything. So
[`vsixpublisher-check`](vsixpublisher-check/Program.cs) loads the
`VsixPublisher.exe` the publish job uses (18.11.1098, restored by
[`vsixpublisher/`](vsixpublisher/VsixPublisher.csproj)) and calls the steps of its
`publish` command by reflection, in order, up to its first Marketplace request:
`PublishCommand.Validate` and `TryReadPublishManifest`, then
`Publisher.TryAddIdentityForVsixPayload`, the icon extraction,
`ValidatePublishManifest`, `MarketplaceManifestGenerator.GenerateManifest` and
`PayloadPackager.TryGetPackageStream`, which builds the package the upload would
send. A failure prints VsixPublisher's own message and exits with its error code
(`VSSDK: error VsixPub0024 : …`, exit 24). The .NET Framework program runs on
.NET 10 with `System.IO.Packaging` and `System.Security.Permissions`, on Linux and
Windows. The error codes are from VsixPublisher's `VsixPublisherOutputMessages`:

| Code | Rule | Could ours break it? |
| --- | --- | --- |
| VsixPub0001, 0002, 0022 | The payload and publish manifest exist; the payload is a file or an http(s) URL | the workflow paths |
| VsixPub0003, 0006 | `publishManifest.json` deserializes, with `identity.internalName`, `categories`, `overview` and `publisher`; every category and the `priceCategory` are known values | yes, `publishManifest.json` |
| VsixPub0016, 0017, 0018 | The VSIX has `extension.vsixmanifest`; the publish manifest sets no `vsixId`; the manifest parses, with Identity `Id`, a `System.Version` `Version` and `Language`, `<Description>`, `<DisplayName>`, and InstallationTargets with a known SKU `Id` and a version range | yes, `source.extension.vsixmanifest` and the version stamp |
| VsixPub0020 | The `<Icon>` is in the VSIX | yes |
| VsixPub0005, 0009, 0021 | A `publisher`; the `overview` file exists next to `publishManifest.json`; 1 to 3 categories | yes, `publishManifest.json` |
| VsixPub0023, 0024, 0025, 0026, 0027 | Each tag at most 50 characters; a `<Description>` of 1 to 280 characters; a valid `Language`; a `<DisplayName>` of 1 to 80; at least one InstallationTarget | yes, `source.extension.vsixmanifest`; **0024 failed 0.23.0** |
| VsixPub0028, 0033 | `repo` is an absolute http(s) URL; `internalName` matches `^[a-zA-Z0-9][a-zA-Z0-9-]*$` and has at most 63 characters | yes, `publishManifest.json` |
| VsixPub0034, 0036 | Asset file and payload names are valid package part names | the workflow paths |
| VsixPub0031 (binding redirects) | Every reference from VsixPublisher's publish path (`VsixPublisher`, `VsixCommandLine`, `Microsoft.VisualStudio.Services.*`, `Microsoft.Identity.Client*`) to an assembly the package ships in another version has a binding redirect in `VsixPublisher.exe.config` to that version. .NET Framework refuses the load otherwise, which VsixPublisher reports as VsixPub0031. 18.12.1606 has 8 such references, including `Microsoft.Identity.Client` 4.85.2.0 → 4.87.0.0; 18.11.1098 has none | a `Microsoft.VSSDK.BuildTools` update in `vsixpublisher/` |

One difference from the publish job: on .NET 10 with ICU, any well-formed
`Language` tag is a culture, while .NET Framework on Windows also refuses an
unknown one (VsixPub0025). `package.py` checks the manifest's other listing
fields, the images and `overview.md`.

**Checked only by the stores.** These need the store or a token and stay with the
store jobs: authentication and trusted publishing (VsixPub0031 from a bad
token, VsixPub0047, `vsce --oidc`), the publisher existing and owning the item
(VsixPub0030, VsixPub0032), a version already published (`store-check` reads it
from the public gallery in the dry run), the Marketplace's server-side checks
(VsixPub0029, VsixPub0043; vsce reports them as gallery errors), such as its
package scan and size limits, and whether the listing serves the uploaded bytes
(`store-verify`).

**The failures of 0.22 and 0.23.** Five Visual Studio store attempts failed. Three
are now caught in a PR and in the dry run: `NU1004` (a VsixPublisher lock file that
restored only on Linux; CI restores `vsixpublisher/` in locked mode on Windows since
#45, and the candidate job restores it too), VsixPub0031 in 18.12.1606 (the
binding-redirect check) and VsixPub0024 in 0.23.0 (VsixPublisher's own check; the
0.23.0 manifest fails it with exit 24). Two needed the Marketplace: VsixPub0047
(VsixPublisher's OIDC exchange, [worked around](#vsixpublisher-oidc-workaround-vsixpub0047))
and the byte check that read a non-public asset (fixed in #68).

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
landed in (D070.11). 0.22.0 itself was uploaded on the publisher's manage page
(the v0.22.0 release's `runic-devkit-vs.vsix`, checked against its `SHA256SUMS`
and attestation; a one-time exception, D072.26) after VsixPublisher 18.12.1606
failed twice (VsixPub0047, VsixPub0031). The next final release is the first
that the job publishes. The rules:

- **Final versions only** (D070.11). The job runs only for a version without `-`:
  `1.0.0-rc.N` and `-preview.N` versions reach Visual Studio users through the
  GitHub release only, because the store has no pre-release channel and would offer
  them to everyone.
- **Publisher** `runic-artifex`, display name "Runic Artifex", shared with the
  VS Code Marketplace (D070.12). The Microsoft account connected to the
  maintainer's GitHub account owns it; there is no second owner for now (D070.16).
- **Trusted publishing only** (D070.14). The `marketplace` environment has
  required reviewers, deployment branch `main` only and no secrets; the job
  exchanges its GitHub OIDC token for a short-lived credential and hands it to
  VsixPublisher ([VsixPub0047](#vsixpublisher-oidc-workaround-vsixpub0047),
  D072.25). Never add a personal access token.
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
   through its manual checklist, on the runic-sdk release candidate and then the
   short check on the published packages
   ([Checklist on the runic-sdk candidate](#checklist-on-the-runic-sdk-candidate)).
   Record the result in the release PR.
2. Dispatch **Release** from `main` with the version and **dry-run** checked. In the
   candidate job's log, `store-publish visualstudio … --dry-run` must show the
   `runic-artifex.runic-devkit-vs` identity from `publishManifest.json`, report the
   version as not yet listed (a listed version with other bytes, or a newer listed
   version, fails here), say that it would exchange the job's GitHub OIDC token at
   `POST https://marketplace.visualstudio.com/_apis/gallery/token?api-version=7.2-preview.1`,
   and print the `VsixPublisher.exe publish -payload … -publishManifest
   visualstudio/publishManifest.json -personalAccessToken
   <marketplace-token-from-github-oidc>` command.
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
   the page must show no Preview label from 0.23.0 on (VsixPublisher has mishandled
   the flag before: microsoft/vsmarketplace#905), the icon, the overview and no Q&A tab. Then install
   it in Visual Studio 2026 through Extensions > Manage Extensions and check that
   Tools > Runic appears in a Runic solution.
6. If the job failed after the GitHub release, fix the cause on `main` and
   retry the store with a store-only dispatch ([Retrying a store](#retrying-a-store)):
   **stores** `visualstudio`, dry run first. It publishes the GitHub release's own
   `runic-devkit-vs.vsix` (checked against `SHA256SUMS` and its attestation), not a
   new build; the GitHub release and the VS Code listing stay as they are.

#### VsixPublisher OIDC workaround (VsixPub0047)

`VsixPublisher.exe publish -oidc` of `Microsoft.VSSDK.BuildTools` 18.12.1606, the
newest on nuget.org, and of 18.11.1098 (the same VsixPublisher code) cannot
publish: its exchange POSTs the GitHub OIDC token to
`https://marketplace.visualstudio.com/_apis/gallery/token` without an
`api-version` (and with `Authorization: Bearer`), and the Marketplace refuses it
with `VsixPub0047: Could not exchange the OIDC token for a Marketplace session
token: {"message":"No api-version was supplied for the \"POST\" request. …",
"typeName":"Microsoft.VisualStudio.Services.WebApi.VssVersionNotSpecifiedException"}`.
This failed the store-only retry of 0.22.0 (run 44467138693), while `vsce publish
--oidc` against the same Marketplace and publisher succeeded.

So `store-publish visualstudio` exchanges the token itself, exactly as
`@vscode/vsce` 4.1.1 does (`out/oidc.js`), in `release.mjs` `marketplaceToken`:

1. `GET $ACTIONS_ID_TOKEN_REQUEST_URL&audience=marketplace.visualstudio.com` with
   `Authorization: Bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN` (the job's
   `id-token: write`); the GitHub OIDC token is the response's `value`.
2. `POST https://marketplace.visualstudio.com/_apis/gallery/token?api-version=7.2-preview.1`
   with `Authorization: FederatedToken <GitHub OIDC token>`, `Content-Type` and
   `Accept: application/json` and the body `{"publisherName":"runic-artifex"}`;
   the Marketplace token is the response's `credential`. The publisher's Trusted
   Publishing policy (`Runic-Artifex/runic-editors`, `release.yml`, environment
   `marketplace`) decides whether the exchange succeeds.
3. Both tokens are masked (`::add-mask::`) before anything else is logged, and
   VsixPublisher gets the credential as `-personalAccessToken`, its only token
   input: it reads none from the environment or stdin, and its `-oidc` path
   feeds its own exchanged credential into the same `VssBasicCredential`. The
   shown command has the placeholder `<marketplace-token-from-github-oidc>`, and
   VsixPublisher runs without the `ACTIONS_ID_TOKEN_REQUEST_*` variables. The
   token expires on its own and is never written anywhere.

`release.test.mjs` pins the requests, the masking, the dry run without a token,
and the failure messages, and compares the exchange with the installed vsce. When
Microsoft fixes `-oidc`, set `VSIXPUBLISHER_NATIVE_OIDC` in `release.mjs` to
`true` and `store-publish` runs `VsixPublisher.exe publish … -oidc` again.
That needs a `Microsoft.VSSDK.BuildTools` newer than 18.12.1606 with both bugs
fixed: 18.11.1098 has the same broken `-oidc`.

**VsixPub0031 in 18.12.1606.** The job therefore pins 18.11.1098
([`vsixpublisher/`](vsixpublisher/VsixPublisher.csproj)). With 18.12.1606 the
exchange above succeeded (run 44480434131), and VsixPublisher then failed every
authentication with `VSSDK: error VsixPub0031 : An error occurred while
attempting to authenticate: Could not load file or assembly
'Microsoft.Identity.Client, Version=4.85.2.0, …' … The located assembly's
manifest definition does not match the assembly reference.` Its
`tools/vssdk/bin/VsixPublisher.exe.config` has 8 `dependentAssembly` binding
redirects; 18.11.1098's has 44, including `Microsoft.Identity.Client` and
`Microsoft.Identity.Client.Extensions.Msal` → 4.87.0.0 (the bundled
`lib/Microsoft.Identity.Client.dll`) and `Microsoft.IdentityModel.*` → 8.15.0.0.
18.11.1098's VsixPublisher has the same `publish` arguments (`-payload`,
`-publishManifest`, `-personalAccessToken`, `-oidc`) and publish-manifest model.

Report or follow both bugs in the Marketplace's issue tracker,
<https://github.com/microsoft/vsmarketplace/issues>:

1. `-oidc` POSTs to `_apis/gallery/token` without `api-version` (VsixPub0047,
   18.11.1098 and 18.12.1606).
2. 18.12.1606's `VsixPublisher.exe.config` lacks the `Microsoft.Identity.Client`,
   `Microsoft.Identity.Client.Extensions.Msal` and `Microsoft.IdentityModel.*`
   binding redirects that 18.11.1098 has, so authentication fails with
   VsixPub0031 (`Microsoft.Identity.Client, Version=4.85.2.0` manifest
   mismatch).

If VsixPublisher rejects the exchanged token (the publish step fails after the
`Exchanged the job's GitHub OIDC token …` line), the maintainer decides again
(D072.25): upload the release's `runic-devkit-vs.vsix` on the publisher's manage
page, or defer the Visual Studio listing.

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
only on `main`, released those bytes; the store jobs run this check, pinned to the
tag commit, before publishing. The workflow does not build them: it takes
them from the successful CI run of the same commit and checks them by hash first.
After publishing, `store-verify` checks that each Marketplace serves the same bytes.
