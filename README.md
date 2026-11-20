# Runic editor extensions

The editor extensions for [Runic](https://github.com/Runic-Artifex): the Runic
SDK's development loop (`dotnet runic dev`, F5 of the Window, doctor, the
ViewModel/Bridge tree) and authoring direct `.mf2` and grouped `.rmf2` Runic
Translations resources through the Runic language server.

| Directory | Extension | Package |
| --- | --- | --- |
| [`vscode/`](vscode/README.md) | **Runic DevKit** for VS Code: Runic SDK and Translations. Store ID `runic-artifex.runic-devkit` | `runic-devkit.vsix` |
| [`visualstudio/`](visualstudio/README.md) | **Runic DevKit** for Visual Studio 2026 (Windows x64): Runic SDK and Translations. Store ID `runic-artifex.runic-devkit-vs`, VSIX identity `RunicArtifex.DevKit.VisualStudio` | `runic-devkit-vs.vsix` |

Install them from the stores (since 0.22.0) or from a VSIX file:

| Editor | Store | VSIX |
| --- | --- | --- |
| VS Code | [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit) (`code --install-extension runic-artifex.runic-devkit`); release candidates as pre-releases | `runic-devkit.vsix` from the [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases) |
| Visual Studio 2026 | [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit-vs) (**Extensions > Manage Extensions**); final versions only | `runic-devkit-vs.vsix` from the GitHub releases, also for release candidates |
| VSCodium, Cursor, Windsurf and other Open VSX editors | Not on Open VSX (deferred, D070.15) | `runic-devkit.vsix` from the GitHub releases |

Both moved here from
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk)
with their history (`tools/vscode-runic-translations`,
`tools/visualstudio-runic-translations`). Each release
attaches both VSIX files to
the [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases)
of this repository and, since 0.22.0, publishes them to the Marketplaces above
([Stores](eng/release/README.md#stores)). Since 0.20 the VS Code
extension is the one Runic extension for the Runic SDK and Translations; its Runic
SDK views stay hidden in workspaces without a Runic SDK project. Since 0.22 the
Visual Studio extension has them too: `dotnet runic dev`, Debug the Window with
.NET and page breakpoints in one session, doctor and the ViewModels tree. The
[Visual Studio README](visualstudio/README.md#visual-studio-and-vs-code)
compares the two extensions feature by feature.

Since 0.22 both extensions are named **Runic DevKit** and have new identities. The
editors install them next to the former ones, so uninstall those: **Runic**
(`runic-artifex.runic`, 0.20–0.21) or **Runic MF2 Translations**
(`runic-artifex.runic-translations`, up to 0.11) in VS Code ([Moving from Runic or
Runic MF2 Translations](vscode/README.md#moving-from-runic-or-runic-mf2-translations)),
and **Runic MF2 Translations** (`Runic.Artifex.Translations.Rmf2`) in Visual Studio
([Moving from Runic MF2 Translations](visualstudio/README.md#moving-from-runic-mf2-translations)).
Both extensions say so when they find a former one.

## Language server and supported Translations versions

Neither extension bundles a language server. Each runs the workspace's own
`dotnet-runic-translations` .NET tool (`dotnet tool run runic-translations -- lsp`),
so the server always matches the project's Translations release. Install it in
the repository or solution root with the same version as the project's
`Runic.Translations` packages:

```sh
dotnet new tool-manifest --output .config
dotnet tool install dotnet-runic-translations --version <VERSION>
```

| Translations (`dotnet-runic-translations`) | Extensions |
| --- | --- |
| 0.14.0 | runic-editors 0.22.0 and this repository's `main`; CI runs the VS Code host test against this release from NuGet, with its protocol 2 refactorings (F2 for inputs and slots, Extract Group and Inline Resource) |
| 0.13.0 | runic-editors 0.21.0; 0.22.0 still works with it, without the protocol 2 refactorings |
| 0.12.0 | runic-editors 0.20.0 |
| 0.11.0 and older | The VSIX files attached to that [Translations release](https://github.com/Runic-Artifex/runic-translations-sdk/releases) |

The `lsp` protocol is outside the Translations 1.0 compatibility rules, so use a
Translations release listed here. The tested release is the version in
[`.config/dotnet-tools.json`](.config/dotnet-tools.json); see
[Language server](eng/release/README.md#language-server) for how to move it.

## Development environment

On NixOS (or any system with Nix), the flake provides the pinned .NET SDK
(`global.json`), Node (`.node-version`), Bun, Python and actionlint:

```sh
direnv allow      # or: nix develop
dotnet tool restore
```

Without Nix, install the .NET SDK of `global.json`, Node of `.node-version`, Bun
1.4.4 and Python 3.

## Build, test and package

VS Code extension (any platform), from `vscode/`:

```sh
bun install --frozen-lockfile
bun run --bun check        # TypeScript
bun run --bun test         # unit tests
bun run package            # artifacts/runic-devkit.vsix
bun run test:host          # VS Code extension host against the pinned language server (needs `code`)
```

Visual Studio extension: the installable VSIX needs Visual Studio's full-framework
MSBuild on Windows:

```powershell
msbuild visualstudio/RunicArtifex.DevKit.VisualStudio.csproj /restore /m:1 /p:Configuration=Release
python visualstudio/package.py --configuration Release   # visualstudio/artifacts/runic-devkit-vs.vsix
```

On Linux, `dotnet build visualstudio` cross-compiles the extension (no VSIX) and
`python3 visualstudio/package.py --check-source` checks the source manifest. The
native integration check is described in the
[Visual Studio extension README](visualstudio/README.md#windows-integration-check).

Repository checks: `bun test eng` (version mapping and release scripts),
`bun eng/translations.mjs --check` (bundled `runic.json` schema) and `actionlint`.

Both packages are stamped with the version in `eng/version.json`; see
[Releasing the editor extensions](eng/release/README.md).

## License

[MIT](LICENSE)
