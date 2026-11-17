# Contributing

Use the locked development environment through `direnv exec . <command>` or
`nix develop . -c <command>` (see the [README](README.md#development-environment)).
Run focused checks while changing an extension; GitHub runs the Windows build.

## Checks

Pull requests must pass the `verify` job in [CI](.github/workflows/ci.yml), which
requires every other CI job to succeed. Run the matching command locally before
pushing:

| CI job | Local command |
| --- | --- |
| Source checks | `actionlint`, `bun test eng`, `bun eng/translations.mjs --check`, `python3 visualstudio/package.py --check-source` and `dotnet run --project visualstudio/Core.Tests` |
| VS Code extension | `bun install --frozen-lockfile && bun run --bun check && bun run --bun test && bun run package` in `vscode`, then `dotnet tool restore` and `bun run test:host` (needs an installed `code` or `VSCODE_EXECUTABLE_PATH`) |
| Visual Studio extension | Windows only: `msbuild visualstudio/RunicArtifex.DevKit.VisualStudio.csproj /restore /m:1 /p:Configuration=Release /p:RestoreLockedMode=true`, then `python visualstudio/package.py --configuration Release` and `dotnet run --project visualstudio/Core.Tests`. On Linux, `dotnet build visualstudio` cross-compiles it |

The VS Code host test and CI run the language server of the
`dotnet-runic-translations` release in `.config/dotnet-tools.json`, restored from
NuGet; neither builds runic-translations-sdk. To test against a local server build,
set `RUNIC_TRANSLATIONS_SERVER_ASSEMBLY` (VS Code host test) or
`RUNIC_TRANSLATIONS_SERVER` (Visual Studio) to its `dotnet-runic-translations.dll`.

## Dependencies

Pin exact versions: npm dependencies through `vscode/bun.lock`, NuGet packages
in the project with `visualstudio/packages.lock.json`, actions by commit SHA with
the version in a comment. Do not adopt a third-party npm or NuGet package version,
action version or tool release published the same day; first-party Runic packages
are exempt.

## Releases

See [Releasing the editor extensions](eng/release/README.md). Nothing is
published to an extension marketplace.
