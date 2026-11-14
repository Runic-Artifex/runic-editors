import { mkdtemp, mkdir, cp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';
// Runs test/host.cjs in a real VS Code against the language server of the supported Translations release: the
// fixture workspace gets this repository's .config/dotnet-tools.json (dotnet-runic-translations at a pinned version)
// and `dotnet tool restore` installs it from NuGet, so the extension starts `dotnet tool run runic-translations -- lsp`
// as it does for users. RUNIC_TRANSLATIONS_SERVER_ASSEMBLY=<dotnet-runic-translations.dll> tests a locally built
// server instead (runicTranslations.serverAssembly).
const extension = fileURLToPath(new URL('../', import.meta.url));
const repository = resolve(extension, '..');
const temporary = await mkdtemp(join(tmpdir(), 'runic-vscode-host-'));
let passed = false;
const environment = { ...process.env, ELECTRON_ENABLE_LOGGING: '1', RUNIC_VSCODE_RESULT_FILE: join(temporary, 'result.json'), XDG_RUNTIME_DIR: join(temporary, 'runtime') };
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
  const child = spawn(process.env.VSCODE_EXECUTABLE_PATH || 'code', ['--new-window', '--wait', '--no-sandbox', '--disable-gpu', '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes', '--user-data-dir', join(temporary, 'profile'), '--extensions-dir', join(temporary, 'extensions'), `--extensionDevelopmentPath=${extension}`, `--extensionTestsPath=${join(extension, 'test/host.cjs')}`, workspace], { stdio: 'inherit', env: environment });
  const timeout = setTimeout(() => child.kill('SIGTERM'), 90000);
  const result = await new Promise((accept, reject) => { child.once('error', reject); child.once('exit', accept); });
  clearTimeout(timeout);
  const report = JSON.parse(await readFile(join(temporary, "result.json"), "utf8"));
  if (!report.passed) throw new Error(report.message || "Extension-host assertions did not finish.");
  console.log(report.message);
  if (result !== 0) throw new Error(`VS Code host tests exited with ${result}`);
  passed = true;
} finally {
  if (!passed) { await mkdir(join(extension, 'artifacts'), { recursive: true }); await cp(join(temporary, 'profile'), join(extension, 'artifacts/host-failure'), { recursive: true }).catch(() => {}); }
  await rm(temporary, { recursive: true, force: true });
}
