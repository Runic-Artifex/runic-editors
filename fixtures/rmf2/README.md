# RMF2 payment fixture

The test fixture of both extensions: the VS Code host test (`vscode/test/run-host.mjs`) and the Visual Studio
integration check (`visualstudio/test/native-host.ps1`) copy this directory to a temporary workspace and edit it
there. It combines two links, a conditional action, an accessible standalone icon, a custom badge with a dynamic
enum option and a feature directory (`account/`).

Copied from runic-translations-sdk 0.11.0 (`specs/translations/examples/rmf2`, without its browser demo), where
the maintained example and its renderer guide live.
