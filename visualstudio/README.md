# Runic DevKit for Visual Studio

**Preview** for Visual Studio 2026 (18.x) on Windows x64. Its commands, settings
and the `lsp` protocol it uses are outside the 1.0 compatibility rules and may
change in a minor release. Visual Studio 2022 (17.x) and ARM64 are not
supported.

One extension for the Runic SDK and Runic Translations, as
[Runic DevKit for VS Code](../vscode/README.md):

- **Runic SDK** (in a solution with a Runic SDK project): start and stop
  `dotnet runic dev` and follow its session, debug the Window's C# code and its
  page in one session, `dotnet runic doctor` results in the Error List, and a
  ViewModels tree that opens the C# members.
- **Translations** (in every solution with Runic Translations): direct `.mf2`
  and grouped `.rmf2` resources through the project's Runic language server:
  highlighting, diagnostics, completion, hover, symbols, definition and
  references, formatting, F2 rename of resources and MF2 locals, the message
  preview, XLIFF, catalog import, library overrides and translation status.

## Install

- **Visual Studio Marketplace** (final versions, from 0.22.0 once it is
  published, D070.11): in **Extensions > Manage Extensions**, search for
  **Runic DevKit**, or open
  [runic-artifex.runic-devkit-vs](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit-vs).
  The Marketplace has no pre-release channel, so release candidates are not
  published there.
- **VSIX file:** download `runic-devkit-vs.vsix` from the
  [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases)
  (release candidates included) and open it to install. Until the Marketplace
  listing exists, this is the way to install it.

Restart Visual Studio after installing. The extension loads only in a solution
with a Runic project: one with the `RunicApplication` project capability (Runic
Views projects since runic-sdk 0.22), one that uses the Runic Translations
build, or a Runic SDK project, a `runic.json` or a tool manifest with
`dotnet-runic-translations` up to four levels below the solution directory. Opening an `.mf2` or `.rmf2` file loads it too.

Debugging the page needs Visual Studio's **JavaScript diagnostics** component
(`Microsoft.VisualStudio.Component.JavaScript.Diagnostics`). Projects from
`dotnet new runic-app` have a `.vsconfig` that names it, and Visual Studio
offers to install it when it opens the project.

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

## Where to find it

- **Tools → Runic** has every command.
- **View → Other Windows → Runic** (also **Tools → Runic → Runic**) opens the
  Runic tool window. It has a toolbar and four sections: **Dev Session**,
  **ViewModels** and **Doctor** in a solution with a Runic SDK project, and
  **Translations**. A solution with only Runic Translations sees the
  Translations section alone, and the Runic SDK commands stay hidden.
- **Tools → Options → Runic DevKit** has the settings ([Settings](#settings)).
- The **Runic SDK** and **Runic Translations** Output panes show the tools'
  output.

Assign your own shortcuts in Visual Studio's keyboard options; the extension
does not override existing bindings.

## Runic SDK features

### Dev Session: `dotnet runic dev`

**Start dotnet runic dev** runs the development server and the Window, which
`dotnet watch` restarts after each C# edit. **Start dotnet runic dev
--no-window** runs only the frontend, for a Window that the debugger starts.
**Stop dotnet runic dev** stops a `dev` that Visual Studio started; stop one
that runs in a terminal there with Ctrl+C. The output goes to the **Runic SDK**
Output pane. With several Runic SDK projects, the commands act on the project of
the active document, else on the startup project.

The **Dev Session** section follows `obj/runic/dev-session.json`, wherever
`dev` was started: the state, the development server, the Window's process or
"started by the IDE", the pages, the debug endpoints and the generated clients.
It reads the file on each change and every 2 seconds, and shows a `dev` whose
process has exited as stopped. **Open the Development Server** opens the page in
the browser; **Open the dev Session File** opens the file. `dev`'s failures
(`RAPPDEV` codes) and the build's errors appear in the Error List; F1 on one
opens its entry in the Runic diagnostics catalog.

With `dotnet-runic` 0.22 and later:

- when Visual Studio starts `dev` with its Window, it passes `--debug-endpoint`,
  so a CS-WebUI Window opens its DevTools Protocol port (D070.17). A `dev` you
  start in a terminal opens none;
- **Stop dotnet runic dev** writes `stop` to `dev`'s standard input
  (`RUNIC_DEV_STOP_ON_INPUT=1`), so `dev` stops as on Ctrl+C: the session file
  says `"stopped"` and the development pages leave `www/`. Only when `dev` has
  not exited within 10 seconds, and with an older tool, Visual Studio ends the
  process tree (WIN-06).

### F5 asks to start `dotnet runic dev --no-window`

When F5 or Ctrl+F5 starts a Runic SDK startup project and no `dotnet runic dev
--no-window` runs for it, Runic DevKit asks first (D070.6):

- **Yes** starts `dotnet runic dev --no-window`, waits until it is ready, and
  then starts the Window. The Window loads the page from the development server,
  and frontend edits reach it while you debug.
- **No** starts the Window without it. The Window then shows the production
  frontend.
- **Cancel** starts nothing.

If a `dev` that Visual Studio started runs its own Window, Yes restarts it with
`--no-window`. If a `dev` started in a terminal runs its own Window, stop it
there first, or continue and get two Windows. The question comes every time;
there is no setting to remember the answer.

F5 starts the launch profile under the .NET debugger only. For breakpoints in
the page, use [Debug the Window](#debugging-the-window-and-its-page).

### Doctor and ViewModels

**Run dotnet runic doctor** (Tools → Runic, the tool window's toolbar or the
Error List's context menu) runs `dotnet runic doctor` for the project. The
**Doctor** section lists every check, failures first, with its remediation.
Failing checks and warnings appear in the Error List on the project file; F1
opens the check's documentation.

The **ViewModels** section shows the ViewModels and their Bridge members from
the development source map. Double-click a member, press Enter or use its
context menu to open the C# member. **Refresh ViewModels** reads the map again.

## Debugging the Window and its page

**Tools → Runic → Debug the Window** (also on the tool window's toolbar) debugs
the .NET code and the page in one session:

1. It starts `dotnet runic dev --no-window` if it does not run yet, and waits
   until it is ready. If a `dev` that Visual Studio started runs its own Window,
   it asks before it restarts it with `--no-window`.
2. It builds the project as F5 does.
3. It starts the program `dotnet run` would start under the .NET debugger, with
   the session file's `window.environment` and a DevTools Protocol port:
   - **WebView2** (Runic Desktop on Windows): a free loopback port, added to
     `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` (a value that already names a port
     keeps it).
   - **CS-WebUI**: the port `dev --no-window` names in the session
     (`RUNIC_WEBUI_REMOTE_DEBUGGING_PORT`, 9225, since runic-sdk 0.22).
4. Once `http://127.0.0.1:<port>/json/version` answers and the page is listed,
   it attaches Visual Studio's JavaScript debugger over the "JavaScript and
   TypeScript (Chrome DevTools/V8 Inspector)" transport.

Breakpoints in C# and in `Frontend/src` then stop in the same session. The
status bar shows each step. Debug the Window waits for the page up to three
minutes of the Window running (time stopped at a .NET breakpoint does not
count). If the page does not appear, it says so in the **Runic SDK** Output pane
and the .NET debugger stays attached.

**Tools → Runic → Attach to the Page (JavaScript)** attaches the JavaScript
debugger alone to a page that already runs with an endpoint: the session file's
`debugEndpoints` (WebView2's, and since runic-sdk 0.22 a CS-WebUI Window's) and
the Windows that Debug the Window started. With several, it asks which one.

Page debugging needs:

- Visual Studio's **JavaScript diagnostics** component
  (`Microsoft.VisualStudio.Component.JavaScript.Diagnostics`), which the
  template's `.vsconfig` names;
- a Visual Studio that does not run as administrator: WebView2 ignores the port
  in an elevated process, and the Runic SDK Output pane says so;
- runic-sdk 0.22's `vite-plugin-runic` for breakpoints in `.tsx`, `.vue` and
  `.svelte` files: it writes development source maps with absolute `sources`.
  With an older plugin, the page's JavaScript breakpoints bind but those in
  `Frontend/src` stay unbound.

The template's `(WebView2 script debugging)` launch profile of runic-sdk 0.20
and 0.21 does not work in Visual Studio: its JavaScript adapter starts the
Window without the profile's environment variables. runic-sdk 0.22 removes it
(D070.18). Delete it from an existing project's `launchSettings.json` and use
Debug the Window instead. See
[Debugging a Runic application](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#visual-studio)
in runic-sdk.

### Troubleshooting

- **The JavaScript debugger does not attach** ("Value cannot be null. Parameter
  name: source", or the attach never finishes). Visual Studio's own JavaScript
  debug adapter can fail with a damaged `.vs` folder, for example after Visual
  Studio was ended by force. Close Visual Studio, delete the solution's `.vs`
  folder and open the solution again. DevKit stops waiting for an attach after
  60 seconds, says so in the **Runic SDK** Output pane and enables **Debug the
  Window** again; closing and reopening the solution also ends a run that is
  still waiting (runic-editors#23).
- **Breakpoints in `.tsx` files stay hollow.** Update
  `@runic-artifex/vite-plugin-runic` to 0.22 or later, or check that the Window
  loads the page from the development server and not the production build
  (start `dev --no-window` first).

## Translations

Restore the solution's local `runic-translations` .NET tool before opening an
MF2 document:

```sh
dotnet tool restore
```

The extension finds the nearest `.config/dotnet-tools.json` from the active
document (or solution), respecting `isRoot`, and starts
`dotnet tool run runic-translations -- lsp`. The tool runs on its own .NET
runtime, outside Visual Studio's process. The extension never downloads or
restores a tool; use trusted projects. If the server cannot start, an InfoBar
says why once per solution, with **Show output** and install help. One server
serves the Visual Studio session; **Tools → Runic → Restart Language Server**
restarts it, for example after switching solutions or changing the tool.

### In the editor

Visual Studio's LSP support gives highlighting, diagnostics, completion, hover,
symbols, Go to Definition, Find All References, quick fixes, Format Document and
F2 rename of resources and MF2 locals. F2 refuses a resource rename when
application or other non-resource files make a whole-workspace rename unsafe.
MF2 local rename stays within its lexical scope. Runic does not rename C#,
TypeScript or Svelte call sites.

Diagnostics cover every file of a project with an open file, including closed
translations and `runic.json`. Hover over a message in a translation shows the
base-locale text with its comments and `@param` descriptions. `runic.json` keeps
Visual Studio's JSON language service, which validates it against the
`$schema` the templates write. The extension also sends every open `runic.json`
buffer to the Runic server, so diagnostics, the preview and refactors such as
F2 resource rename use unsaved configuration edits.

The client watches direct `.mf2`, grouped `.rmf2` and manifests under the
solution. When `sourceRoots` points outside the config directory, open a
solution that contains those files. The server rejects symlink and
reparse-point mounts rather than following them.

### Translations tools

All in **Tools → Runic**; the tools' output goes to the **Runic Translations**
Output pane.

- **Preview Message** (also in the editor's context menu) opens the message
  preview. **Preview locale** switches the language, including the
  pseudo-locales that `runic.json` configures (`pseudoLocales`). **Sample
  values** offers each `@example` set; edit a value (the selector then shows
  **Custom values**) and choose **Render preview**. **Preview regional locale**
  formats numbers and dates for the locale a user requests (`de-CH` for `de`
  text), and **Preview time zone** sets the zone `timeZone=local` formats in
  (`utc` or an IANA zone; empty is this computer's); press Enter to render. A
  line below the text names the requested locale, the locale it formatted in
  and the time zone. Links and actions are inert, icons show slot labels, and
  custom components show as labeled spans; the extension never loads
  application renderers.
- **Export XLIFF…** asks for a folder and writes one XLIFF 2.1 document per
  translated locale into it.
- **Import XLIFF (Dry Run)…** asks for an XLIFF 2.1 file and reports each
  unit's outcome, without writing.
- **Import .resx or i18next Catalog…** runs a dry run first, then imports after
  you confirm.
- **Override Library Message…** (also in the editor's context menu) asks for a
  library catalog, a message and a locale, writes
  `overrides/<catalog>/<locale>.rmf2` seeded with the library's text, and opens
  it at the message. Go to Definition from an override
  source opens a read-only copy of the library source.
- **Show Translation Status** opens the `runic-translations status` report per
  locale as a read-only document; the **Translations** section of the Runic tool
  window shows the same numbers.
  **Refresh Translation Status** reads it again.
- **Show Translations Output** opens the Output pane.

## Settings

**Tools → Options → Runic DevKit → General**:

| Setting | What it does | VS Code |
| --- | --- | --- |
| .NET executable | The `dotnet` that runs the project-local tools (`dotnet runic`, `runic-translations`). Empty uses `RUNIC_DOTNET`, else `dotnet` on the PATH. Applies when the language server next starts. | `runic.dotnetPath` |
| Language server assembly | A built `dotnet-runic-translations.dll`, absolute or relative to the solution directory, for Translations SDK development. Empty uses `RUNIC_TRANSLATIONS_SERVER`, else `dotnet tool run runic-translations`. Applies when the language server next starts. | `runic.translations.serverAssembly` |
| Trace | The language server's messages in the Runic Translations Output pane: Off, Messages or Verbose (with each message's JSON). Applies at once. | `runic.translations.trace.server` |

The environment variables `RUNIC_DOTNET` and `RUNIC_TRANSLATIONS_SERVER` still
work when the settings are empty; set them before starting Visual Studio.

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
| Rename Input, Rename Slot, Rename Resource in Resource Sources | Not yet ([Known limits](#known-limits)) | F2 with Translations 0.14, commands |
| Extract Group to Directory, Inline Resource into Ancestor | Not yet ([Known limits](#known-limits)) | Light bulb with Translations 0.14, commands |
| Restart Language Server | Tools → Runic menu | Command |
| Export XLIFF, Import XLIFF (dry run) | Tools → Runic menu; folder or file picker, tool output in the "Runic Translations" Output pane | Commands |
| Import .resx or i18next Catalog | Tools → Runic menu; dry run, then import after confirmation | Command |
| Override Library Message… | Tools → Runic menu, editor context menu | Command |
| Library sources from Go to Definition in an override source | Read-only copy of the source | Read-only `runic-library:` document |
| Override sources of library catalogs: diagnostics, completion, hover | Yes | Yes |
| Review state in hover | Yes | Yes |
| Mark reviewed quick fix (`RTR0090`) | Where the IDE runs code-action commands | Yes |
| Translation status per locale | Runic tool window, Translations section; Show Translation Status (read-only report), Refresh Translation Status | Translations view, Show Translation Status panel |
| Server output | "Runic Translations" Output pane (Tools → Runic → Show Translations Output); trace in Tools → Options → Runic DevKit | Show Output command and status bar |
| Server state, catalog and locale | No | Status bar |
| Start failure | One InfoBar per solution, with the reason, Show output and install help | One notification per folder, with install help |
| Settings | Tools → Options → Runic DevKit | `runic.*` settings |
| ViewModels tree of the development source map | Runic tool window, ViewModels section; Refresh ViewModels | ViewModels view |
| Open C# Member | Double-click, Enter or context menu in the ViewModels section | ViewModels view |
| Run dotnet runic doctor, doctor results | Tools → Runic, tool window toolbar, Error List context menu; Doctor section | Command, Doctor view |
| Doctor findings as problems with help links | Error List on the project file; F1 opens the doctor documentation | Problems with a code link |
| Start, start `--no-window` and stop `dotnet runic dev` | Tools → Runic, tool window toolbar; output in the "Runic SDK" Output pane. Stop works for the `dev` Visual Studio started (on its input with `dotnet-runic` 0.22, else by ending it); one started in a terminal stops there with Ctrl+C | Commands and tasks; outside `dev` stopped with SIGINT (not on Windows) |
| `dev` diagnostics as problems | Error List, read as the `$runic-dev` matcher reads them; F1 opens the Runic diagnostics catalog | Problems through the `$runic-dev` problem matchers |
| Dev session follow | Session file watcher and 2 s poll; liveness by process ID and start time | Watcher and 2 s poll; process command line or image |
| Open the Development Server, Open the dev Session File | Tools → Runic, tool window toolbar | Commands |
| Dev Session view: state, server, Window owner or process, pages, debug endpoints, generated clients | Runic tool window, Dev Session section; toolbar buttons enabled per state; state changes in the status bar text | Dev Session view; "Runic dev" status bar item |
| F5 with `dev --no-window` | Pre-F5 question: start `dev --no-window` (then launch once it is ready) or continue without it | `runic` debug type starts it |
| Debug the Window: .NET and page breakpoints in one session | Tools → Runic, tool window toolbar: starts `dev --no-window`, builds, launches the Window under the .NET debugger with the session file's Window environment and a DevTools Protocol port, then attaches the JavaScript debugger over CDP (no 30-second limit) | `runic` debug type (`coreclr`) and the template's compound with the `msedge` attach |
| Attach to the Page (JavaScript) | Tools → Runic, tool window toolbar: the session file's WebView2 and CS-WebUI (0.22) `debugEndpoints` and the Windows Debug the Window started; a picker when there are several | Attach configuration |
| Install | Visual Studio Marketplace (final versions), VSIX | VS Code Marketplace (release candidates as pre-releases), VSIX |

## Known limits

- **Windows x64 and Visual Studio 2026 only.** The manifest declares
  `[18.0,19.0)` and the `amd64` architecture. There is no ARM64 build, because
  there is no ARM64 test machine. The native integration check last passed on
  Visual Studio Community 2026 18.8.2; the range is not evidence that every
  18.x release has been run.
- **No Rename Input, Rename Slot, Extract Group or Inline Resource yet.** These
  refactorings come from the language server through standard LSP (F2 and the
  light bulb), with no refactoring code in this extension (D070.1). They arrive
  with the language server of Translations 0.14, which is not released yet. With
  Translations 0.13, F2 renames resources and MF2 locals only, and the CLI and
  VS Code run the other resource transactions.
- **No status bar item** for the language server's state, catalog and locale.
- **F5 debugs .NET only.** Page breakpoints need Debug the Window or Attach to
  the Page; there is no Runic launch type for both (D070.19).

## Layout

`Commands.vsct` holds the whole command table: every command ID, the **Tools →
Runic** menu, the editor and Error List context-menu groups, **View → Other
Windows → Runic** and the tool window's toolbar groups. `RunicPackage.cs`
registers every command with its handler (`Commands/CommandRegistrar.cs`) and
the tool window. Each command has one handler file, `<Command>Command.cs`, in
the folder of the work that implements it, and each tool-window section one
`ToolWindow/Sections/<Section>Section.cs`. Every command and section is
implemented; a future command starts with a handler that derives from
`NotAvailableYetHandler` (hidden, `DefaultInvisible`), and a future section
shows a placeholder. Implementing a command means changing its handler file,
not the `.vsct` or the package class.

| Folder | Commands |
| --- | --- |
| `Translations/` | Preview Message, Restart Language Server, Export XLIFF, Import XLIFF (dry run), Import Catalog, Override Library Message, Show/Refresh Translation Status |
| `Output/`, `Settings/` | Show Translations Output, Show Runic SDK Output, Options |
| `DevSession/` | Start `dotnet runic dev`, Start `--no-window`, Stop, Open the Development Server, Open the dev Session File |
| `ToolWindow/` | Runic tool window, Run `dotnet runic doctor`, Refresh ViewModels |
| `Debugging/` | Debug the Window, Attach to the Page (JavaScript) |

`Core/` (`RunicArtifex.DevKit.VisualStudio.Core`, netstandard2.0, no
dependencies) holds the logic that needs no Visual Studio: the command and
section catalogs, the doctor and source-map readers and the trees of the
ViewModels and Doctor sections, the dev session reader, the Debug the Window
plan and the Translations tool calls. The VSIX ships it next to the extension's
assembly. `Core.Tests/` (TUnit)
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
