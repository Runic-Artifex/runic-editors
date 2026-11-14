# Runic editor extensions

The editor extensions for [Runic](https://github.com/Runic-Artifex): authoring
direct `.mf2` and grouped `.rmf2` Runic Translations resources through the Runic
language server.

| Directory | Extension | Package |
| --- | --- | --- |
| [`vscode/`](vscode/README.md) | Runic MF2 Translations for VS Code (`runic-artifex.runic-translations`) | `runic-translations.vsix` |
| [`visualstudio/`](visualstudio/README.md) | Runic MF2 Translations for Visual Studio 2026 (`Runic.Artifex.Translations.Rmf2`) | `runic-translations-visualstudio.vsix` |

Both moved here from
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk)
with their history (`tools/vscode-runic-translations`,
`tools/visualstudio-runic-translations`). Both are **previews** until the
production language server (2.0). They are released as VSIX files attached to
the [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases)
of this repository, not on an extension marketplace. The VS Code extension will
become the one Runic extension for Translations and the Runic SDK
([#2](https://github.com/Runic-Artifex/runic-editors/issues/2)).

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
| 0.11.0 | This repository's `main`; CI runs the VS Code host test against this release from NuGet |
| Older releases | The VSIX files attached to that [Translations release](https://github.com/Runic-Artifex/runic-translations-sdk/releases) |

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
bun run package            # artifacts/runic-translations.vsix
bun run test:host          # VS Code extension host against the pinned language server (needs `code`)
```

Visual Studio extension: the installable VSIX needs Visual Studio's full-framework
MSBuild on Windows:

```powershell
msbuild visualstudio/Runic.Translations.VisualStudio.csproj /restore /m:1 /p:Configuration=Release
python visualstudio/package.py --configuration Release   # visualstudio/artifacts/runic-translations-visualstudio.vsix
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
