# Working on the Runic editor extensions

Use the locked development environment described in README.md and .envrc
(`direnv exec . <command>`).

This repository holds two independent extensions: `vscode/` (TypeScript, Bun) and
`visualstudio/` (C#, Visual Studio SDK, Windows-only packaging). Both are
**Runic DevKit** since 0.22 (D070.5). Keep both extensions' identities
(`runic-artifex.runic-devkit` and `RunicArtifex.DevKit.VisualStudio`, with the store
names in `eng/release/release.mjs` `MARKETPLACE`, which are permanent once
published), settings and commands stable unless a change is about them. The VS Code
extension keeps reading its former `runicTranslations.*` settings and running its
former command IDs, and both extensions ask users to uninstall their former
identities (`vscode/src/identity.ts`, `visualstudio/Migration/`).

The language server is not built here: the extensions run the workspace's
`dotnet-runic-translations` tool, and this repository tests against the version in
`.config/dotnet-tools.json`, restored from NuGet. Do not add a build of
runic-translations-sdk to CI. The VS Code extension's Runic SDK features run the
project's `dotnet-runic` tool and read the contracts runic-sdk documents (the dev
session file, doctor JSON, the development source map); the host test uses
fixtures for them, not a build of runic-sdk.
Both extensions accept the tool IDs before and after the rename (D079, D080.7):
`dotnet-runic` / `dotnet runic` and `RunicArtifex.Tool` / `dotnet rax`,
`runic-translations` and `rax-translations`, in `.config/dotnet-tools.json` or
`dotnet-tools.json` (`vscode/src/workspace.ts`, `visualstudio/Core/RunicTools.cs`).

Match verification to the changed behavior. Run the focused checks of the
extension you changed (CONTRIBUTING.md); GitHub owns the Windows build and the
full CI run. Passing the relevant checks completes verification unless new
evidence justifies more work. Track nonblocking limitations as issues.

Versions come from `eng/version.json` and are mapped by
`eng/release/ide-versions.mjs`; never commit a release number into
`vscode/package.json` or `visualstudio/source.extension.vsixmanifest`. Releases
are GitHub releases, which the release workflow then publishes to the VS Code
Marketplace and (final versions only) the Visual Studio Marketplace through
trusted publishing (eng/release/README.md, "Stores"); Open VSX is deferred.
