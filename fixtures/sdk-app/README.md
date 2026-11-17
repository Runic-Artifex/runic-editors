# Runic SDK app fixture

The VS Code host test (`vscode/test/run-host.mjs`) opens this directory next to `fixtures/rmf2` in a multi-root
workspace: `rmf2` is a Translations-only folder, this one a Runic SDK project (a Window project that references
`Runic.Application.Views.Desktop`). The project is never built or restored; the test writes the files that
`dotnet runic dev` and the Bridge generator would (`obj/runic/dev-session.json`,
`obj/Debug/net10.0/runic-bridge/sources.json`) and answers `dotnet runic doctor` with a stand-in `dotnet`.

The Visual Studio native check (`visualstudio/test/native-host.ps1`) opens a copy of it in a solution beside
`fixtures/rmf2`, follows a session file of a stand-in `dev` process the same way, and runs the real `dotnet runic
doctor` on it. For that the copy is restored (`dotnet restore`, which imports the Runic Views targets) and
`Frontend/package.json` gives the Window project its frontend; the frontend is never installed or built.
