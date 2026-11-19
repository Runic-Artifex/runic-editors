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
  references, formatting, F2 rename of resources, MF2 locals, inputs and slots,
  Extract Group and Inline Resource in the light bulb, the message preview,
  XLIFF, catalog import, library overrides and translation status.

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
process has exited as stopped. For such a killed `dev` it touches
`obj/runic/dev-stopped` once, as `dev` does when it stops, so the next F5 builds
and puts the production frontend back in `www/`. **Open the Development Server** opens the page in
the browser; **Open the dev Session File** opens the file. `dev`'s failures
(`RAPPDEV` codes) and the build's errors and warnings (C#, `RUNICBRIDGE`)
appear in the Error List; F1 on one opens its entry in the Runic diagnostics
catalog. A build error leaves the Error List once `dev` reports `[dev] Ready`
again after the fix, and all entries go at the next build cycle (`[dev]
Evaluating`, with the Window also `Window exited` and `Reloading`).

Frontend errors are not in the Error List. Vite reports a `.tsx` parse or
transform error as several lines of formatted text, without a line in MSBuild's
form, and `dev` passes them through as they are; they show in the **Runic SDK**
pane and in the page's error overlay. `dev` runs no TypeScript type check, so
type errors appear only in the editor.

With `dotnet-runic` 0.22 and later:

- when Visual Studio starts `dev` with its Window, it passes `--debug-endpoint`,
  so a CS-WebUI Window opens its DevTools Protocol port (D070.17). A `dev` you
  start in a terminal opens none;
- **Stop dotnet runic dev** writes `stop` to `dev`'s standard input
  (`RUNIC_DEV_STOP_ON_INPUT=1`), so `dev` stops as on Ctrl+C: the session file
  says `"stopped"` and the development pages leave `www/`. Only when `dev` has
  not exited within 10 seconds, and with an older tool, Visual Studio ends the
  process tree (WIN-06).

`dev` runs in a Windows Job object. After **Stop dotnet runic dev**, whatever
`dev` left running (its Vite server, `runic-translations serve`) is ended when
`dev` exits, and a forced stop ends all of it, also processes whose parent has
already exited. Visual Studio counts `dev` as stopped when its process exits, not
when its output closes. **Start dotnet runic dev** during a stop waits up to 30
seconds for it to finish, and otherwise says which process is still running.

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
status bar shows each step.

For a WebView2 page, DevKit turns off **Tools → Options → Debugging → General →
Break all processes when one process breaks** while the session runs, and on
again when debugging stops; the Runic SDK Output pane says so. With it on, a
breakpoint in the page also stops the .NET Window. The Window's UI thread holds
the window that WebView2's browser process draws into, so the browser process
then waits for the stopped Window, stops answering the JavaScript debugger, and
Visual Studio hangs on "a debugger operation is taking a long time". So a
TSX breakpoint pauses the page while the .NET code runs on, and a C# breakpoint
pauses the .NET code while the page's JavaScript runs on (its Bridge calls wait).
Attach to the Page does the same for a WebView2 page. If Visual Studio ends while
the option is off, DevKit turns it on again the next time it loads. If you have
turned the option off yourself, it stays off. Debug the Window waits for the page up to three
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
symbols, Go to Definition, Find All References, quick fixes, Format Document,
F2 rename and the refactorings of the light bulb (see
[Refactorings](#refactorings-f2-and-the-light-bulb)). MF2 local rename stays
within its lexical scope. Runic does not rename C#, TypeScript or Svelte call
sites.

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
reparse-point mounts rather than following them. With Translations 0.14, the
server also watches the files itself, because Visual Studio cannot register
file watchers for it; changes made outside Visual Studio reach it within about
200 ms.

### Refactorings: F2 and the light bulb

DevKit asks the language server for its protocol 2
(`initializationOptions.runicProtocol: 2`), which Translations 0.14 speaks;
Translations 0.13 ignores the request. The refactorings come from the server
through standard LSP, with no refactoring code in this extension (D070.1):

- **F2** renames a resource, an MF2 local, a caller input (on `$name`, in every
  locale's resource) or a slot reference (on the value of `ref=`, also in the
  `runic.json` slot contracts). Visual Studio's LSP client saves a rename at
  once: it writes every renamed file to disk, open documents included (their
  buffers show the new names and stay unmodified). When the solution also holds application
  sources (C#, TypeScript, Svelte), a resource rename asks first in an InfoBar,
  **Rename in resource sources** or **Cancel** (the server's
  `window/showMessageRequest`): Runic renames the resource in resource sources
  only and does not update call sites. When the server refuses a rename (an
  invalid name, a catalog that does not compile), Visual Studio closes the
  rename box, and DevKit shows the server's reason in an InfoBar (with **Show
  output**), the status bar and the **Runic Translations** Output pane
  (runic-editors#32).
- **The light bulb** (Ctrl+.) on a group's name offers **Extract group 'x' to
  x/en.rmf2**, which creates that file. Visual Studio does not create a new
  file's folder (it would drop the new file and keep only the removal from the
  source), so **DevKit creates a missing `x/` folder first**, only inside the
  solution or the Translations project, and notes it in the **Runic
  Translations** Output pane. If it cannot, Visual Studio applies none of the
  edit and DevKit says why in an InfoBar and the pane. In a feature-local
  resource it offers **Inline into ../en.rmf2 (leaves en.rmf2 empty)**, one
  action per ancestor resource of the same locale.

Visual Studio cannot delete files through LSP (its client has no `delete`
resource operation), so Inline Resource moves the resources into the ancestor
and empties the child file instead of deleting it, as its title says; a
server-side delete could lose moved resources that are not saved yet. The
edited files stay open and unsaved. **Once the emptied file and the files that
received its resources are saved, DevKit deletes the emptied file** through
its project item, so Solution Explorer and source control see a normal delete,
and says so in the **Runic Translations** Output pane. It deletes only the
file that action emptied, and only while it is saved and empty on disk: when
you undo the Inline or write into the file before saving it, the file stays.
A file in no project of the solution is left empty, with a note in the pane;
delete it yourself.

With Translations 0.13, F2 renames resources and MF2 locals only, a resource
rename in a solution with application sources is refused, and there are no
Extract Group and Inline Resource actions; use the CLI or VS Code for them.

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
| F2 rename of resources, MF2 locals, inputs and slots | Yes (Translations 0.14) | Yes (Translations 0.14) |
| Resource rename next to application sources | Asks in an InfoBar (Translations 0.14) | Asks in a notification (Translations 0.14) |
| Unsaved `runic.json` in refactors | Yes | Yes |
| `runic.json` schema validation | `$schema` (published schema) | Bundled schema, or `$schema` |
| Preview Message | Tools → Runic menu, editor context menu | Command |
| Change Preview Samples | Sample selector in the preview window | Command |
| Change Preview Locale | Locale selector in the preview window | Command |
| Pseudo-locales (`pseudoLocales`) in the preview | Locale selector in the preview window | Change Preview Locale |
| Regional locale and time zone of the preview | Fields in the preview window | Commands |
| Rename Input, Rename Slot, Rename Resource in Resource Sources | F2 with Translations 0.14 | F2 with Translations 0.14, commands |
| Extract Group to Directory, Inline Resource into Ancestor | Light bulb with Translations 0.14; DevKit creates the folder Extract Group needs and deletes the file Inline empties once it is saved | Light bulb with Translations 0.14, commands |
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
| `dev` diagnostics as problems | Error List, read as the `$runic-dev` matcher reads them, and a fixed build error goes at `[dev] Ready`; F1 opens the Runic diagnostics catalog. Vite's frontend errors stay in the Output pane | Problems through the `$runic-dev` problem matchers |
| Dev session follow | Session file watcher and 2 s poll; liveness by process ID and start time; a killed `dev` gets `obj/runic/dev-stopped` | Watcher and 2 s poll; process command line or image; the same `dev-stopped` touch |
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
  Visual Studio Community 2026 18.12.0; the range is not evidence that every
  18.x release has been run.
- **Inline Resource deletes its file only after the save.** Visual Studio has
  no LSP file delete, so the server empties the child file and DevKit deletes it
  once it and the edited ancestor are saved
  ([Refactorings](#refactorings-f2-and-the-light-bulb)). Until then the empty
  file stays in the project; a file in no project stays until you delete it.
- **No palette-style refactoring commands.** Rename Input, Rename Slot, Extract
  Group and Inline Resource are F2 and the light bulb only (Translations 0.14);
  VS Code also has commands for them.
- **Frontend errors stay in the Runic SDK pane.** A `.tsx` error while `dev`
  runs is not an Error List row ([Dev Session](#dev-session-dotnet-runic-dev)).
  For that, `dev` would have to print each Vite error also as one line in
  MSBuild's form (`Frontend/src/App.tsx(30,5): error <code>: <message>`, as it
  does for its own faults) and repeat a line it can match when the frontend
  compiles again.
- **No status bar item** for the language server's state, catalog and locale.
- **F5 debugs .NET only.** Page breakpoints need Debug the Window or Attach to
  the Page; there is no Runic launch type for both (D070.19).
- **Debug → Break All while the page runs** stops the .NET Window, and WebView2
  with it, so the page's part of the break fails: Visual Studio may say "The
  script document cannot be opened". Continue, or stop debugging, to go on.
  Breakpoints in the page are not affected.

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
| `Debugging/` | Debug the Window, Attach to the Page (JavaScript), and "Break all processes" off while they debug a WebView2 page |

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
[RMF2 validation](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/rmf2-validation.md#visual-studio-2026-journey-after-the-server-fix-2026-10-07))
and 18.12.0 ([Windows integration check](#windows-integration-check)).
`packages.lock.json` records the graph. The VSIX contains only the extension's
own managed assembly; VS-owned DLLs resolve from the host.

## Windows integration check

The native check runs the installed VSIX in Visual Studio 2026 on a signed-in
Windows desktop; hosted CI runners have none, so it runs by hand before each
release. It passed on Visual Studio Community 2026 18.12 (devenv 18.12.12405.243) on
2026-11-18, with `dotnet-runic-translations` 0.14.0 and its refactorings.
[`test/README.md`](test/README.md) describes it in full, with the manual
checklist for what needs human eyes.

1. Use the CI `visualstudio-vsix` artifact of the commit under test, or build
   the VSIX. Close every Visual Studio instance of `/RootSuffix RunicRmf2`: the
   check starts and quits its own. The first start of a new experimental
   instance shows Visual Studio's first-run dialogs; start
   `devenv.exe /RootSuffix RunicRmf2` once by hand and close them.
2. Run, as the signed-in user (over SSH or from a console, no elevation):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File visualstudio/test/run-native-checks.ps1 -Vsix <runic-devkit-vs.vsix>
```

`run-native-checks.ps1` uninstalls Runic MF2 Translations from the instance,
installs the VSIX with `VSIXInstaller.exe /quiet /rootSuffix:RunicRmf2`, runs
`devenv.exe /RootSuffix RunicRmf2 /updateconfiguration` (otherwise the MEF
catalog can omit the new client after a reinstall), and starts
`test/native-host.ps1` in the desktop session through a temporary Interactive
scheduled task, which it removes afterwards. It prints `PASS Runic DevKit
<version> native check` or the first failed check; the receipt, its log and
Visual Studio's activity log stay in `visualstudio/artifacts/`. `-SkipInstall`
checks the VSIX already installed, `-RootSuffix` names another instance.

`native-host.ps1` copies `fixtures/rmf2` and `fixtures/sdk-app` into a
temporary solution, restores the language server and `dotnet-runic` from a tool
manifest, and checks: the installed VSIX version; every `Commands.vsct` command;
the package and the Runic tool window; the dev session follower (ready, Open the
dev Session File, stopped once `dev` ends); the real `dotnet runic doctor`; the
language server; Preview Message with invalid-number recovery, locale switching
and samples; and an unsaved buffer after Restart Language Server. It removes the
temporary folder afterwards. runic-sdk's
`tests/native/Runic.Desktop.WebViewSmoke/run-windows-ui-automation.ps1` can also
start it (`-AutomationScript`) once the VSIX is installed. The shared LSP
integration suite separately covers diagnostics, navigation, versioned rename,
cancellation and refusal of partial application renames.

When repeatedly reinstalling the same development version, Visual Studio can
retain stale extension/component metadata. Close the experimental instance and
refresh only that disposable profile's extension metadata and component cache,
or use a fresh experimental profile. Do not clear a user's normal IDE profile.
No marketplace publication is performed.

Implementation follows Microsoft's [LSP extension guide](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension?view=vs-2022)
and [VSIX schema](https://learn.microsoft.com/en-us/visualstudio/extensibility/vsix-extension-schema-2-0-reference?view=vs-2022).
