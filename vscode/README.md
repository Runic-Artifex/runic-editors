# Runic DevKit for VS Code

**Preview.** This extension is a preview product until the production language
server (2.0): its commands and settings, and the `lsp` protocol it uses, are
outside the 1.0 compatibility rules and may change in a minor release. Every
release carries the Marketplace "Preview" label.

## Install

- **VS Code Marketplace** (from 0.22.0): search for **Runic DevKit** in the
  Extensions view, open
  [runic-artifex.runic-devkit](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit),
  or run `code --install-extension runic-artifex.runic-devkit`. Release
  candidates (`1.0.0-rc.N`) are Marketplace pre-releases: choose **Switch to
  Pre-Release Version** to get them.
- **VSIX file:** download `runic-devkit.vsix` from the
  [GitHub releases](https://github.com/Runic-Artifex/runic-editors/releases) of
  runic-editors and run `code --install-extension runic-devkit.vsix` (or
  **Extensions: Install from VSIX…**). `SHA256SUMS` and a build-provenance
  attestation come with each release.
- **Open VSX:** Runic DevKit is not on Open VSX for now. In VSCodium, Cursor,
  Windsurf and other editors that install from Open VSX, install the VSIX file
  from the GitHub release.

One extension for the Runic SDK and Runic Translations:

- **Runic SDK** (in a workspace with a Runic SDK project): start and stop
  `dotnet runic dev` and follow it through its session file, F5 of the Window
  under the debugger with `dotnet runic dev --no-window`, `dotnet runic doctor`
  results, Runic diagnostics with code actions, and a ViewModels tree from the
  development source map that opens the C# members.
- **Translations** (in every workspace): direct `.mf2` and grouped `.rmf2`
  resources through the shared Runic language server: highlighting,
  diagnostics, symbols, folding, completion, hover, definition/reference
  navigation, formatting and versioned resource edits, the message preview,
  XLIFF, library overrides and translation status. It synchronizes open
  `runic.json` buffers so mounted/slot configuration changes participate in
  refactors without requiring an intermediate save.

## Moving from Runic or Runic MF2 Translations

This extension has had three identities:

| Releases | Name | Extension ID | VSIX |
| --- | --- | --- | --- |
| 0.22 and later | **Runic DevKit** | `runic-artifex.runic-devkit` | `runic-devkit.vsix` |
| 0.20–0.21 | Runic | `runic-artifex.runic` | `runic.vsix` |
| up to 0.11 | Runic MF2 Translations (Translations only) | `runic-artifex.runic-translations` | `runic-translations.vsix` |

0.22 only renames the extension, before its first Marketplace listing: its
commands, settings, views and Command Palette titles stay the same. Since 0.20
Translations-only workspaces use the same extension; the Runic SDK views stay
hidden there. VS Code treats each identity as another extension, so a `.vsix`
install does not replace the former one:

1. Uninstall the former extension (Extensions view, or
   `code --uninstall-extension runic-artifex.runic` and
   `code --uninstall-extension runic-artifex.runic-translations`). While one is
   installed, both register the same `runic.*` commands and start a language
   server for the same files. On activation Runic DevKit shows one notification
   that names the former extensions it finds and offers to uninstall them; reload
   the window afterwards.
2. Install Runic DevKit from the Marketplace or the release ([Install](#install))
   (`code --install-extension runic-devkit.vsix`).

From Runic MF2 Translations, settings and commands also moved to the `runic.`
namespace. The former settings still apply while the new ones are not set (and are marked deprecated in the Settings
editor), and the former command IDs still run, so keybindings keep working:

| Former (Translations extension) | Now |
| --- | --- |
| `runicTranslations.dotnetPath` | `runic.dotnetPath` (also runs `dotnet runic`) |
| `runicTranslations.serverAssembly` | `runic.translations.serverAssembly` |
| `runicTranslations.trace.server` | `runic.translations.trace.server` |
| `runicTranslations.<command>`, such as `runicTranslations.preview` | `runic.translations.<command>`, such as `runic.translations.preview` |

The Command Palette titles are unchanged (`Runic: Preview Message`).

## Runic SDK features

### Workspace detection

A workspace folder has a Runic SDK project when a `.csproj` up to four levels
below it (not in `bin`, `obj`, `node_modules`, `artifacts`, `dist`, `www` or a
dot directory) is a Runic Views Window project
(`RunicApplicationFrontendWindowProject`) or references a `Runic.Application.*`,
`Runic.Desktop*` or `Runic.Platform.*` package or project. Only then does the
**Runic** view container show **Dev Session**, **ViewModels** and **Doctor**, and
the status bar **Runic dev** item. A project that references only Runic
Translations packages is not a Runic SDK project. The **Translations** view shows
in any workspace with a `runic.json`. Adding or removing a project file updates
the views.

### Dev Session: `dotnet runic dev`

**Start dotnet runic dev** runs the development server and the Window, restarted
after each C# edit; **Start dotnet runic dev --no-window (frontend only)** runs
only the frontend, for a Window that the debugger starts. **Stop dotnet runic
dev** ends it. Both run as VS Code tasks: when the workspace folder has the
template's `.vscode/tasks.json` (`dotnet new runic-app --editor vscode`), the
extension runs its `runic: dev` or `runic: dev --no-window` task; otherwise it
runs its own task of type `runic`:

```jsonc
// .vscode/tasks.json
{ "type": "runic", "command": "dev", "noWindow": true }  // also "build" and "doctor"; "project" with several projects
```

Its `$runic-dev` and `$runic-dev-no-window` problem matchers are the template's:
`$msCompile`'s pattern for `dev`'s faults (`RAPPDEV`) and the build's errors
(`CS`, `RUNICBRIDGE`), and a background task that is ready at each
`[dev] Ready`. The pattern skips the label that `dev` writes before each line
of a process it runs (`[host] `, `[vite] `). With `dotnet-runic` 0.22 and later,
the extension runs `dev` in its own terminal and reports its problems itself,
because a problem matcher keeps every problem of a build cycle: a C# error
leaves Problems at the `[dev] Ready` after its fix (`dev` is ready only after a
build that succeeded), and warnings stay until the next cycle.

From `dotnet-runic` 0.23, `dev` also prints each frontend (Vite) error as one
line with code `RUNICP008`, for example
`[vite] /…/Frontend/src/App.tsx(29,50): error RUNICP008: [vite:oxc] …`, and
`[vite] [runic] Frontend errors cleared` once the files compile again
(runic-sdk#1212). The extension's terminal shows those errors in Problems and
removes them, and only them, at the clear line; `[dev] Ready` leaves them in
place. A process task's problem matcher shows them too, but keeps them until
the next build cycle (runic-editors#52).

The extension follows `obj/runic/dev-session.json`
(`runic.application.tool.dev-session/1`, see "Session file" in the
[dotnet-runic README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md#ide-contract)),
which `dev` replaces on each change, wherever `dev` was started: a task, a
terminal or another tool. The **Dev Session** view shows the state, the
development server (open it in the browser), the Window's process or "started by
the IDE", the pages, the debug endpoints and the generated clients' directory.
`"starting"` and `"ready"` count only while the file's top-level `processId` is a
running `dotnet runic dev`: a `dev` that was killed (for example when VS Code
quit while its task ran) leaves `"ready"` behind, and the extension shows it as
stopped (`stopped (process 41250 exited)`). On Linux and macOS it checks the
process's command line, so a process ID that another program reuses also reads
as stopped; on Windows it checks that the process is `dotnet.exe` or
`dotnet-runic.exe`. The file is read again on each change and every 2 seconds.
For such a killed `dev`, the extension touches `obj/runic/dev-stopped` beside the
session file once, as `dev` does when it stops, so the next build puts the
production frontend back in `www/`.
**Stop dotnet runic dev** ends the task; a `dev` started in a terminal gets the
signal of Ctrl+C after you confirm (on Windows, stop it in its terminal).

With `dotnet-runic` 0.22 and later (the tool manifest's version, else
`dotnet runic --version`), the extension's own `dev` task passes
`--debug-endpoint` when `dev` runs its Window, so a CS-WebUI Window opens its
DevTools Protocol port (D070.17), and runs `dev` with `RUNIC_DEV_STOP_ON_INPUT=1`
in a terminal of its own. **Stop dotnet runic dev**, Ctrl+C in that terminal and
closing it write `stop` to `dev`'s standard input and close it, so `dev` stops
as on Ctrl+C on every platform, also on Windows: the session file says
`"stopped"` and the development pages leave `www/`. Only when `dev` has not
exited within 10 seconds is it ended by force. With an older tool, and for the
template's own `runic: dev` tasks, the task runs and stops as before.

### F5: debug the Window

**Debug the Window (F5 with dotnet runic dev --no-window)** starts the
template's `Window` configuration (`.vscode/launch.json`, `preLaunchTask`
`runic: window`) when the folder has one. Otherwise, and for F5 in a workspace
without a `launch.json`, it uses the extension's `runic` configuration:

```jsonc
// .vscode/launch.json
{ "type": "runic", "request": "launch", "name": "Runic: Window" }  // optional "project", "env"
```

It starts `dotnet runic dev --no-window` (or reuses one that runs; a `dev` that
runs the Window itself is restarted after you confirm), waits until the session
file says ready, builds the project, and starts the program that `dotnet run`
would start (MSBuild's `RunCommand`; on macOS the development bundle's
executable) under the C# extension's `coreclr` debugger, with the environment the
session file names for it (`DOTNET_ENVIRONMENT=Development`, and
`RUNIC_APPLICATION_DEVELOPMENT_ASSETS` without a development server). It needs the
[C# extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp).

### Doctor and Runic diagnostics

**Run dotnet runic doctor** runs `dotnet runic doctor --format json --fail-on never`
for the project and lists every check in the **Doctor** view, failures first,
with its remediation. Failing checks and warnings also appear in the Problems
panel on the project file; their quick fix **Run dotnet runic doctor again**
checks again. Diagnostics with Runic codes (`RUNICBRIDGE`, `RAPPDEV`, `RAPPCLI`,
`RAPPPKG`, `RCLI`), from the `dev` and build tasks' problem matchers, get the quick
fix **Show <code> in the Runic diagnostics catalog**, which opens the code's entry
at the release tag of the project's `dotnet-runic` tool; `RAPPDEV1007`,
`RAPPDEV1008` and `RAPPCLI1009` also offer to run doctor.

### ViewModels

The **ViewModels** view lists the ViewModels with a generated Bridge and their
members (state, setters, command state, commands, interactions, View requests)
from the development source map that each build writes
(`obj/<Configuration>/<TargetFramework>/runic-bridge/sources.json`, the newest
one; see "Development source map" in the
[Runic.Application.Views README](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#development-source-map)).
Selecting an entry (**Open C# Member**) opens its C# location: a command at its
handler, a CommunityToolkit `[ObservableProperty]` at its field. The view follows
each build, also the rebuilds of `dotnet runic dev`. Entries without a location
(a build without a PDB) are listed without one.

### Translations

The **Translations** view shows `runic-translations status` for each
`runic.json` project of the workspace: per locale the coverage, with the details
of **Show Translation Status** in its tooltip. It follows saved changes, as the
status bar coverage does.

### Commands

| Command | What it does |
| --- | --- |
| Runic: Start dotnet runic dev | The development server and the Window |
| Runic: Start dotnet runic dev --no-window (frontend only) | The frontend; the debugger starts the Window |
| Runic: Stop dotnet runic dev | Ends the task, or a `dev` started in a terminal |
| Runic: Debug the Window (F5 with dotnet runic dev --no-window) | The template's `Window` configuration, or the extension's |
| Runic: Open the Development Server | The session's development server in the browser |
| Runic: Open the dev Session File | `obj/runic/dev-session.json` |
| Runic: dotnet runic dev Actions… | The status bar item's choices |
| Runic: Run dotnet runic doctor | The Doctor view and Problems |
| Runic: Refresh ViewModels | Rereads the development source map |
| Runic: Open C# Member | From the ViewModels view |
| Runic: Refresh Translation Status | The Translations view |
| Runic: Show Runic SDK Output | The **Runic** output (dev session changes, doctor) |

## Install the tool

The extension runs the workspace's own `runic-translations` .NET tool, so the
language server always matches the project's Translations release. It does not
install or restore tools for you. In the repository (or solution) root:

```sh
dotnet new tool-manifest --output .config
dotnet tool install dotnet-runic-translations --version <VERSION>
dotnet tool restore
```

Use the same exact version as the project's Runic Translations packages, and
commit `.config/dotnet-tools.json`; collaborators then run only
`dotnet tool restore`. The tool needs the .NET 10 runtime. Projects created with
the `runic-translations` templates already contain the manifest.

## Starting and status

Open a trusted workspace containing a restored local `runic-translations` .NET
tool. The extension starts `dotnet tool run runic-translations -- lsp` from the
nearest tool-manifest directory.
If the server cannot start (no tool manifest, an unrestored tool, a missing
.NET runtime), a notification says why once per workspace folder, with
**Show output** and **How to install the tool**; **Runic: Restart Language
Server** tries again. The status bar shows the server state for the active
`.mf2`, `.rmf2` or `runic.json` file and, while the server runs, the catalog ID
and the file's locale (`Runic: app · de`). Select it, or run **Runic: Show
Output**, for the server log.

For SDK development, set `runic.translations.serverAssembly` to the built tool DLL;
`runic.dotnetPath` selects the installed .NET executable. Separate
workspace folders get separate server instances; the server's `runic.*` commands
(such as the **Mark reviewed** quick fix's) are registered once and run against
the folder of the file they name. Restarting reopens unsaved
buffers; stopping releases watchers and terminates the language-server client.
For grouped projects, declared `sourceRoots` inside the opened workspace are
covered by one canonical recursive watch plan, so mounted add/change/delete/rename
events outside the config directory are reconciled without duplicate nested
watchers. Open a common containing workspace for such mounts; roots outside that
boundary and symlink/reparse-point escapes are not watched.

## Diagnostics, hover and `runic.json`

Diagnostics cover the whole catalog, not only open files: while any file of a
project is open, catalog errors and warnings in its closed translations and in
`runic.json` appear in the Problems view, and they are cleared when the last
file of the project closes. Each `RTR` code links to its entry in the
[diagnostics reference](https://github.com/Runic-Artifex/runic-translations-sdk/blob/main/docs/guides/translations/diagnostics.md)
at the release tag of the tool. Hover over a message in a translation shows the
base-locale text with its comments and `@param` descriptions, and its review
state from the project's review file (`<catalog>.review.json` beside
`runic.json`, the file `runic-translations review mark` and the Translations
Editor write), marked stale when the source changed since it was recorded.

A stale review entry is reported as `RTR0090`; its quick fix **Mark … reviewed**
records the entry's state again for the source on disk in the review file. It is
offered, and runs, only while the files with the base message and the translation
have no unsaved changes. Review warnings, hover and quick fixes also work while
the catalog has errors elsewhere, such as a locale that misses keys.
`RTR0095` (plural categories that fall to `*`) has a quick fix that adds the
missing variants as copies of the `*` variant.

`runic.json` files are validated and completed against the project schema
bundled with the extension, copied from the specification on every build. A
file that declares `$schema` uses that schema instead (VS Code downloads the
published URL); templates write the published `project-v1.schema.json` URI.

## Commands and preview

| Command | What it does |
| --- | --- |
| Runic: Restart Language Server | Stops and starts the language server; reopens unsaved buffers |
| Runic: Show Output | The language server's log |
| Runic: Preview Message | The message preview panel |
| Runic: Change Preview Samples | Another `@example` sample set, or custom values |
| Runic: Change Preview Locale | The open panel's language, pseudo-locales included |
| Runic: Change Preview Regional Locale | The locale a user requests, such as `de-CH` for `de` text |
| Runic: Change Preview Time Zone | The zone `timeZone=local` formats in |
| Runic: Rename Input in Resource Sources | F2 on the input (protocol 2), or a pick list and an input box (0.13) |
| Runic: Rename Slot in Resource Sources | F2 on the slot reference (protocol 2), or a pick list and an input box (0.13) |
| Runic: Rename Resource in Resource Sources | F2 on the resource (protocol 2); renames in resource sources only |
| Runic: Extract Group to Directory | The Extract code action (protocol 2) |
| Runic: Inline Resource into Ancestor | The Inline code action (protocol 2), or a file dialog (0.13) |
| Runic: Export XLIFF | One XLIFF 2.1 document per translated locale |
| Runic: Import XLIFF (dry run) | What an import would do, written to the output |
| Runic: Override Library Message… | Adds a library message to the application's overrides |
| Runic: Show Translation Status | Coverage per locale |
| Runic: Import .resx or i18next Catalog | A dry run, then the import after you confirm |
| Runic: Refresh Translation Status | The Translations view |

**Export XLIFF** writes one XLIFF 2.1 document per translated locale into a
folder you choose, and **Import XLIFF (dry run)** reports what importing a
translated document would do, unit by unit, without writing anything (the
output goes to **Runic: Show Output**). Both use the project of the active file (the
nearest `runic.json` above it), or ask which project when no file of one is
active and the workspace has several. Both run the workspace's local
`runic-translations xliff` tool, the same code as the Translations Editor; apply
an import with `runic-translations xliff import` or the editor.

## Library catalogs and overrides

An application overrides the text of a library catalog that an installed package packs (runic.json `"overrides"`,
`translations/overrides/<catalog>/<locale>.rmf2`). The language server finds the library in the restore output
(`obj/project.assets.json`) or in `node_modules`, as the build and the Vite plugin do, and treats override sources as
overlays on it: an unknown key (`RTR0103`), changed inputs and markup in a plain-text message are reported as the build
reports them, completion offers the library's keys and inputs, hover shows the library's base text (and its text in the
file's locale, or the fallback of a locale the application adds), and **Go to Definition** opens the library's source as
a read-only document. A library update that changes the base text of an overridden message is reported (`RTR0090`).
Restore the project first; without the package the override sources report `RTR0104`. The extension watches the restore output
(`obj/project.assets.json`) and the package managers' install state and lockfiles, so the language server picks up a restore or an install
without a restart.

**Override Library Message…** asks for an installed library catalog, one of its messages and a locale (a library locale,
or another one the application adds; a typed tag is canonicalized, so `de_ch` becomes `de-CH`), then runs the workspace's local `runic-translations overrides add`: it adds the
message to `translations/overrides/<catalog>/<locale>.rmf2`, seeded with the library's text and with the base text as
comments, records that base text, lists the catalog in runic.json `"overrides"` when needed, and opens the file at the
message.
**Show Translation Status** shows `runic-translations status` for the project:
per locale the messages translated, served by a fallback locale, missing and
stale, the coverage, and whether missing translations are errors or warnings
(the locale's `validation.translationCompleteness`), the same report as the
command line and the Translations Editor. The status bar adds the coverage of
the active file's locale (`Runic: app · de 87.5%`, details in the tooltip); it
follows saved changes.

**Import .resx or i18next Catalog** migrates an existing catalog into the
project's `.rmf2` sources with `runic-translations import`: it runs a dry run
first, shows its outcome per message in **Runic: Show Output**, and imports
after you confirm, optionally replacing the text of messages that already exist.

The Runic Translations commands in the Command Palette provide inert example previews,
group extraction/inlining for `.rmf2`, and explicitly source-only
input/slot/resource renames. Preview asks the server to execute the selected
semantic contract's verified .NET runtime plan and renders the returned inert runs. Links/actions are
inert, icons are placeholders and custom markup is labeled; no application
callbacks or application startup are involved. Custom renderer code is never
loaded by the extension.

**Preview Message** offers every `@example` sample set plus custom values. The
selected locale and values are remembered per message during the extension
session; reopening a preview reuses them. **Change Preview Samples** lets you
choose another example or edit custom values. **Change Preview Locale** switches
the open panel's language; it also lists the pseudo-locales that `runic.json`
configures (`pseudoLocales`: `en-XA`, `ar-XB`), which the server generates from
the base locale exactly as a `--pseudo-locales` build does (read-only).
**Change Preview Regional Locale** sets the locale a user requests, such as
`de-CH` for `de` text: numbers and dates then format as the runtime formats a
`de-CH` request that resolved to `de`. **Change Preview Time Zone** sets the zone
that `timeZone=local`, the default of `:datetime` styles, formats in (`utc`, an
IANA zone, or the host's). The panel names the requested locale, how it relates
to the shown locale (exact, parent or related), the locale numbers and dates
formatted in and the time zone, and lays the text out in the locale's direction.
The regional locale and time zone stay for the next message. Samples for the
128 most recently previewed message/locale combinations are retained. The panel refreshes from
the latest unsaved resource/configuration buffers and watched source changes.
Out-of-order requests and cancelled selections cannot replace a newer preview.

The LSP owns direct and grouped MF2 plus configuration synchronization. Existing C#, TypeScript,
Svelte language services retain their files. Resource-source edits are not
application-call-site refactors. In a workspace containing application or other
non-resource sources or unindexable links, standard resource F2 asks before it
renames in resource sources only (Translations 0.14), or refuses (Translations
0.13), where the explicit **Rename Resource in Resource Sources** command
intentionally applies the narrower transaction. Use the native language service
for generated API usages and review source-only input/slot operations against
application bindings. Unsupported language operations must not be treated as a
successful whole-application rename.

### Refactorings: F2 and the light bulb

With Translations 0.14 (the language server's protocol 2, which the extension
requests with `initializationOptions.runicProtocol: 2`), the Translations
refactorings take the editor's standard paths, and the server applies their
edits. The host test runs them against `dotnet-runic-translations` 0.14.0:

- **F2** renames a resource, an MF2 local, a caller input (on `$name`, in every
  locale's resource) or a slot reference (on the value of `ref=`, also in the
  `runic.json` slot contracts).
- **The light bulb** (Ctrl+.) on a group's name offers **Extract group 'x' to
  x/en.rmf2**; in a feature-local resource it offers **Inline into
  ../en.rmf2**, one action per ancestor resource of the same locale (this
  replaces the file dialog). VS Code deletes the inlined file as part of the
  edit. Visual Studio cannot delete files through LSP, so there the server
  empties the file and Runic DevKit for Visual Studio deletes it once it is
  saved (see its
  [README](../visualstudio/README.md#refactorings-f2-and-the-light-bulb)).
- A resource rename in a workspace with application sources asks first, with
  **Rename in resource sources** or **Cancel** (the server's
  `window/showMessageRequest`).
- The server builds a light-bulb refactoring's edit when VS Code resolves the
  action, and rejects it as outdated when files changed meanwhile, for example
  through the file watcher after a save. The extension then resolves the action
  once more; if that is outdated too, it says so in a warning (run the action
  again) instead of applying nothing silently (runic-editors#32).

The palette commands remain: **Rename Input**, **Rename Slot** and **Rename
Resource in Resource Sources** move the cursor to the symbol and open the F2
rename box; **Extract Group to Directory** and **Inline Resource into Ancestor**
open the code action of that kind. With Translations 0.13 they keep their 0.21
behaviour (a pick list and an input box, a file dialog for Inline), and F2
renames resources and MF2 locals only. The extension asks the server for
protocol 2 and falls back when it does not offer it; **Runic: Show Output**
logs `Runic protocol 1` or `2` for each workspace folder.

## VS Code and Visual Studio

Runic DevKit for Visual Studio has the same Runic SDK and Translations
features, with Visual Studio's own menus, tool window and Error List. The
[Visual Studio README](../visualstudio/README.md#visual-studio-and-vs-code)
compares the two extensions feature by feature and lists what Visual Studio
does not have yet.

## Build and test

Build and test in the runic-editors development shell (see the repository
README), from the `vscode` directory:

```sh
bun install --frozen-lockfile
bun run check
bun run test
bun run build
bun run test:host
bun run package
```

The host check uses the installed `code`, or `VSCODE_EXECUTABLE_PATH`, with an
isolated profile, extension directory and a multi-root workspace of a copy of
`fixtures/rmf2` (Translations only), of `fixtures/sdk-app` (a Runic SDK
project) and a second copy of `fixtures/rmf2`, whose own language server shows
that the folders share the server's `runic.*` commands. Each Translations fixture gets the repository's
`.config/dotnet-tools.json`, and `dotnet tool restore` installs that
`dotnet-runic-translations` release from NuGet, so the extension starts the
language server as it does in a user's workspace. The SDK fixture is never
built: the test writes the session file and the source map that `dotnet runic
dev` and the build would, with a stand-in `dotnet runic dev` process whose death
the session must follow, and a stand-in `dotnet` answers `dotnet runic doctor`. Set
`RUNIC_TRANSLATIONS_SERVER_ASSEMBLY` (or `RUNIC_TRANSLATIONS_SERVER`) to a built
`dotnet-runic-translations.dll` to test a local server build instead. The test
follows the protocol the server offers: against a protocol 2 server (Translations
0.14) it also runs F2 on an input and the Inline Resource and Extract Group code
actions through the palette commands, and checks that the `runic.*` commands
apply their edits. `bun run test:host:protocol2` requires protocol 2 and fails
against an older server:

```sh
RUNIC_TRANSLATIONS_SERVER=<runic-translations-sdk>/tools/dotnet-runic-translations/bin/Release/net10.0/dotnet-runic-translations.dll \
  bun run test:host:protocol2
```
 The check requires an assertion
report, not merely a zero CLI exit code. Failure logs are retained under
`artifacts/host-failure`. Packaging produces `artifacts/runic-devkit.vsix`;
no marketplace upload or user-profile installation is part of these commands.

The `version` in `package.json` is a placeholder. `bun run package` (`package.mjs`)
stamps the release version of `eng/version.json`, mapped to `x.y.z`, into a
staged copy and packs that: the plain `0.8.0` becomes `0.8.1000`, `1.0.0-rc.N`
becomes `1.0.(500 + N)` packaged with `--pre-release`, and the final `1.0.0`
becomes `1.0.1000`. It fails if the VSIX does not carry that version, the
pre-release flag of a `-preview.N`/`-rc.N` release, and the Preview label. See
[IDE extension versions](https://github.com/Runic-Artifex/runic-editors/blob/main/eng/release/README.md#ide-extension-versions) for
the full mapping. Each GitHub release attaches the attested VSIX that CI built.

The language client follows Microsoft's [Language Server Extension Guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide).
