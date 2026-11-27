# Changelog

The VS Code extension's changes per runic-editors release. Releases are GitHub
releases of [runic-editors](https://github.com/Runic-Artifex/runic-editors/releases)
and, from 0.22.0, versions on the VS Code Marketplace.

## 0.24.0

**Frontend dependencies.** With runic-sdk 0.24's library packages, the Dev
Session view lists the frontend packages a Window project's page imports, with
their owner and state, and offers **Build Frontend Dependencies (dotnet
build)**, a click-to-install offer on a fresh clone and quick fixes for the
dependency diagnostics. Nothing is installed or built without a click
(runic-editors#73).

- Only Window projects are `dotnet runic dev` targets: client libraries and test
  projects are no longer offered for `dev`, F5, the `runic` tasks or doctor.
- The `runic` launch keeps `args`, `cwd`, `envFile`, `env`, the launch profile
  (`launchProfile`) and every C# debugger option; MSBuild's `RunArguments`, the
  profile's arguments and `args` are all passed, split as .NET splits them.
  `devArgs` passes options to the `dev --no-window` that F5 starts. Run and Debug
  lists one entry per Window project and launch profile, each once, named after
  the project (runic-editors#80, #81).
- When the `dev` that F5 starts exits early, F5 ends at once with `dev`'s exit
  code and last lines, and offers **Restore tools** on a fresh clone
  (runic-editors#81).
- Restart (Ctrl+Shift+F5) builds again: DevKit's build is the launch's
  `preLaunchTask` (runic-editors#83).
- A library's TypeScript error appears once in Problems, in the package's file,
  and the dependency tree shows a failed package build as "failed"
  (runic-editors#83). With runic-sdk 0.24, both also work when `dev` was
  started as the template's `runic: dev` task, from the session file's failure
  (runic-editors#85).
- A launch profile value that isn't a string (`"PORT": 8080`) is refused with a
  message instead of being dropped.
- The Restore tools and package-manager tasks no longer log "no registered task
  type".
- Accepts `RunicArtifex.Tool` (`dotnet rax`) and `rax-translations` next to
  `dotnet-runic` and `runic-translations`; the `rax migrate` offer appears only
  once `RunicArtifex.Tool` is available (runic-editors#72).
- Tested against `dotnet-runic-translations` 0.16.0 (language server protocol 2).
  Translations 0.15.1 keeps working. The bundled `runic.json` schema is
  unchanged.

## 0.23.1

No change to the VS Code extension. 0.23.1 is a Visual Studio Marketplace
release; the VS Code Marketplace keeps 0.23.0 (`0.23.1000`), and the
`runic-devkit.vsix` of the 0.23.1 GitHub release (`0.23.2000`) has the same
features.

## 0.23.0

**No Preview label.** Runic DevKit is published without the Marketplace
"Preview" label (runic-editors#50). Before 1.0, a minor release can still change
commands and settings; the release notes list every change.

- Tested against `dotnet-runic-translations` 0.15.1, which fixes a flaw in
  0.15.0's pack loading. Its language server speaks only protocol 2. The
  extension has asked for protocol 2 since 0.22, and Translations 0.14.0 keeps
  working. The bundled `runic.json` schema is unchanged.
- With `dotnet-runic` 0.23, Vite errors that `dotnet runic dev` prints
  (`RUNICP008`) are problems at their file and line, removed when `dev` prints
  that the frontend errors cleared. A successful .NET build keeps them.
  `RUNICP` codes link to runic-sdk's diagnostics reference (runic-editors#52).
- When `dev` doesn't stop on its input, the forced stop signals the `dotnet`
  launcher's whole process group off Windows, so `dev`, its frontend server and
  its Window end too (runic-editors#53).

## 0.22.0

**VS Code Marketplace.** Runic DevKit is published to the VS Code Marketplace as
[runic-artifex.runic-devkit](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit),
with an icon and store metadata; the VSIX stays on the GitHub release. It is not on
Open VSX for now. See [Install](README.md#install).

**Runic DevKit.** The extension is renamed **Runic DevKit**
(`runic-artifex.runic-devkit`, `runic-devkit.vsix`), the name it will have on the
extension marketplaces. Command IDs, settings, views and Command Palette titles are
unchanged. VS Code installs it next to **Runic** (`runic-artifex.runic`); on
activation it offers once to uninstall Runic or Runic MF2 Translations when either
is still installed. See [Moving from Runic or Runic MF2
Translations](README.md#moving-from-runic-or-runic-mf2-translations).

- Tested against `dotnet-runic-translations` 0.14.0 and its language server
  protocol 2: F2 renames inputs and slot references, the light bulb offers
  Extract Group and Inline Resource, and a resource rename next to application
  sources asks first. The palette commands open the same rename box or code
  action; with 0.13.0 they work as in 0.21. The bundled `runic.json` schema is
  unchanged.
- A light-bulb refactoring (Inline Resource, Extract Group) that the server
  rejects as outdated, because files changed between the code action and its
  resolve (a save's file-watcher event), is resolved once more; if it is still
  outdated, a warning says so instead of nothing happening (runic-editors#32).
- In a multi-root workspace, each folder's language server starts even when
  several folders hold Translations projects.
- With `dotnet-runic` 0.22, the extension's `dev` task passes `--debug-endpoint`
  to a `dev` with its Window, so a CS-WebUI Window opens its DevTools Protocol
  port, and **Stop dotnet runic dev** asks `dev` to stop on its standard input,
  so it marks its session stopped and removes its development pages, on Windows
  too; after 10 seconds it is ended by force. Older tools run as in 0.21. The
  `dev` terminal ends when the `dev` process exits, even while a child it
  started still holds its output (runic-editors#41).
- When a `dev` was killed (its session file still says starting or ready, but
  the process is gone), the extension touches `obj/runic/dev-stopped` once, as
  `dev` does when it stops, so the next build restores the production frontend
  in `www/` (runic-editors#29).
- A light-bulb refactoring that the server refuses for another reason, such as
  Extract Group from a catalog where a locale lacks the group, shows the server's
  reason as a warning instead of applying nothing without a message; only an
  outdated one is resolved again (runic-editors#38).
- The palette's refactorings work right after a save. The server's "changed
  while pending" answer reached VS Code as no code actions or an empty outline,
  so Extract Group and Inline Resource applied nothing, and Extract Group and
  Rename Resource reported that the outline had no such group. They now ask
  again for a few seconds, apply the code action they listed, and warn when the
  server still lists none. A refactoring command whose error message is still
  open no longer stays running (runic-editors#40).
- `dotnet runic dev`'s build errors open their file again: the `$runic-dev`
  problem matchers skip the `[host] ` label that `dev` writes before each build
  line, which had become part of the file. With `dotnet-runic` 0.22, a fixed C#
  error leaves Problems once `dev` prints `[dev] Ready` after the fix, and the
  warnings stay; the extension's own `dev` terminal reports these problems
  (runic-editors#36).

## 0.21.0

- The **ViewModels** view reads the development source map of Runic SDK 0.21
  (format ID `runic.views.bridge-sources/1`) and still reads that of 0.20
  (`runic-bridge-sources/1`).
- Tested against `dotnet-runic-translations` 0.13.0; the bundled `runic.json`
  schema is unchanged. **Import XLIFF (dry run)** works with 0.12.0 and 0.13.0.

## 0.20.0

**One Runic extension.** The Translations extension, Runic MF2 Translations
(`runic-artifex.runic-translations`, `runic-translations.vsix`), became **Runic**
(`runic-artifex.runic`, `runic.vsix`), which adds the Runic SDK features. Uninstall
the former extension when you install this one; see [Moving from Runic MF2
Translations](README.md#moving-from-runic-mf2-translations).

- Settings moved to `runic.dotnetPath`, `runic.translations.serverAssembly` and
  `runic.translations.trace.server`; the former `runicTranslations.*` settings
  still apply while the new ones are not set.
- Commands moved from `runicTranslations.*` to `runic.translations.*`; the former
  IDs still run, so keybindings keep working. Command Palette titles are unchanged.
- Runic SDK features, shown only in workspaces with a Runic SDK project:
  - **Dev Session** view and **Runic dev** status bar item: start and stop
    `dotnet runic dev` (or `--no-window`) as a task with the `$runic-dev` problem
    matchers, or the template's `runic: dev` tasks when the workspace has them;
    follow `obj/runic/dev-session.json`, where a `"starting"` or `"ready"` session
    whose `dotnet runic dev` process no longer runs reads as stopped.
  - F5 of the Window with `dotnet runic dev --no-window`: the template's `Window`
    configuration, or the extension's `runic` debug configuration, which starts
    the frontend, builds and debugs the Window with the C# extension.
  - **Doctor** view: `dotnet runic doctor` results, with failing and warning
    checks in the Problems panel.
  - Quick fixes for Runic diagnostic codes (`RUNICBRIDGE`, `RAPPDEV`, `RAPPCLI`,
    `RAPPPKG`, `RCLI`): the code's entry in the diagnostics catalog at the tool's
    release.
  - **ViewModels** view: the ViewModels and Bridge members of the development
    source map, opening their C# members.
- **Translations** view: `runic-translations status` coverage per locale, in any
  workspace with a `runic.json`.

Everything the Translations extension did is unchanged.

## 0.11.0 and earlier

Released as Runic MF2 Translations with Runic Translations
([runic-translations-sdk releases](https://github.com/Runic-Artifex/runic-translations-sdk/releases)).
