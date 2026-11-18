# Visual Studio native check

Runic DevKit for Visual Studio has unit tests of its Core library (`../Core.Tests`, every CI run on Linux and
Windows) and this native check, which runs the installed VSIX in Visual Studio 2026 on a Windows desktop. Hosted CI
runners have no signed-in desktop, so the check runs by hand on a Windows machine before each release
([release runbook](../../eng/release/README.md#publishing-to-the-visual-studio-marketplace)). Parts that need human
eyes are the [manual checklist](#manual-checklist) below.

## Run it

Requirements: Windows 11 with Visual Studio 2026 (18.x, x64) and a .NET 10 SDK, signed in to the desktop as the user
that runs the check (no elevation). The repository's `global.json` pins an SDK; the check builds nothing in the
repository, it only needs `dotnet` for `dotnet tool restore`, `dotnet restore` and `dotnet new sln` in a temporary
folder.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File visualstudio/test/run-native-checks.ps1 -Vsix <runic-devkit-vs.vsix>
```

Use the `visualstudio-vsix` artifact of the CI run of the commit under test. The script works over SSH (session 0)
as well as from a console:

1. It uninstalls Runic MF2 Translations (`Runic.Artifex.Translations.Rmf2`, the former identity) and installs the
   VSIX with `VSIXInstaller.exe /quiet` into the experimental instance `/RootSuffix RunicRmf2` (`-RootSuffix`), then
   runs `devenv /updateconfiguration` for that instance.
2. It starts `native-host.ps1` in the signed-in desktop session through a temporary Interactive scheduled task, waits
   for it (`-TimeoutSeconds`, default 900) and removes the task.
3. It prints the progress log and `PASS Runic DevKit <version> native check`, or fails with the first failed check.
   The receipt (`-ReceiptPath`, default `visualstudio/artifacts/native-host-<time>.json`), its `.log` and Visual
   Studio's activity log (`.activity.xml`) stay next to each other.

Close every Visual Studio instance of that root suffix first: the check starts and quits its own. `-SkipInstall`
checks the VSIX that is installed already. runic-sdk's `tests/native/Runic.Desktop.WebViewSmoke/run-windows-ui-automation.ps1`
can also start `native-host.ps1` (`-AutomationScript`) once the VSIX is installed; its timeout is at most 240 s.

The first start of a new experimental instance shows Visual Studio's first-run dialogs. Start
`devenv /RootSuffix RunicRmf2` once by hand and close them before the first automated run.

## What it checks

`native-host.ps1` copies `fixtures/rmf2` and `fixtures/sdk-app` to a temporary folder, writes a tool manifest with
this repository's `dotnet-runic-translations` and `dotnet-runic` (`-RunicToolVersion`, default 0.21.0), restores
them and the SDK project, and puts the project into a solution with a minimal SDK project around `fixtures/rmf2`
(so its files are project items). Then, in Visual Studio:

| Check | How |
| --- | --- |
| The VSIX is installed | Its manifest in the instance's `Extensions` folder has the expected identity and version; the former identity is absent |
| The solution opens | DTE reports the solution with the Runic SDK project |
| Commands | Every button of `Commands.vsct` is registered (command set and ID). The receipt lists those Visual Studio does not name: commands placed only on the tool window's toolbar (Refresh ViewModels, Refresh Translation Status) |
| The package loads | `Runic.ShowToolWindow` opens the Runic tool window with its Dev Session, ViewModels, Doctor and Translations sections (UI Automation) |
| Dev session followed | A session file (`obj/runic/dev-session.json`, state `ready`, Window owned by the IDE) of a running stand-in process reads as ready in the Runic SDK Output pane and the Dev Session section; Open the dev Session File opens it; when the process ends, the session reads as stopped |
| F5 asks first | With no `dev --no-window` running, F5 (a key press posted to Visual Studio's window) shows the D070.6 question about the project, and Cancel leaves the debugger in design mode |
| Doctor runs | Run dotnet runic doctor runs the real tool on the project and its summary reaches the Runic SDK pane |
| Language server | It starts (Runic Translations pane) when an RMF2 document opens |
| Translations | Preview Message renders inert rich content, reports and recovers from an invalid number, switches locale and has samples; Restart Language Server keeps an unsaved buffer |
| Refactorings (protocol 2) | The light bulb's **Inline into ../en.rmf2 (leaves en.rmf2 empty)** empties `account/en.rmf2`; saving it alone keeps it, saving `en.rmf2` too deletes it (closed, no longer a project item). **Extract group** creates it again, and Extract Group into a folder that does not exist (`profile/`) creates the folder and keeps the messages after Save All. An emptied child written into before the save is kept. F2 renames an input in every locale and a slot with its `runic.json` contract; F2 on a resource asks in an InfoBar (`window/showMessageRequest`, the sdk-app's C# makes the workspace mixed): Cancel keeps it, **Rename in resource sources** renames it. F2 to an invalid input name shows the server's refusal in an InfoBar and the Runic Translations pane |

The stand-in follows the VS Code host test (`vscode/test/run-host.mjs`): a real `dotnet runic dev` needs a frontend
toolchain and minutes, and the follower only reads the session file and checks the process.

## Manual checklist

Run these on the same machine with a real app (`dotnet new runic-app --frontend react --package-manager bun`, the
release's templates), in the experimental instance with the VSIX installed. Record the Visual Studio version and the
result of each line in the release PR.

| # | Area | Do | Expect |
| --- | --- | --- | --- |
| 1 | Look | Open View > Other Windows > Runic in the light and the dark theme | Sections, icons and toolbar read in both themes; nothing clipped at 150 % scaling |
| 2 | Activation | Open a solution without a Runic project, then one with only `.rmf2` files, and open View > Other Windows > Runic in each | Without a Runic project there is no Tools > Runic; with only `.rmf2` files Tools > Runic has the Translations commands, Runic and Options only (no `dev`, doctor or debugging commands), and the tool window and its toolbar show the Translations section and its buttons alone |
| 3 | Former extension | Install the 0.21 `runic-translations-visualstudio.vsix` beside Runic DevKit | An InfoBar offers to uninstall Runic MF2 Translations |
| 4 | Start failure | Rename `.config/dotnet-tools.json` and open an `.rmf2` file | One InfoBar with "Show output" and the install help |
| 5 | Options | Tools > Options > Runic DevKit: set the .NET executable and trace level | Run Doctor uses the new executable; the Translations pane shows the trace |
| 6 | dev | Start dotnet runic dev, then Stop | The Runic SDK pane shows `dev`; Dev Session shows the server, Window, pages; Open the Development Server opens the browser; Stop ends `dev` within 15 s and the session reads as stopped |
| 7 | dev errors | While `dev` runs, break a C# file (a ViewModel), then fix it; break `Frontend/src/App.tsx`, then fix it | The C# error is an Error List row with file and line that opens the file there; after the fix it goes once the Runic SDK pane shows `[dev] Ready`. The `.tsx` error shows in the Runic SDK pane (Vite's output) and the page's overlay, not in the Error List ([Known limits](../README.md#known-limits)) |
| 8 | F5 guard | F5 without `dev --no-window` | The question; Yes starts `dev --no-window` and then the Window, No starts the Window alone, Cancel starts nothing |
| 9 | F5 up-to-date (sdk#1091) | F5 twice; edit `Frontend/src/App.tsx`, F5; start and stop `dev --no-window`, F5 | The second F5 builds nothing; the edit and the stop each make F5 build, and the Window then shows the production page. Tools > Options > Projects and Solutions > SDK-Style Projects > Up to date checks > Logging level: Verbose names the `RunicFrontend`/`RunicDevSession` items in the Build pane |
| 10 | Debug the Window | Breakpoints in a ViewModel and in `Frontend/src/*.tsx`, then Tools > Runic > Debug the Window | Both breakpoints hit in one debugging session |
| 11 | Attach to the page | A Window started with the CDP port (or `dev --browser` in Edge with `--remote-debugging-port`), then Attach to the Page | TSX breakpoints hit |
| 12 | ViewModels | Build, then click a ViewModel member in the ViewModels section | The C# file opens at the member |
| 13 | Doctor | Run Doctor on a project with a failing check | The Doctor section and the Error List show it; the help link opens the tool's README |
| 14 | Translations tools | Export XLIFF, Import XLIFF (Dry Run), Import Catalog, Override Library Message, Show Translation Status | Each writes its result to the Translations pane; library documents open read-only |
| 15 | Refactoring | In the real app: F2 on an `$input`, a slot and a resource; the light bulb's Extract Group and Inline Resource (Translations 0.14); then Save All, and check Solution Explorer and Git Changes | The edits apply. Visual Studio saves F2's renames at once, open documents included (its LSP client's behaviour); the light bulb's edits stay open and unsaved until saved. After the save, the file Inline Resource emptied is gone from disk, Solution Explorer and the project, and Git Changes shows it deleted |
| 16 | Keyboard | Open the tool window from View > Other Windows > Runic and from Tools > Runic > Runic with the keyboard; reach its toolbar, every section header and every tree row; Narrator reads the names | Everything is reachable and named; every Tab stop is a section header, a tree, a button or a toolbar item |
