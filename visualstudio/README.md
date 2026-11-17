# Runic DevKit for Visual Studio

**Preview** for Visual Studio 2026 (18.x), Windows x64. The extension is a preview
product until the production language server (2.0): its commands, environment
variables and the `lsp` protocol it uses are outside the 1.0 compatibility rules and
may change in a minor release. The manifest
declares `[18.0,19.0)`. Visual Studio 2022 (17.x) is not supported. The
in-process API is pinned to 17.14, which 18.x hosts provide. Native evidence
covers Visual Studio Community 2026 18.8.2; the complete native journey passed
on 2026-10-07 (see
[RMF2 validation](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/rmf2-validation.md#visual-studio-2026-journey-after-the-server-fix-2026-10-07)). The range declaration is not
evidence that every 18.x release has been run. The MEF client
registers direct `.mf2` and grouped `.rmf2` with Visual Studio's remote-code content type and uses the same
stdio language server as VS Code. TextMate highlighting, diagnostics, completion,
hover, symbols, definition/references, formatting and bounded F2 resource/local
rename use the shared compiler and language service.

Restore the solution's local `runic-translations` .NET tool before opening an
MF2 document. Activation finds the nearest `.config/dotnet-tools.json` from the
active document (or solution), respecting `isRoot`, and starts
`dotnet tool run runic-translations -- lsp`. The installed tool requires its
matching .NET runtime; it runs outside Visual Studio's .NET Framework process.
No tool download or restore occurs during activation. Use trusted projects.

For development, set `RUNIC_TRANSLATIONS_SERVER` to an absolute built tool DLL
before starting Visual Studio. `RUNIC_DOTNET` optionally selects the installed
.NET executable. Initialization failures appear in an IDE notification; server
stderr is drained to diagnostic trace output. One server serves the current IDE
session. Restart it after switching solutions or changing the selected tool.

## Install

- **Visual Studio Marketplace** (final versions; from 0.22.0 once the parity
  work lands, D070.11): in **Extensions > Manage Extensions**, search for **Runic
  DevKit**, or open
  [runic-artifex.runic-devkit-vs](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit-vs).
  The Marketplace has no pre-release channel, so release candidates are not
  published there.
- **VSIX file:** download `runic-devkit-vs.vsix` from the
  [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases)
  (release candidates included) and open it to install.

The Marketplace listing comes from `publishManifest.json` (publisher
`runic-artifex`, internal name `runic-devkit-vs`, permanent once published),
`overview.md` and the listing fields of `source.extension.vsixmanifest`; the
images in `media/` are rendered by `eng/brand/render-icons.sh`. `package.py`
checks them (`--check-source`, and the built VSIX).

## Moving from Runic MF2 Translations

Up to 0.21 this extension was **Runic MF2 Translations**
(`Runic.Artifex.Translations.Rmf2`, `runic-translations-visualstudio.vsix`). Since
0.22 it is **Runic DevKit** (`RunicArtifex.DevKit.VisualStudio`,
`runic-devkit-vs.vsix`), the one Runic extension for the Runic SDK and Translations,
as in VS Code. Its Translations features, commands and environment variables are
unchanged. Visual Studio treats the new identity as another extension, so
installing `runic-devkit-vs.vsix` does not replace the former one, and while both
are installed both start a language server for the same files:

1. Uninstall Runic MF2 Translations: **Extensions > Manage Extensions >
   Installed**, select **Runic MF2 Translations**, choose **Uninstall**, then
   restart Visual Studio. Runic DevKit finds it through the extension manager when
   a solution is open and shows an InfoBar with these steps and a link to Manage
   Extensions.
2. Install Runic DevKit ([Install](#install)).

Keyboard shortcuts assigned to the former **Tools → Runic: …** commands belong to
the former command set; assign them again. Their command names
(`Tools.RunicPreviewMessage`, `Tools.RunicRestartLanguageServer`) are unchanged.

## Commands

Commands appear in the **Tools → Runic** menu. **View → Other Windows → Runic**
(also **Tools → Runic → Runic**) opens the Runic tool window, with sections for
the Dev Session, ViewModels, Doctor and Translations and a toolbar; in this build
the sections are placeholders and the Runic SDK commands stay hidden until they
are implemented (the 0.22 parity work, see [Layout](#layout)). Assign preferred shortcuts in Visual Studio’s
keyboard options; the extension does not override existing IDE bindings.

Within an MF2 editor:

- **Tools → Runic → Preview Message** (also in the editor context menu) opens the native message preview. The
  **Preview locale** selector switches the language and **Sample values** offers
  each `@example` set of the message; either change renders at once. Edit a value
  (the selector then shows **Custom values**) and choose **Render preview**. The locale selector also lists the
  pseudo-locales that `runic.json` configures (`pseudoLocales`), generated from the base locale as a
  `--pseudo-locales` build does. **Preview regional locale** formats numbers and dates for the locale a user requests
  (`de-CH` for `de` text) and **Preview time zone** sets the zone `timeZone=local` formats in (`utc` or an IANA
  zone; empty is this computer's); press Enter to render. A line below the text names the requested locale, the
  locale it formatted in and the time zone. The server executes its verified runtime
  plan. Links and actions are inert, icons use slot labels, and custom components
  use labeled spans. The extension never loads application renderers.
- **Tools → Runic → Restart Language Server** stops and restarts the server.
- Standard IDE navigation, Find All References, Format Document and Rename use
  Visual Studio's LSP support. F2 refuses a resource rename when application or
  other non-resource files make whole-workspace correctness unsupported. MF2 local rename
  stays within its lexical scope. Explicit resource-only input/slot/structural
  transactions are available in the CLI, Editor and VS Code; this client does
  not implement a second workspace-edit engine for those commands.

`runic.json` retains Visual Studio's JSON language service, which validates it
against the `$schema` the templates write. The extension also opens every
`runic.json` editor buffer on the Runic server (full-text `didOpen`/`didChange`/`didClose`
over the client's connection), so diagnostics, preview and configuration-changing
refactors such as F2 resource rename use unsaved configuration edits, as in VS
Code. Resource buffers are synchronized through LSP.
Diagnostics cover every file of a project with an open file, including closed
translations and `runic.json`. Hover over a message in a translation shows the
base-locale text with its comments and `@param` descriptions. C#, TypeScript and Svelte retain their existing language services.
Runic does not silently rename their application call sites.
The client watches direct `.mf2`, grouped `.rmf2`, and manifests under the containing solution;
when `sourceRoots` points outside the config directory, open the common
containing workspace so those declared files remain inside the host watch
boundary. Symlink/reparse-point mounts are rejected by the shared server rather
than traversed.

## Visual Studio and VS Code

| Feature | Visual Studio | VS Code |
| --- | --- | --- |
| Highlighting, completion, symbols, folding, formatting | Yes | Yes |
| Diagnostics for every project file | Yes | Yes |
| Diagnostic documentation links | Where the IDE shows LSP code descriptions | Yes |
| Hover with base text and description | Yes | Yes |
| Definition, references, quick fixes | Yes | Yes |
| F2 rename of resources and MF2 locals | Yes | Yes |
| Unsaved `runic.json` in refactors | Yes | Yes |
| `runic.json` schema validation | `$schema` (published schema) | Bundled schema, or `$schema` |
| Preview Message | Tools → Runic menu, editor context menu | Command |
| Change Preview Samples | Sample selector in the preview window | Command |
| Change Preview Locale | Locale selector in the preview window | Command |
| Pseudo-locales (`pseudoLocales`) in the preview | Locale selector in the preview window | Change Preview Locale |
| Regional locale and time zone of the preview | Fields in the preview window | Commands |
| Rename Input, Rename Slot, Rename Resource in Resource Sources | No (2.0) | Commands |
| Extract Group to Directory, Inline Resource into Ancestor | No (2.0) | Commands |
| Restart Language Server | Tools → Runic menu | Command |
| Server output | Server stderr in the diagnostic trace | Show Output command and status bar |
| Server state, catalog and locale | No | Status bar |
| Start failure | IDE notification on initialization failure | One notification per folder, with install help |

Full command parity waits for the production language server (2.0).

## Layout

`Commands.vsct` holds the whole command table: every command ID, the **Tools →
Runic** menu, the editor and Error List context-menu groups, **View → Other
Windows → Runic** and the tool window's toolbar groups. `RunicPackage.cs`
registers every command with its handler (`Commands/CommandRegistrar.cs`) and
the tool window. Each command has one handler file, `<Command>Command.cs`, in
the folder of the work that implements it, and each tool-window section one
`ToolWindow/Sections/<Section>Section.cs`; until then a handler derives from
`NotAvailableYetHandler` (hidden, `DefaultInvisible`) and a section shows a
placeholder. Implementing a command means changing its handler file, not the
`.vsct` or the package class.

| Folder | Commands |
| --- | --- |
| `Translations/` | Preview Message, Restart Language Server (implemented); Export XLIFF, Import XLIFF (dry run), Import Catalog, Override Library Message, Show/Refresh Translation Status |
| `Output/`, `Settings/` | Show Translations Output, Show Runic SDK Output, Options |
| `DevSession/` | Start `dotnet runic dev`, Start `--no-window`, Stop, Open the Development Server, Open the dev Session File |
| `ToolWindow/` | Runic tool window (implemented), Run `dotnet runic doctor`, Refresh ViewModels |
| `Debugging/` | Debug the Window, Attach to the Page (JavaScript) |

`Core/` (`RunicArtifex.DevKit.VisualStudio.Core`, netstandard2.0, no
dependencies) holds the logic that needs no Visual Studio: the command and
section catalogs now, and the dev session, doctor and source-map readers
later. The VSIX ships it next to the extension's assembly. `Core.Tests/` (TUnit)
checks `Commands.vsct`, the catalog and the handler files against each other and
runs on Linux and Windows: `dotnet run --project visualstudio/Core.Tests`.

## Build and package

Cross-compilation uses the runic-editors development shell (see the repository README):

```sh
dotnet build visualstudio
python visualstudio/package.py --check-source
```

Create the installable VSIX with Visual Studio's full-framework MSBuild on
Windows (for example, from its Developer PowerShell):

```powershell
MSBuild visualstudio/RunicArtifex.DevKit.VisualStudio.csproj /restore /m:1
python visualstudio/package.py
```

The pinned `Microsoft.VSSDK.BuildTools` package supplies the packaging targets;
no global extension-development workload is required. Packaging includes the
installer's generated `manifest.json` and `catalog.json`, the registered command
package, TextMate registration and grammar. The verifier rejects missing assets
and redistributed host-owned DLLs. Output:
`visualstudio/artifacts/runic-devkit-vs.vsix`.
A native-built archive copied to Linux can be verified with `package.py --input
/path/to/native.vsix`. Cross-compilation alone does not produce an installable
archive.

The manifest's `Version` is the `|%CurrentProject%;GetRunicVsixVersion|` token. The
VSSDK build replaces it with the release version of `eng/version.json`, mapped to
`N.N.N.N` like the `AnalyzerReleases` headers: the plain `0.8.0` becomes `0.8.0.1000`,
`1.0.0-preview.N` becomes `1.0.0.N`, `1.0.0-rc.N` becomes `1.0.0.(500 + N)` and the
final `1.0.0` becomes `1.0.0.1000`. `package.py` fails unless the VSIX has
that version (`--release-version` checks against another release). Pass
`/p:RunicIdeReleaseVersion=<version>` to MSBuild to build another version. See
[IDE extension versions](../eng/release/README.md#ide-extension-versions). Each
GitHub release attaches the attested VSIX that CI built.

The VS host dependency set is pinned separately from the SDK's modern runtime
packages: in-process dependencies target the Visual Studio 17.14 API surface.
The declared host range is `[18.0,19.0)`, Visual Studio 2026 only. Native
evidence covers Visual Studio Community 2026 18.8.2 (see
[RMF2 validation](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/rmf2-validation.md#visual-studio-2026-journey-after-the-server-fix-2026-10-07)).
`packages.lock.json` records the graph. The VSIX contains only the extension's
own managed assembly; VS-owned DLLs resolve from the host.

## Windows integration check

Use a dedicated experimental instance; the maintained test expects
`/RootSuffix RunicRmf2` and a signed-in interactive Windows desktop. The script
copies the maintained payment fixture and explicitly activates
`rmf2-execution-v2`. Every step passed on Visual Studio 2026 18.8.2 on
2026-10-07. See
[RMF2 validation](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/rmf2-validation.md#visual-studio-2026-journey-after-the-server-fix-2026-10-07).
To run the check:

1. Build the VSIX, or use the CI `visualstudio-vsix` artifact. Install with
   `VSIXInstaller.exe /rootSuffix:RunicRmf2 /quiet <native-built.vsix>`.
   After a reinstall into an existing profile, run
   `devenv.exe /RootSuffix RunicRmf2 /updateconfiguration` once. Otherwise
   the MEF catalog can omit the new client.
2. Run `dotnet tool restore` in this repository (it installs the
   `dotnet-runic-translations` release of `.config/dotnet-tools.json` from
   NuGet), or set `RUNIC_TRANSLATIONS_SERVER` to the absolute DLL of a local
   server build, before starting
   `devenv.exe <document.rmf2> /RootSuffix RunicRmf2 /Log`. Opening an MF2
   document activates LSP. Put the document before `/Log`, because `/Log`
   treats its next argument as the log file path.
3. Run the native interaction check. The runner script lives in a
   `runic-sdk` checkout. Pass this repository's test script by absolute path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:/src/runic-sdk/tests/native/Runic.Desktop.WebViewSmoke/run-windows-ui-automation.ps1 `
  -Executable "C:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/devenv.exe" `
  -AutomationScript C:/src/runic-editors/visualstudio/test/native-host.ps1 `
  -ReceiptPath C:/src/runic-editors/artifacts/rmf2-vs-host.json
```

The runner schedules the test in the signed-in desktop session and removes its
scheduled task afterward. The test creates and removes its own temporary payment
fixture, exercises registered commands and native preview controls, and checks
invalid-number recovery, locale switching and unsaved text after server restart.
The shared LSP integration suite separately covers diagnostics, navigation,
versioned rename, cancellation and refusal of partial application renames.

When repeatedly reinstalling the same development version, Visual Studio can
retain stale extension/component metadata. Close the experimental instance and
refresh only that disposable profile's extension metadata and component cache,
or use a fresh experimental profile. Do not clear a user's normal IDE profile.
No marketplace publication is performed.

Implementation follows Microsoft's [LSP extension guide](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=vs-2022)
and [VSIX schema](https://learn.microsoft.com/en-us/visualstudio/extensibility/vsix-extension-schema-2-0-reference?view=vs-2022).
