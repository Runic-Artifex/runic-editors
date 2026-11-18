# Changelog

The VS Code extension's changes per runic-editors release. Releases are GitHub
releases of [runic-editors](https://github.com/Runic-Artifex/runic-editors/releases)
and, from 0.22.0, versions on the VS Code Marketplace.

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
