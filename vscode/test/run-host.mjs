import { mkdtemp, mkdir, cp, writeFile, readFile, rm, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
// Runs test/host.cjs in a real VS Code against the language server of the supported Translations release: the
// fixture workspace gets this repository's .config/dotnet-tools.json (dotnet-runic-translations at a pinned version)
// and `dotnet tool restore` installs it from NuGet, so the extension starts `dotnet tool run runic-translations -- lsp`
// as it does for users. RUNIC_TRANSLATIONS_SERVER_ASSEMBLY (or RUNIC_TRANSLATIONS_SERVER) =<dotnet-runic-translations.dll>
// tests a locally built server instead (runic.translations.serverAssembly). The host test takes the protocol 2 path
// when the server advertises it (Translations 0.14 and later). The pinned release speaks protocol 2, so a run against
// it fails when the server does not; for a local server build, RUNIC_TRANSLATIONS_PROTOCOL=2
// (`bun run test:host:protocol2`) insists on it, and without it the test follows what the server advertises.
// The workspace has a second folder, a copy of fixtures/sdk-app (a Runic SDK project), for the Runic SDK features: a
// stand-in for `dotnet runic dev` (a process whose command line names dotnet-runic.dll with dev) gives the session file
// a live process ID, and a stand-in `dotnet` (runic.dotnetPath of that folder) answers `dotnet runic doctor`.
// F5 of the `runic` type runs against test/coreclr-stand-in, a second development extension that stands in for the C#
// extension's coreclr debugger and records the launch request.
// A third folder, another copy of fixtures/rmf2, gives the test a second language server (multi-root).
const extension = fileURLToPath(new URL('../', import.meta.url));
const repository = resolve(extension, '..');
const temporary = await mkdtemp(join(tmpdir(), 'runic-vscode-host-'));
let passed = false;
const environment = { ...process.env, ELECTRON_ENABLE_LOGGING: '1', RUNIC_VSCODE_RESULT_FILE: join(temporary, 'result.json'), XDG_RUNTIME_DIR: join(temporary, 'runtime') };
const fakeDev = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)', '--', '/tools/dotnet-runic.dll', 'dev'], { stdio: 'ignore' });
await new Promise((accept, reject) => { fakeDev.once('spawn', accept); fakeDev.once('error', reject); });
environment.RUNIC_FAKE_DEV_PID = String(fakeDev.pid);
// A second stand-in dev for the F5 test, which the session test above does not kill.
const fakeF5Dev = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)', '--', '/tools/dotnet-runic.dll', 'dev', '--no-window'], { stdio: 'ignore' });
await new Promise((accept, reject) => { fakeF5Dev.once('spawn', accept); fakeF5Dev.once('error', reject); });
environment.RUNIC_FAKE_F5_DEV_PID = String(fakeF5Dev.pid);
if (process.env.WAYLAND_DISPLAY && !process.env.WAYLAND_DISPLAY.startsWith('/') && process.env.XDG_RUNTIME_DIR) environment.WAYLAND_DISPLAY = join(process.env.XDG_RUNTIME_DIR, process.env.WAYLAND_DISPLAY);
await mkdir(environment.XDG_RUNTIME_DIR, { mode: 0o700 });
for (const name of ['VSCODE_IPC_HOOK_CLI', 'VSCODE_CLI', 'ELECTRON_RUN_AS_NODE']) delete environment[name];
try {
  const serverAssembly = process.env.RUNIC_TRANSLATIONS_SERVER_ASSEMBLY || process.env.RUNIC_TRANSLATIONS_SERVER;
  // The NuGet tool of .config/dotnet-tools.json (Translations 0.14.0) speaks protocol 2.
  if (!serverAssembly) environment.RUNIC_TRANSLATIONS_PROTOCOL ??= '2';
  const translations = async folder => {
    await cp(join(repository, 'fixtures/rmf2'), folder, { recursive: true });
    if (serverAssembly) {
      await mkdir(join(folder, '.vscode'));
      await writeFile(join(folder, '.vscode/settings.json'), JSON.stringify({ 'runicTranslations.serverAssembly': resolve(serverAssembly) }));
    } else {
      // The repository's NuGet.config keeps packages in .cache/nuget; the temporary workspace has no NuGet.config.
      environment.NUGET_PACKAGES ??= join(repository, '.cache/nuget');
      await mkdir(join(folder, '.config'));
      await cp(join(repository, '.config/dotnet-tools.json'), join(folder, '.config/dotnet-tools.json'));
      await cp(join(repository, 'NuGet.config'), join(folder, 'NuGet.config'));
      execFileSync('dotnet', ['tool', 'restore'], { cwd: folder, env: environment, stdio: 'inherit' });
    }
  };
  const workspace = join(temporary, 'workspace');
  await translations(workspace);
  const second = join(temporary, 'second');
  await translations(second);
  const app = join(temporary, 'app');
  await cp(join(repository, 'fixtures/sdk-app'), app, { recursive: true });
  // A client library beside the Window project (devtools-library-dx S8): no dev target, a frontend dependency.
  await cp(join(repository, 'fixtures/sdk-library'), app, { recursive: true, filter: source => !source.endsWith('README.md') });
  const doctorEnvelope = { protocol: 'runic.commandline/1', command: 'doctor', success: true, exitCode: 0, payloadType: 'runic.application.tool.doctor/1', fault: null, diagnostics: [], payload: {
    project: join(app, 'App.csproj'), host: 'desktop', target: null, healthy: false, summary: { passed: 1, warnings: 1, failed: 1 }, hosts: ['desktop'], checks: [
      { id: 'dotnet-sdk', status: 'pass', message: '.NET SDK 10.0.403.', remediation: null },
      { id: 'lock-file', status: 'warn', message: 'The frontend has no lock file.', remediation: 'Run npm install in Frontend.' },
      { id: 'desktop-runtime', status: 'fail', message: 'WebKitGTK 4.1 is missing.', remediation: 'Install webkitgtk_4_1.' }] } };
  await writeFile(join(app, 'doctor.json'), JSON.stringify(doctorEnvelope));
  // It also builds nothing for F5 (each build adds a line to builds.log), stands in for a dev that exits at once and for a fresh clone's unrestored tools (tools-missing), and answers dotnet msbuild -getProperty (the Window's run properties) from run-properties.json.
  await writeFile(join(app, 'fake-dotnet.sh'), `#!/bin/sh\nif [ "$1" = runic ] && [ "$2" = doctor ]; then cat '${join(app, 'doctor.json')}'; exit 0; fi\nif [ "$1" = build ]; then echo "$2" >> '${join(app, 'builds.log')}'; exit 0; fi\nif [ "$1" = msbuild ]; then cat '${join(app, 'run-properties.json')}'; exit 0; fi\nif [ "$1" = tool ] && [ "$2" = restore ]; then rm -f '${join(app, 'tools-missing')}'; echo 'Restore was successful.'; exit 0; fi\nif [ "$1" = runic ] && [ -e '${join(app, 'tools-missing')}' ]; then echo "Cannot find a tool in the manifest file that has a command named 'runic'." >&2; echo 'Run "dotnet tool restore" to make the "dotnet-runic" command available.' >&2; exit 1; fi\nif [ "$1" = runic ] && [ "$2" = --version ] && [ -e '${join(app, 'tool-version')}' ]; then cat '${join(app, 'tool-version')}'; exit 0; fi\nif [ "$1" = runic ] && [ "$2" = dev ]; then echo 'Restoring App.csproj'; echo 'error RAPPCLI1001: dev failed early' >&2; exit 3; fi\necho "fake dotnet: $*" >&2; exit 1\n`);
  await chmod(join(app, 'fake-dotnet.sh'), 0o755);
  await mkdir(join(app, '.vscode'));
  await writeFile(join(app, '.vscode/settings.json'), JSON.stringify({ 'runic.dotnetPath': join(app, 'fake-dotnet.sh') }));
  // Inline Resource deletes a file; the test profile has no trash to move it to.
  await mkdir(join(temporary, 'profile', 'User'), { recursive: true });
  // The language client's trace goes to the Runic Translations log, in the failure artifact (#40). vscode-languageclient
  // traces only while that channel's level is Trace, which --log sets below.
  await writeFile(join(temporary, 'profile', 'User', 'settings.json'), JSON.stringify({ 'files.enableTrash': false, 'runic.translations.trace.server': 'messages' }));
  const workspaceFile = join(temporary, 'runic.code-workspace');
  await writeFile(workspaceFile, JSON.stringify({ folders: [{ path: workspace }, { path: app }, { path: second }] }));
  const child = spawn(process.env.VSCODE_EXECUTABLE_PATH || 'code', ['--new-window', '--wait', '--no-sandbox', '--disable-gpu', '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes', '--log', 'runic-artifex.runic-devkit:trace', '--user-data-dir', join(temporary, 'profile'), '--extensions-dir', join(temporary, 'extensions'), `--extensionDevelopmentPath=${extension}`, `--extensionDevelopmentPath=${join(extension, 'test/coreclr-stand-in')}`, `--extensionTestsPath=${join(extension, 'test/host.cjs')}`, workspaceFile], { stdio: 'inherit', env: environment });
  const timeout = setTimeout(() => child.kill('SIGTERM'), 120000);
  const result = await new Promise((accept, reject) => { child.once('error', reject); child.once('exit', accept); });
  clearTimeout(timeout);
  const stage = await readFile(join(temporary, 'result.json.stage'), 'utf8').catch(() => 'activation');
  const report = await readFile(join(temporary, 'result.json'), 'utf8').then(JSON.parse, () => ({ passed: false, message: `${stage}: VS Code exited (${result}) without an assertion report` }));
  if (!report.passed) throw new Error(report.message || "Extension-host assertions did not finish.");
  console.log(report.message);
  if (result !== 0) throw new Error(`VS Code host tests exited with ${result}`);
  passed = true;
} finally {
  fakeDev.kill('SIGKILL'); fakeF5Dev.kill('SIGKILL');
  if (!passed) { await mkdir(join(extension, 'artifacts'), { recursive: true }); await cp(join(temporary, 'profile'), join(extension, 'artifacts/host-failure'), { recursive: true }).catch(() => {}); }
  // RUNIC_HOST_KEEP=1 keeps the temporary workspace and profile (with the logs) of a passing run too.
  if (process.env.RUNIC_HOST_KEEP) console.log(`Kept ${temporary}`);
  else await rm(temporary, { recursive: true, force: true });
}
