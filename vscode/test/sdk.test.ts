import { test, expect } from "bun:test";
import { spawn } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { existsSync, mkdtempSync, mkdirSync, renameSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { detectToolVersion, devArguments, forceStop, parseToolVersion, spawnDev, stopDevProcess, stopOnInputVariable, supportsDevSdk022, terminalText, type StoppableProcess } from "../src/dev-process.js";
import { DevSessionFollower, devSessionPath, devSessionSchema, devStateLabel, devStoppedStampPath, isDevCommandLine, isRunningDevProcess, needsStoppedStamp, parseDevSession, type DevSession, type DevSnapshot } from "../src/dev-session.js";
import { newestSourceMap, parseSourceMap, sourceMapTree, type SourceMap } from "../src/source-map.js";
import { detectWorkspace, isRunicSdkProject, projectRunicToolVersion, runicToolVersion } from "../src/workspace.js";
import { doctorArguments, doctorSummary, orderedChecks, parseDoctor } from "../src/doctor.js";
import { codeValue, diagnosticHelpUri, isRunicCode } from "../src/diagnostics.js";
import { formerSettingsInUse, settingValue, type SettingInspection } from "../src/identity.js";
import { parseRunProperties, splitArguments, templateWindowConfiguration, windowLaunchConfiguration } from "../src/window-debug.js";

const temporary = (prefix: string) => mkdtempSync(join(tmpdir(), `runic-vscode-${prefix}-`));
const session = (overrides: Partial<DevSession> = {}): DevSession => ({
  schema: devSessionSchema, state: "ready", processId: 41250, startedAt: "2026-11-14T18:53:16.2642872+00:00", project: "/app/App.csproj", configuration: "Debug",
  frontend: { developmentServer: "vite", url: "http://127.0.0.1:5173/", port: 5173, stateUrl: null, directory: "/app/Frontend", outputDirectory: "/app/Frontend/dist", generatedDirectory: "/app/Frontend/src/generated" },
  webRoot: "/app/bin/Debug/net10.0/www", pages: [{ document: "index.html", file: "/app/bin/Debug/net10.0/www/index.html", url: "http://127.0.0.1:5173/index.html" }],
  window: { owner: "dev", program: "App", processId: 41377, starts: 1, environment: { DOTNET_ENVIRONMENT: "Development" } }, debugEndpoints: [], ...overrides,
});

// ---- Workspace detection ---------------------------------------------------------------------------------------------

test("a Window project or a Runic SDK reference is a Runic SDK project; Translations references alone are not", () => {
  expect(isRunicSdkProject(`<Project><PropertyGroup><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup></Project>`)).toBe(true);
  expect(isRunicSdkProject(`<ItemGroup><PackageReference Include="Runic.Application.Views.Desktop" Version="0.20.0" /></ItemGroup>`)).toBe(true);
  expect(isRunicSdkProject(`<ProjectReference Include="../../packages/dotnet/Runic.Desktop/Runic.Desktop.csproj" />`)).toBe(true);
  expect(isRunicSdkProject(`<PackageReference Version="0.20.0" Include="Runic.Platform.MacOS" />`)).toBe(true);
  expect(isRunicSdkProject(`<PackageReference Include="Runic.Translations" Version="0.11.0" /><PackageReference Include="Runic.Translations.Generator" Version="0.11.0" />`)).toBe(false);
  expect(isRunicSdkProject(`<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.14" />`)).toBe(false);
  expect(isRunicSdkProject(`<RunicApplicationFrontendWindowProject>false</RunicApplicationFrontendWindowProject>`)).toBe(false);
});

test("workspace detection tells an SDK workspace from a Translations-only one and skips build output", () => {
  const root = temporary("detect");
  try {
    // Translations-only: a runic.json project and a .NET project that uses only the Translations packages.
    mkdirSync(join(root, "translations-only", "src"), { recursive: true });
    writeFileSync(join(root, "translations-only", "runic.json"), "{}");
    writeFileSync(join(root, "translations-only", "src", "Library.csproj"), `<Project><ItemGroup><PackageReference Include="Runic.Translations" Version="0.11.0" /></ItemGroup></Project>`);
    expect(detectWorkspace(join(root, "translations-only"))).toEqual({ sdkProjects: [], translationsProjects: [join(root, "translations-only", "runic.json")] });
    // SDK: a template app (Window project with translations/runic.json); copies in bin/obj/node_modules are ignored.
    const app = join(root, "app");
    for (const directory of ["translations", "obj/Debug/net10.0", "bin/Debug", "Frontend/node_modules/pkg"]) mkdirSync(join(app, directory), { recursive: true });
    const window = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup></Project>`;
    writeFileSync(join(app, "App.csproj"), window);
    writeFileSync(join(app, "obj", "Debug", "net10.0", "Copy.csproj"), window);
    writeFileSync(join(app, "bin", "Debug", "Copy.csproj"), window);
    writeFileSync(join(app, "Frontend", "node_modules", "pkg", "runic.json"), "{}");
    writeFileSync(join(app, "translations", "runic.json"), "{}");
    expect(detectWorkspace(app)).toEqual({ sdkProjects: [join(app, "App.csproj")], translationsProjects: [join(app, "translations", "runic.json")] });
    // A workspace of both finds both; a project below the search depth is not found.
    expect(detectWorkspace(root).sdkProjects).toEqual([join(app, "App.csproj")]);
    mkdirSync(join(root, "a", "b", "c", "d", "e"), { recursive: true });
    writeFileSync(join(root, "a", "b", "c", "d", "e", "Deep.csproj"), window);
    expect(detectWorkspace(root).sdkProjects).toEqual([join(app, "App.csproj")]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the project's dotnet-runic version comes from the nearest tool manifest", () => {
  const root = temporary("tool");
  try {
    expect(runicToolVersion(JSON.stringify({ tools: { "dotnet-runic": { version: "0.20.0", commands: ["dotnet-runic"] } } }))).toBe("0.20.0");
    expect(runicToolVersion("not json")).toBeUndefined();
    mkdirSync(join(root, ".config")); mkdirSync(join(root, "src", "App"), { recursive: true });
    writeFileSync(join(root, ".config", "dotnet-tools.json"), JSON.stringify({ isRoot: true, tools: { "dotnet-runic": { version: "0.20.0", commands: ["dotnet-runic"] } } }));
    expect(projectRunicToolVersion(join(root, "src", "App", "App.csproj"))).toBe("0.20.0");
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// ---- Session file follower -------------------------------------------------------------------------------------------

test("the session file parses only for its schema", () => {
  expect(parseDevSession(JSON.stringify(session()))?.frontend?.url).toBe("http://127.0.0.1:5173/");
  expect(parseDevSession(JSON.stringify({ ...session(), schema: "runic.application.tool.dev-session/2" }))).toBeUndefined();
  expect(parseDevSession(JSON.stringify({ ...session(), state: "paused" }))).toBeUndefined();
  expect(parseDevSession("{")).toBeUndefined();
  // Later versions only add properties.
  expect(parseDevSession(JSON.stringify({ ...session(), added: { anything: true } }))?.state).toBe("ready");
  expect(devSessionPath("/app/App.csproj")).toBe(join("/app", "obj", "runic", "dev-session.json"));
});

test("dotnet runic dev's command line is recognized as the DLL, the apphost or the tool name", () => {
  expect(isDevCommandLine(["/usr/share/dotnet/dotnet", "exec", "--runtimeconfig", "x.json", "/home/u/.nuget/packages/dotnet-runic/0.20.0/tools/net10.0/any/dotnet-runic.dll", "dev", "--no-window"])).toBe(true);
  expect(isDevCommandLine(["/home/u/.dotnet/tools/dotnet-runic", "dev"])).toBe(true);
  expect(isDevCommandLine(["C:\\Users\\u\\.nuget\\packages\\dotnet-runic\\0.20.0\\tools\\net10.0\\any\\dotnet-runic.exe", "dev"])).toBe(true);
  expect(isDevCommandLine(["/usr/share/dotnet/dotnet", "exec", "/x/dotnet-runic.dll", "doctor"])).toBe(false);
  expect(isDevCommandLine(["node", "vite", "dev"])).toBe(false);
  expect(isDevCommandLine(["/app/bin/Debug/net10.0/App"])).toBe(false);
});

/** A follower over an in-memory file whose liveness answer the test controls. */
function memoryFollower(initial?: string) {
  const files = new Map<string, string>();
  const alive = new Set<number>();
  const follower = new DevSessionFollower("/app/App.csproj", { read: async path => files.get(path), isRunning: async pid => alive.has(pid) });
  if (initial !== undefined) files.set(follower.path, initial);
  return { files, alive, follower, write: (value: DevSession) => files.set(follower.path, JSON.stringify(value)) };
}

test("a ready session whose dotnet runic dev process is dead reads as stopped", async () => {
  const { alive, follower, write } = memoryFollower();
  const seen: DevSnapshot[] = [];
  follower.onDidChange(snapshot => seen.push(snapshot));
  expect((await follower.refresh()).state).toBe("none");
  write(session({ state: "starting" }));
  alive.add(41250);
  expect((await follower.refresh()).state).toBe("starting");
  write(session());
  expect((await follower.refresh()).state).toBe("ready");
  // dev was killed: the file keeps "ready", but its process is gone.
  alive.delete(41250);
  const killed = await follower.refresh();
  expect(killed).toMatchObject({ state: "stopped", exited: true });
  expect(devStateLabel(killed)).toBe("stopped (process 41250 exited)");
  // An unchanged file and liveness notify once.
  await follower.refresh();
  expect(seen.map(snapshot => snapshot.state)).toEqual(["starting", "ready", "stopped"]);
  // A file that says stopped needs no live process.
  write(session({ state: "stopped" }));
  expect(await follower.refresh()).toMatchObject({ state: "stopped" });
  expect(follower.snapshot.exited).toBeUndefined();
});

test("the dev-stopped stamp is needed only for a starting or ready session whose dev exited and not stamped since", () => {
  const written = Date.parse("2026-11-17T12:00:00Z");
  const exited: DevSnapshot = { state: "stopped", session: session(), exited: true };
  expect(needsStoppedStamp(exited, undefined, written)).toBe(true);
  expect(needsStoppedStamp(exited, written - 3_600_000, written)).toBe(true);
  expect(needsStoppedStamp(exited, written, undefined)).toBe(true);
  expect(needsStoppedStamp({ ...exited, session: session({ state: "starting" }) }, undefined, written)).toBe(true);
  expect(needsStoppedStamp(exited, written + 1000, written)).toBe(false);
  expect(needsStoppedStamp(exited, written, written)).toBe(false);
  expect(needsStoppedStamp({ state: "ready", session: session() }, undefined, written)).toBe(false);
  expect(needsStoppedStamp({ state: "stopped", session: session({ state: "stopped" }) }, undefined, written)).toBe(false);
  expect(needsStoppedStamp({ state: "none" })).toBe(false);
  expect(devStoppedStampPath(devSessionPath("/app/App.csproj"))).toBe(join("/app", "obj", "runic", "dev-stopped"));
});

/** A follower whose file times, touches and liveness the test controls. */
function stampFollower(options: { readOnly?: boolean; times?: Map<string, number> } = {}) {
  const files = new Map<string, string>();
  const alive = new Set<number>();
  const times = options.times ?? new Map<string, number>();
  const touches: string[] = [];
  const logs: string[] = [];
  const follower = new DevSessionFollower("/app/App.csproj", {
    read: async path => files.get(path), isRunning: async pid => alive.has(pid), modified: async path => times.get(path),
    touch: async path => { if (options.readOnly) throw new Error("EACCES: permission denied"); touches.push(path); times.set(path, Date.now()); },
  }, undefined, message => logs.push(message));
  return { files, alive, times, touches, logs, follower, write: (value: DevSession) => files.set(follower.path, JSON.stringify(value)) };
}

test("the follower touches dev-stopped once per dead dev, and never for a live or stopped one", async () => {
  const { alive, touches, logs, follower, write } = stampFollower();
  alive.add(41250);
  write(session({ state: "starting" }));
  await follower.refresh();
  write(session());
  await follower.refresh();
  expect(touches).toEqual([]);
  // Killed: the stamp beside this session file, once, however often the follower polls (also concurrently).
  alive.delete(41250);
  await Promise.all([follower.refresh(), follower.refresh()]);
  await follower.refresh();
  expect(touches).toEqual([follower.stoppedStampPath]);
  expect(follower.stoppedStampPath).toBe(join("/app", "obj", "runic", "dev-stopped"));
  expect(logs).toHaveLength(1);
  expect(logs[0]).toContain("process 41250");
  // A file that says stopped: dev touched the stamp itself.
  write(session({ state: "stopped" }));
  await follower.refresh();
  expect(touches).toHaveLength(1);
  // Another dev that is killed is stamped again.
  write(session({ processId: 41300 }));
  await follower.refresh();
  expect(touches).toHaveLength(2);
});

test("the follower leaves a stamp newer than the session file alone and logs a read-only obj/runic", async () => {
  const times = new Map<string, number>();
  const restarted = stampFollower({ times });
  times.set(restarted.follower.path, Date.now() - 300_000);
  times.set(restarted.follower.stoppedStampPath, Date.now() - 60_000);
  restarted.write(session());
  expect(await restarted.follower.refresh()).toMatchObject({ state: "stopped", exited: true });
  expect(restarted.touches).toEqual([]);

  const denied = stampFollower({ readOnly: true });
  denied.write(session());
  expect(await denied.follower.refresh()).toMatchObject({ state: "stopped", exited: true });
  await denied.follower.refresh();
  expect(denied.logs).toHaveLength(1);
  expect(denied.logs[0]).toContain("could not be touched");

  // A source without file times (memoryFollower) only reads.
  const { follower } = memoryFollower(JSON.stringify(session()));
  expect(await follower.refresh()).toMatchObject({ state: "stopped", exited: true });
});

test("an unreadable or foreign session file keeps the last snapshot; a deleted one is none", async () => {
  const { files, alive, follower, write } = memoryFollower();
  alive.add(41250);
  write(session());
  expect((await follower.refresh()).state).toBe("ready");
  files.set(follower.path, "{\"schema\":");
  expect((await follower.refresh()).state).toBe("ready");
  files.delete(follower.path);
  expect((await follower.refresh()).state).toBe("none");
});

test("the follower reads a session file that dev replaces atomically, and a real dead process as stopped", async () => {
  const root = temporary("session");
  // A stand-in for dotnet runic dev: its command line names dotnet-runic.dll with dev, as the real tool's does.
  const fake = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)", "--", "/tools/dotnet-runic.dll", "dev"], { stdio: "ignore" });
  try {
    await new Promise(resolveSpawn => fake.once("spawn", resolveSpawn));
    const project = join(root, "App.csproj");
    writeFileSync(project, "<Project />");
    const logs: string[] = [];
    let follower = new DevSessionFollower(project, undefined, undefined, message => logs.push(message));
    mkdirSync(join(root, "obj", "runic"), { recursive: true });
    const replace = (value: DevSession) => {
      // dev writes a temporary file and renames it over the session file, so a reader never sees half of one.
      const staged = join(root, "obj", "runic", `dev-session.json.${Math.random().toString(36).slice(2)}.tmp`);
      writeFileSync(staged, JSON.stringify(value));
      renameSync(staged, follower.path);
    };
    replace(session({ state: "starting", processId: fake.pid!, project }));
    expect((await follower.refresh()).state).toBe(process.platform === "win32" ? "stopped" : "starting");
    if (process.platform === "win32") return;
    replace(session({ processId: fake.pid!, project, window: { owner: "ide", program: "App", processId: null, starts: 0, environment: { DOTNET_ENVIRONMENT: "Development" } } }));
    const ready = await follower.refresh();
    expect(ready.state).toBe("ready");
    expect(devStateLabel(ready)).toBe("ready (Window in the IDE)");
    // A process ID that is not dotnet runic dev (this test runner) does not count, as after a reused ID.
    replace(session({ processId: process.pid, project }));
    expect(await follower.refresh()).toMatchObject({ state: "stopped", exited: true });
    // That dev is gone too: dev-stopped is touched. Take it away to see a killed dev's own touch.
    expect(existsSync(follower.stoppedStampPath)).toBe(true);
    rmSync(follower.stoppedStampPath);
    replace(session({ processId: fake.pid!, project }));
    follower = new DevSessionFollower(project, undefined, undefined, message => logs.push(message));
    expect((await follower.refresh()).state).toBe("ready");
    expect(existsSync(follower.stoppedStampPath)).toBe(false);
    fake.kill("SIGKILL");
    await new Promise(resolveExit => fake.once("exit", resolveExit));
    expect(await isRunningDevProcess(fake.pid!)).toBe(false);
    const before = Date.now() - 2000;
    expect(await follower.refresh()).toMatchObject({ state: "stopped", exited: true });
    const stamp = statSync(follower.stoppedStampPath);
    expect(stamp.size).toBe(0);
    expect(stamp.mtimeMs).toBeGreaterThanOrEqual(before);
    expect(stamp.mtimeMs).toBeGreaterThanOrEqual(statSync(follower.path).mtimeMs);
    expect(logs.at(-1)).toContain("touched");
    // A missing obj/runic is logged, not thrown.
    const other = join(root, "Other", "Other.csproj");
    const missing = new DevSessionFollower(other, { read: async () => JSON.stringify(session({ processId: fake.pid!, project: other })), isRunning: async () => false,
      modified: async () => undefined, touch: path => writeFile(path, "") }, undefined, message => logs.push(message));
    expect(await missing.refresh()).toMatchObject({ state: "stopped", exited: true });
    expect(logs.at(-1)).toContain("could not be touched");
  } finally { fake.kill("SIGKILL"); rmSync(root, { recursive: true, force: true }); }
});

// ---- Development source map -----------------------------------------------------------------------------------------

const sourceMap: SourceMap = {
  format: "runic-bridge-sources/1",
  viewModels: [
    { id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:EE06", pageKinds: ["counter"], symbol: "Demo.CounterViewModel", location: { file: "/src/Demo/CounterViewModel.cs", line: 6, column: 29 } },
    { id: "Demo.AboutViewModel", name: "About", symbol: "Demo.AboutViewModel" },
  ],
  members: [
    { id: "Demo.CounterViewModel/command/increment", viewModel: "Demo.CounterViewModel", member: "increment", kind: "command", symbol: "Demo.CounterViewModel.Increment()", property: "Demo.CounterViewModel.IncrementCommand", location: { file: "/src/Demo/CounterViewModel.cs", line: 18, column: 18 } },
    { id: "Demo.CounterViewModel/state/count", viewModel: "Demo.CounterViewModel", member: "count", kind: "state", symbol: "Demo.CounterViewModel.Count", location: { file: "/src/Demo/CounterViewModel.cs", line: 10, column: 9 } },
    { id: "Demo.CounterViewModel/commandState/canIncrement", viewModel: "Demo.CounterViewModel", member: "canIncrement", kind: "commandState" },
    { id: "Demo.Orphan/state/x", viewModel: "Demo.Orphan", member: "x", kind: "state" },
  ],
};

test("a source map with the current format ID is accepted", () => {
  expect(parseSourceMap(JSON.stringify({ ...sourceMap, format: "runic.views.bridge-sources/1" })).format).toBe("runic.views.bridge-sources/1");
});

test("a source map with the 0.20 format ID is accepted", () => {
  expect(parseSourceMap(JSON.stringify(sourceMap)).format).toBe("runic-bridge-sources/1");
});

test("a source map with an unknown format version is rejected", () => {
  expect(() => parseSourceMap(JSON.stringify({ ...sourceMap, format: "runic.views.bridge-sources/2" }))).toThrow("Unsupported");
  expect(() => parseSourceMap(JSON.stringify({ ...sourceMap, format: "runic-bridge-sources/2" }))).toThrow("Unsupported");
});

test("the source map tree lists ViewModels with their members in client order and their C# locations", () => {
  const tree = sourceMapTree(parseSourceMap(JSON.stringify(sourceMap)));
  expect(tree.map(node => [node.label, node.description])).toEqual([["About", undefined], ["Counter", "route counter"], ["Demo.Orphan", undefined]]);
  const counter = tree[1];
  expect(counter.location).toEqual({ file: "/src/Demo/CounterViewModel.cs", line: 6, column: 29 });
  expect(counter.children.map(child => [child.label, child.description])).toEqual([["count", "state"], ["canIncrement", "command state"], ["increment", "command"]]);
  const increment = counter.children[2];
  expect(increment.location).toEqual({ file: "/src/Demo/CounterViewModel.cs", line: 18, column: 18 });
  expect(increment.tooltip).toContain("via Demo.CounterViewModel.IncrementCommand");
  expect(counter.children[1].tooltip).toContain("no source location");
});

test("the newest source map under obj/<Configuration>/<TargetFramework> is used", () => {
  const root = temporary("sources");
  try {
    const project = join(root, "App.csproj");
    expect(newestSourceMap(project)).toBeUndefined();
    const debug = join(root, "obj", "Debug", "net10.0", "runic-bridge");
    const release = join(root, "obj", "Release", "net10.0", "runic-bridge");
    mkdirSync(debug, { recursive: true }); mkdirSync(release, { recursive: true });
    writeFileSync(join(debug, "sources.json"), JSON.stringify(sourceMap));
    writeFileSync(join(release, "sources.json"), JSON.stringify(sourceMap));
    utimesSync(join(release, "sources.json"), new Date(2026, 0, 1), new Date(2026, 0, 1));
    expect(newestSourceMap(project)).toBe(join(debug, "sources.json"));
    utimesSync(join(release, "sources.json"), new Date(2030, 0, 1), new Date(2030, 0, 1));
    expect(newestSourceMap(project)).toBe(join(release, "sources.json"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// ---- Doctor, diagnostics, settings and the Window launch -------------------------------------------------------------

const doctorPayload = { project: "/app/App.csproj", host: "desktop", target: null, healthy: false, summary: { passed: 1, warnings: 1, failed: 1 }, checks: [
  { id: "dotnet-sdk", status: "pass", message: "SDK 10.0.403.", remediation: null },
  { id: "lock-file", status: "warn", message: "No lock file.", remediation: "Run npm install." },
  { id: "views-window", status: "fail", message: "Not a Window project.", remediation: "Set RunicApplicationFrontendWindowProject." }], hosts: ["desktop"] };

test("doctor reads a successful envelope, a failed envelope's fault data, and reports a fault without a report", () => {
  expect(doctorArguments("/app/App.csproj")).toEqual(["runic", "doctor", "--project", "/app/App.csproj", "--format", "json", "--fail-on", "never"]);
  const success = parseDoctor(JSON.stringify({ protocol: "runic.commandline/1", success: true, exitCode: 0, payloadType: "runic.application.tool.doctor/1", payload: doctorPayload, fault: null, diagnostics: [] }));
  expect(orderedChecks(success).map(check => check.id)).toEqual(["views-window", "lock-file", "dotnet-sdk"]);
  expect(doctorSummary(success)).toBe("1 failed, 1 warning, 1 passed");
  const failed = parseDoctor(JSON.stringify({ protocol: "runic.commandline/1", success: false, exitCode: 1, payloadType: null, payload: null,
    fault: { code: "RAPPCLI1009", message: "Doctor checks failed.", data: { type: "runic.application.tool.doctor/1", payload: doctorPayload } } }));
  expect(failed.healthy).toBe(false);
  expect(() => parseDoctor(JSON.stringify({ success: false, fault: { code: "RAPPCLI1003", message: "No project file was found." } }))).toThrow("RAPPCLI1003: No project file was found.");
  expect(() => parseDoctor("Could not execute because the specified command or file was not found.")).toThrow("did not write a JSON envelope");
});

test("Runic codes link to the diagnostics catalog at the tool's release tag", () => {
  for (const code of ["RUNICBRIDGE013", "RAPPDEV1007", "RAPPCLI1009", "RAPPPKG1001", "RCLI8101"]) expect(isRunicCode(code)).toBe(true);
  for (const code of ["CS1002", "RTR0090", "RUNICBRIDGE", "MSB3073"]) expect(isRunicCode(code)).toBe(false);
  expect(diagnosticHelpUri("RUNICBRIDGE013", "0.20.0")).toBe("https://github.com/Runic-Artifex/runic-sdk/blob/v0.20.0/docs/diagnostics.md#runicbridge013");
  expect(diagnosticHelpUri("RAPPDEV1007")).toBe("https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#rappdev1007");
  expect(diagnosticHelpUri("RAPPDEV1007", "../../evil")).toContain("/blob/main/");
  expect(codeValue({ value: "RAPPDEV1007" })).toBe("RAPPDEV1007");
  expect(codeValue(1007)).toBe("1007");
});

test("former runicTranslations settings apply while the new settings are not set", () => {
  const configuration = (values: Record<string, SettingInspection<string>>) => ({
    inspect: (key: string) => values[key],
    get: (key: string) => { const value = values[key]; return value?.workspaceFolderValue ?? value?.workspaceValue ?? value?.globalValue ?? value?.defaultValue; },
  });
  expect(settingValue("runic.dotnetPath", "dotnet", configuration({ "runic.dotnetPath": { defaultValue: "dotnet" } }))).toBe("dotnet");
  expect(settingValue("runic.dotnetPath", "dotnet", configuration({ "runic.dotnetPath": { defaultValue: "dotnet" }, "runicTranslations.dotnetPath": { globalValue: "/opt/dotnet/dotnet" } }))).toBe("/opt/dotnet/dotnet");
  expect(settingValue("runic.dotnetPath", "dotnet", configuration({ "runic.dotnetPath": { defaultValue: "dotnet", workspaceValue: "/new/dotnet" }, "runicTranslations.dotnetPath": { globalValue: "/old/dotnet" } }))).toBe("/new/dotnet");
  expect(settingValue("runic.translations.serverAssembly", "", configuration({ "runicTranslations.serverAssembly": { workspaceFolderValue: "server.dll" } }))).toBe("server.dll");
  expect(formerSettingsInUse(key => key === "runicTranslations.trace.server" ? { globalValue: "verbose" } : undefined)).toEqual(["runicTranslations.trace.server"]);
});

test("the Window launch configuration runs what dotnet run starts, with the session's environment", () => {
  const properties = parseRunProperties(JSON.stringify({ Properties: { RunCommand: "/app/bin/Debug/net10.0/App", RunArguments: "", RunWorkingDirectory: "", TargetPath: "/app/bin/Debug/net10.0/App.dll" } }));
  const ide = session({ window: { owner: "ide", program: "App", processId: null, starts: 0, environment: { DOTNET_ENVIRONMENT: "Development", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/app/Frontend/dist" } } });
  expect(windowLaunchConfiguration("/app/App.csproj", properties, ide, { EXTRA: "1" })).toEqual({
    type: "coreclr", request: "launch", name: "Runic: Window", program: "/app/bin/Debug/net10.0/App", args: [], cwd: "/app",
    env: { DOTNET_ENVIRONMENT: "Development", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/app/Frontend/dist", EXTRA: "1" }, console: "internalConsole" });
  // Without an application host, dotnet runs the DLL: the C# debugger takes the DLL as the program.
  const viaDotnet = windowLaunchConfiguration("/app/App.csproj", { RunCommand: "dotnet", RunArguments: "exec \"/app/bin/Debug/net10.0/App.dll\" --flag", TargetPath: "/app/bin/Debug/net10.0/App.dll" }, undefined);
  expect([viaDotnet.program, viaDotnet.args, (viaDotnet.env as Record<string, string>).DOTNET_ENVIRONMENT]).toEqual(["/app/bin/Debug/net10.0/App.dll", ["--flag"], "Development"]);
  expect(() => windowLaunchConfiguration("/app/Lib.csproj", { RunCommand: "" }, undefined)).toThrow("no RunCommand");
  expect(splitArguments(`a "b c" "d \\"e\\""`)).toEqual(["a", "b c", 'd "e"']);
});

test("the template's F5 configuration is found by its preLaunchTask, else by its name", () => {
  expect(templateWindowConfiguration([{ name: "Attach", request: "attach", preLaunchTask: "runic: dev" }, { name: "Window", type: "coreclr", request: "launch", preLaunchTask: "runic: window" }])).toBe("Window");
  expect(templateWindowConfiguration([{ name: "My Window", request: "launch", preLaunchTask: "runic: window" }])).toBe("My Window");
  expect(templateWindowConfiguration([{ name: "Window", request: "launch" }])).toBe("Window");
  expect(templateWindowConfiguration([{ name: "Other" }])).toBeUndefined();
  expect(templateWindowConfiguration(undefined)).toBeUndefined();
});

test("one notice offers to uninstall the former identities that are still installed", async () => {
  const { extensionId, formerExtensions, formerExtensionsNotice } = await import("../src/identity.js");
  expect(extensionId).toBe("runic-artifex.runic-devkit");
  expect(formerExtensions.map(extension => extension.id)).toEqual(["runic-artifex.runic", "runic-artifex.runic-translations"]);
  expect(formerExtensionsNotice(() => false)).toBeUndefined();
  // The new identity itself is never reported.
  expect(formerExtensionsNotice(id => id === extensionId)).toBeUndefined();
  const runic = formerExtensionsNotice(id => id === "runic-artifex.runic");
  expect(runic).toEqual({ ids: ["runic-artifex.runic"], action: "Uninstall Runic",
    message: "Runic DevKit replaces Runic (runic-artifex.runic), which is still installed. Both register the same Runic commands and language server; uninstall it and reload the window." });
  const both = formerExtensionsNotice(() => true)!;
  expect(both.ids).toEqual(["runic-artifex.runic", "runic-artifex.runic-translations"]);
  expect(both.action).toBe("Uninstall both");
  expect(both.message).toContain("Runic (runic-artifex.runic) and Runic MF2 Translations (runic-artifex.runic-translations), which are still installed");
});

test("the manifest keeps the Translations commands and settings under the new identity, and gates the SDK views", async () => {
  const { readFileSync } = await import("node:fs");
  const { formerSettings, translationsCommands, commandId, formerCommandId } = await import("../src/identity.js");
  const manifest = JSON.parse(readFileSync(join(import.meta.dir, "..", "package.json"), "utf8"));
  expect(`${manifest.publisher}.${manifest.name}`).toBe("runic-artifex.runic-devkit");
  const commands = new Set((manifest.contributes.commands as { command: string }[]).map(command => command.command));
  for (const name of translationsCommands) {
    expect(commands.has(commandId(name))).toBe(true);
    // A keybinding of a former command ID activates the extension.
    expect(manifest.activationEvents).toContain(`onCommand:${formerCommandId(name)}`);
  }
  const properties = manifest.contributes.configuration.properties;
  for (const [setting, former] of Object.entries(formerSettings)) {
    expect(properties[setting]).toBeDefined();
    expect(properties[former].markdownDeprecationMessage).toContain(setting);
  }
  expect(manifest.contributes.views.runic.map((view: { id: string; when: string }) => [view.id, view.when])).toEqual([
    ["runic.devSession", "runic.sdkProject"], ["runic.viewModels", "runic.sdkProject"], ["runic.doctor", "runic.sdkProject"], ["runic.translationsStatus", "runic.translationsProject"]]);
  // The template's background patterns (runic-sdk tools/Runic.Application.Templates/content/runic-app/.vscode/tasks.json).
  const matchers = Object.fromEntries(manifest.contributes.problemMatchers.map((matcher: { name: string; background: unknown }) => [matcher.name, matcher.background]));
  expect(matchers["runic-dev"]).toEqual({ activeOnStart: true, beginsPattern: "^\\[dev\\] (Evaluating|Window exited|Reloading)", endsPattern: "^\\[dev\\] Ready$" });
  expect(matchers["runic-dev-no-window"]).toEqual({ activeOnStart: true, beginsPattern: "^\\[dev\\] Evaluating", endsPattern: "^\\[dev\\] Ready$" });
  const pattern = new RegExp(manifest.contributes.problemPatterns[0].regexp);
  expect(pattern.exec("/app/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.")?.slice(1, 7)).toEqual(["/app/App.csproj", undefined, undefined, "error", "RAPPDEV1007", "The vite development server exited unexpectedly with code 1."]);
  expect(pattern.exec("CounterViewModel.cs(18,5): error CS1002: ; expected [/app/App.csproj]")?.slice(1, 7)).toEqual(["CounterViewModel.cs", "18", "5", "error", "CS1002", "; expected"]);
});

// ---- dotnet-runic 0.22: --debug-endpoint and the stop request on dev's input ---------------------------------------

test("--debug-endpoint and the stop request need dotnet-runic 0.22", () => {
  for (const version of ["0.22.0", "0.22.0-rc.1", "0.23.1", "1.0.0-rc.2", "1.0.0"]) expect(supportsDevSdk022(version)).toBe(true);
  for (const version of ["0.21.0", "0.21.9", "0.9.0", "0.2.30", undefined, "", "main"]) expect(supportsDevSdk022(version)).toBe(false);
  expect(parseToolVersion("0.22.0+abc123\n")).toBe("0.22.0");
  expect(parseToolVersion("dotnet-runic 0.21.0\r\n")).toBe("0.21.0");
  expect(parseToolVersion("Could not execute because the specified command or file was not found.")).toBeUndefined();
  expect(devArguments("/a/App.csproj", false, true)).toEqual(["runic", "dev", "--debug-endpoint", "--project", "/a/App.csproj"]);
  expect(devArguments("/a/App.csproj", false, false)).toEqual(["runic", "dev", "--project", "/a/App.csproj"]);
  expect(devArguments("/a/App.csproj", true, true)).toEqual(["runic", "dev", "--no-window", "--project", "/a/App.csproj"]);
  expect(terminalText("a\nb\r\nc")).toBe("a\r\nb\r\nc");
});

test("the tool version comes from the manifest, else from dotnet runic --version", async () => {
  expect(await detectToolVersion("/nonexistent/dotnet", tmpdir(), "0.22.0")).toBe("0.22.0");
  expect(await detectToolVersion(join(tmpdir(), "no-such-dotnet"), tmpdir(), undefined)).toBeUndefined();
  if (process.platform === "win32") return;
  const directory = temporary("version");
  try {
    const fake = join(directory, "dotnet");
    writeFileSync(fake, "#!/bin/sh\necho \"0.22.1+deadbeef\"\n", { mode: 0o755 });
    expect(await detectToolVersion(fake, directory, undefined)).toBe("0.22.1");
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

/** A stand-in for dev with a standard input: it exits when `onInput` says so. */
class FakeDev extends EventEmitter implements StoppableProcess {
  pid = 41250; exitCode: number | null = null; signalCode: NodeJS.Signals | null = null;
  readonly input: string[] = [];
  readonly stdin = new PassThrough();
  forced: number[] = [];
  constructor(onInput: (fake: FakeDev, text: string, ended: boolean) => void) {
    super();
    this.stdin.setEncoding("utf8");
    this.stdin.on("data", (text: string) => { this.input.push(text); onInput(this, text, false); });
    this.stdin.on("end", () => onInput(this, "", true));
  }
  exit(code: number) { if (this.exitCode !== null) return; this.exitCode = code; this.emit("exit", code, null); }
}

test("a 0.22 dev stops on a stop line and the end of its input", async () => {
  const dev = new FakeDev((fake, text, ended) => { if (ended || text.trim() === "stop") fake.exit(130); });
  const how = await stopDevProcess(dev, { stopOnInput: true, inputTimeout: 2000, force: (_, attempt) => dev.forced.push(attempt) });
  expect(how).toBe("input");
  expect(dev.input.join("")).toBe("stop\n");
  expect(dev.forced).toEqual([]);
  expect(dev.stdin.writableEnded).toBe(true);
  expect(await stopDevProcess(dev, { stopOnInput: true, force: () => { throw new Error("no force for an exited dev"); } })).toBe("exited");
});

test("a dev that does not stop on its input is ended by force after the bounded wait", async () => {
  const dev = new FakeDev(() => { /* an older dev never reads its input */ });
  const started = Date.now();
  const how = await stopDevProcess(dev, { stopOnInput: true, inputTimeout: 200, forceTimeout: 1000, force: (fake, attempt) => { dev.forced.push(attempt); (fake as FakeDev).exit(1); } });
  expect(how).toBe("forced");
  expect(Date.now() - started).toBeGreaterThanOrEqual(190);
  expect(dev.forced).toEqual([1]);
});

test("without the stop request dev is ended by force at once, and a second force follows a hang", async () => {
  const dev = new FakeDev(() => { throw new Error("no input without the variable"); });
  const how = await stopDevProcess(dev, { stopOnInput: false, forceTimeout: 100, force: (fake, attempt) => { dev.forced.push(attempt); if (attempt === 2) (fake as FakeDev).exit(137); } });
  expect(how).toBe("forced");
  expect(dev.forced).toEqual([1, 2]);
  expect(dev.input).toEqual([]);
});

test("spawnDev passes RUNIC_DEV_STOP_ON_INPUT only when asked, and a real child stops on its input", async () => {
  if (process.platform === "win32") return;
  const run = async (stopOnInput: boolean) => {
    const child = spawnDev("/bin/sh", ["-c", "echo \"variable=${RUNIC_DEV_STOP_ON_INPUT:-unset}\"; read line; echo \"input=$line\"; exit 130"], tmpdir(), stopOnInput, { ...process.env, [stopOnInputVariable]: "1" });
    const closed = new Promise(resolveClose => child.once("close", resolveClose));
    let output = "";
    child.stdout!.setEncoding("utf8"); child.stdout!.on("data", (text: string) => { output += text; });
    await new Promise(resolveOutput => child.stdout!.once("data", resolveOutput));
    const how = await stopDevProcess(child, { stopOnInput, inputTimeout: 5000, forceTimeout: 1000, force: forceStop });
    await closed;
    return { how, output, code: child.exitCode };
  };
  const asked = await run(true);
  expect(asked.how).toBe("input");
  expect(asked.code).toBe(130);
  expect(asked.output).toContain("variable=1");
  expect(asked.output).toContain("input=stop");
  const older = await run(false);
  expect(older.output).toContain("variable=unset");
  expect(older.how).toBe("forced");
}, 30000);
