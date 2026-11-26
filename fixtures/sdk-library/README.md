# Runic SDK library fixture

The VS Code host test (`vscode/test/run-host.mjs`) copies this directory into its copy of `fixtures/sdk-app`, so the
app folder holds a Window project (`App.csproj`) and a client library (`SharedUi/SharedUi.csproj`) with its frontend
package (`shared-ui/`), as runic-sdk's `examples/shared-library-clients` does. The library is a Runic SDK project but
not a `dev` target. The test writes the files the build and `dotnet runic dev` would write (the dependency graph
`obj/runic/frontend-graph.json`, a session file with `dependencies`, `shared-ui/dist/`) and the workspace files of a
fresh clone; nothing is built or installed. `shared-ui/package.json` declares another package manager than the
workspace the test writes, for the `RUNICBRIDGE089` quick fix.
