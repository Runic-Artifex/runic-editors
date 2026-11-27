# The runic-app template's VS Code tasks

`tasks.json` is runic-sdk's `tools/Runic.Application.Templates/content/runic-app/.vscode/tasks.json` at 20bbaa8
(runic-sdk 0.24), as `dotnet new runic-app --editor vscode` writes it without `--library`. The VS Code host test
(`vscode/test/run-host.mjs`, `templateDevTask` in `vscode/test/host.cjs`) puts it into its copy of `fixtures/sdk-app`
and starts `dotnet runic dev` through DevKit: with this file DevKit runs the template's `runic: dev` task, and a `dev`
that stops at its start must still leave its errors in Problems and the dependency tree. Update it when the template's
dev tasks change.
