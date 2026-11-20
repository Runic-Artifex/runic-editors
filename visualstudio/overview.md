# Runic DevKit for Visual Studio

Runic DevKit is the Visual Studio 2026 (18.x, x64) extension for the
[Runic SDK](https://github.com/Runic-Artifex) and Runic Translations. Before 1.0, a
minor release can still change its commands and environment variables; the
release notes list every change.

## Runic SDK

In a solution with a Runic SDK project, **Tools → Runic** and the **Runic** tool
window (**View → Other Windows → Runic**) bring the Runic SDK's development loop:

- start and stop `dotnet runic dev` (with or without its Window), follow its
  session in the tool window's **Dev Session** section and the status bar, and
  read its diagnostics in the Error List;
- F5 and Ctrl+F5 of a Runic startup project offer to start `dev --no-window`
  first;
- **Debug the Window**: the Window's C# code and its page in one debugging
  session, and **Attach to the Page (JavaScript)** for a running page;
- **Run dotnet runic doctor**: the checks in the tool window, failing and
  warning checks in the Error List;
- the **ViewModels** tree of the development source map, which opens the C#
  members.

## Runic Translations

Direct `.mf2` and grouped `.rmf2` translation resources get the Runic language
server's features in the editor:

- highlighting, diagnostics for every file of the project, completion, hover with
  the base-locale text, symbols, folding and formatting;
- go to definition, Find All References, quick fixes and F2 rename of resources
  and MF2 locals, and with Translations 0.14 of inputs and slot references;
- **Extract group** and **Inline into the ancestor resource** in the light bulb
  (Translations 0.14);
- **Tools → Runic → Preview Message**: a native message preview with locale,
  pseudo-locale, sample-value, regional-locale and time-zone selectors;
- XLIFF export and import, .resx and i18next catalog import, library message
  overrides, and each locale's coverage in the tool window's **Translations**
  section;
- unsaved `runic.json` edits take part in diagnostics, preview and refactorings.

Settings are in **Tools → Options → Runic DevKit**; the tools' output goes to the
**Runic SDK** and **Runic Translations** Output panes.

## Requirements

The extension does not bundle a language server. It runs the solution's own
`dotnet-runic-translations` .NET tool, so the server always matches the project's
Translations release. Install it next to the solution with the version of the
project's `Runic.Translations` packages, then restore it:

```sh
dotnet new tool-manifest --output .config
dotnet tool install dotnet-runic-translations --version <VERSION>
dotnet tool restore
```

See [Language server and supported Translations versions](https://github.com/Runic-Artifex/runic-editors#language-server-and-supported-translations-versions)
for the releases each extension version supports.

## Moving from Runic MF2 Translations

Up to 0.21 this extension was **Runic MF2 Translations**
(`Runic.Artifex.Translations.Rmf2`). Visual Studio installs Runic DevKit next to it,
so uninstall Runic MF2 Translations in **Extensions → Manage Extensions →
Installed**, then restart Visual Studio. Runic DevKit shows an InfoBar when it
finds the former extension. See [Moving from Runic MF2 Translations](https://github.com/Runic-Artifex/runic-editors/blob/main/visualstudio/README.md#moving-from-runic-mf2-translations).

## Releases and support

- Release candidates are not published here: this Marketplace has no pre-release
  channel, so it gets final versions only. Every release, release candidates
  included, is also a VSIX file on the
  [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases), with
  `SHA256SUMS` and a build-provenance attestation.
- Release notes: [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases).
- Questions and bugs: [GitHub issues](https://github.com/Runic-Artifex/runic-editors/issues).
- Source and license (MIT): [Runic-Artifex/runic-editors](https://github.com/Runic-Artifex/runic-editors).
