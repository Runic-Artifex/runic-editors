# The runic-app template's VS Code tasks

`tasks.json` is runic-sdk's `tools/Runic.Application.Templates/content/runic-app/.vscode/tasks.json` at 02d7634
(runic-sdk 0.24, D087), as `dotnet new runic-app --editor vscode` writes it without `--library`. The VS Code host test
(`vscode/test/run-host.mjs`, `sessionFailure` in `vscode/test/host.cjs`) puts it into its copy of `fixtures/sdk-app`
and runs its `runic: dev` task through VS Code's own task system, as F5's `preLaunchTask` and Terminal > Run Task do,
with a stand-in `dotnet` on `PATH` whose `runic dev` fails at its start and writes the session file's `failure`. VS Code
removes the task's problems when it ends; DevKit must still show the failure in Problems and its dependency tree. Update
it when the template's dev tasks change.
