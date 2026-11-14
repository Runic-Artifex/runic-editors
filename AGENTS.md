# Working on the Runic editor extensions

Use the locked development environment described in README.md and .envrc
(`direnv exec . <command>`).

This repository holds two independent extensions: `vscode/` (TypeScript, Bun) and
`visualstudio/` (C#, Visual Studio SDK, Windows-only packaging). Keep both
extensions' identities (`runic-artifex.runic-translations`,
`Runic.Artifex.Translations.Rmf2`), settings and commands stable unless a change
is about them.

The language server is not built here: the extensions run the workspace's
`dotnet-runic-translations` tool, and this repository tests against the version in
`.config/dotnet-tools.json`, restored from NuGet. Do not add a build of
runic-translations-sdk to CI.

Match verification to the changed behavior. Run the focused checks of the
extension you changed (CONTRIBUTING.md); GitHub owns the Windows build and the
full CI run. Passing the relevant checks completes verification unless new
evidence justifies more work. Track nonblocking limitations as issues.

Versions come from `eng/version.json` and are mapped by
`eng/release/ide-versions.mjs`; never commit a release number into
`vscode/package.json` or `visualstudio/source.extension.vsixmanifest`. Releases
are GitHub releases (eng/release/README.md); nothing is published to an
extension marketplace.
