import { mkdtemp, mkdir, cp, writeFile, readFile, rm, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
// Runs test/host.cjs in a real VS Code against the language server of the supported Translations release: the
// fixture workspace gets this repository's .config/dotnet-tools.json (dotnet-runic-translations at a pinned version)
// and `dotnet tool restore` installs it from NuGet, so the extension starts `dotnet tool run runic-translations -- lsp`
// as it does for users. RUNIC_TRANSLATIONS_SERVER_ASSEMBLY=<dotnet-runic-translations.dll> tests a locally built
// server instead (runic.translations.serverAssembly).
// The workspace has a second folder, a copy of fixtures/sdk-app (a Runic SDK project), for the Runic SDK features: a
// stand-in for `dotnet runic dev` (a process whose command line names dotnet-runic.dll with dev) gives the session file
// a live process ID, and a stand-in `dotnet` (runic.dotnetPath of that folder) answers `dotnet runic doctor`.
const extension = fileURLToPath(new URL('../', import.meta.url));
const repository = resolve(extension, '..');
const temporary = await mkdtemp(join(tmpdir(), 'runic-vscode-host-'));
let passed = false;
const environment = { ...process.env, ELECTRON_ENABLE_LOGGING: '1', RUNIC_VSCODE_RESULT_FILE: join(temporary, 'result.json'), XDG_RUNTIME_DIR: join(temporary, 'runtime') };
const fakeDev = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)', '--', '/tools/dotnet-runic.dll', 'dev'], { stdio: 'ignore' });
await new Promise((accept, reject) => { fakeDev.once('spawn', accept); fakeDev.once('error', reject); });
environment.RUNIC_FAKE_DEV_PID = String(fakeDev.pid);
if (process.env.WAYLAND_DISPLAY && !process.env.WAYLAND_DISPLAY.startsWith('/') && process.env.XDG_RUNTIME_DIR) environment.WAYLAND_DISPLAY = join(process.env.XDG_RUNTIME_DIR, process.env.WAYLAND_DISPLAY);
await mkdir(environment.XDG_RUNTIME_DIR, { mode: 0o700 });
for (const name of ['VSCODE_IPC_HOOK_CLI', 'VSCODE_CLI', 'ELECTRON_RUN_AS_NODE']) delete environment[name];
try {
  const workspace = join(temporary, 'workspace');
  await cp(join(repository, 'fixtures/rmf2'), workspace, { recursive: true });
  const serverAssembly = process.env.RUNIC_TRANSLATIONS_SERVER_ASSEMBLY;
  if (serverAssembly) {
    await mkdir(join(workspace, '.vscode'));
    await writeFile(join(workspace, '.vscode/settings.json'), JSON.stringify({ 'runicTranslations.serverAssembly': resolve(serverAssembly) }));
  } else {
    // The repository's NuGet.config keeps packages in .cache/nuget; the temporary workspace has no NuGet.config.
    environment.NUGET_PACKAGES ??= join(repository, '.cache/nuget');
    await mkdir(join(workspace, '.config'));
    await cp(join(repository, '.config/dotnet-tools.json'), join(workspace, '.config/dotnet-tools.json'));
    await cp(join(repository, 'NuGet.config'), join(workspace, 'NuGet.config'));
    execFileSync('dotnet', ['tool', 'restore'], { cwd: workspace, env: environment, stdio: 'inherit' });
  }
  const app = join(temporary, 'app');
  await cp(join(repository, 'fixtures/sdk-app'), app, { recursive: true });
  const doctorEnvelope = { protocol: 'runic.commandline/1', command: 'doctor', success: true, exitCode: 0, payloadType: 'runic.application.tool.doctor/1', fault: null, diagnostics: [], payload: {
    project: join(app, 'App.csproj'), host: 'desktop', target: null, healthy: false, summary: { passed: 1, warnings: 1, failed: 1 }, hosts: ['desktop'], checks: [
      { id: 'dotnet-sdk', status: 'pass', message: '.NET SDK 10.0.403.', remediation: null },
      { id: 'lock-file', status: 'warn', message: 'The frontend has no lock file.', remediation: 'Run npm install in Frontend.' },
      { id: 'desktop-runtime', status: 'fail', message: 'WebKitGTK 4.1 is missing.', remediation: 'Install webkitgtk_4_1.' }] } };
  await writeFile(join(app, 'doctor.json'), JSON.stringify(doctorEnvelope));
  await writeFile(join(app, 'fake-dotnet.sh'), `#!/bin/sh\nif [ "$1" = runic ] && [ "$2" = doctor ]; then cat '${join(app, 'doctor.json')}'; exit 0; fi\necho "fake dotnet: $*" >&2; exit 1\n`);
  await chmod(join(app, 'fake-dotnet.sh'), 0o755);
  await mkdir(join(app, '.vscode'));
  await writeFile(join(app, '.vscode/settings.json'), JSON.stringify({ 'runic.dotnetPath': join(app, 'fake-dotnet.sh') }));
  const workspaceFile = join(temporary, 'runic.code-workspace');
  await writeFile(workspaceFile, JSON.stringify({ folders: [{ path: workspace }, { path: app }] }));
  const child = spawn(process.env.VSCODE_EXECUTABLE_PATH || 'code', ['--new-window', '--wait', '--no-sandbox', '--disable-gpu', '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes', '--user-data-dir', join(temporary, 'profile'), '--extensions-dir', join(temporary, 'extensions'), `--extensionDevelopmentPath=${extension}`, `--extensionTestsPath=${join(extension, 'test/host.cjs')}`, workspaceFile], { stdio: 'inherit', env: environment });
  const timeout = setTimeout(() => child.kill('SIGTERM'), 120000);
  const result = await new Promise((accept, reject) => { child.once('error', reject); child.once('exit', accept); });
  clearTimeout(timeout);
  const report = JSON.parse(await readFile(join(temporary, "result.json"), "utf8"));
  if (!report.passed) throw new Error(report.message || "Extension-host assertions did not finish.");
  console.log(report.message);
  if (result !== 0) throw new Error(`VS Code host tests exited with ${result}`);
  passed = true;
} finally {
  fakeDev.kill('SIGKILL');
  if (!passed) { await mkdir(join(extension, 'artifacts'), { recursive: true }); await cp(join(temporary, 'profile'), join(extension, 'artifacts/host-failure'), { recursive: true }).catch(() => {}); }
  await rm(temporary, { recursive: true, force: true });
}
