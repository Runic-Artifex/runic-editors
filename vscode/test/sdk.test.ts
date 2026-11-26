import { test, expect } from "bun:test";
import { spawn } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { existsSync, mkdtempSync, mkdirSync, renameSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { detectToolVersion, devArguments, forceStop, onDevEnd, parseToolVersion, spawnDev, stopDevProcess, stopOnInputVariable, supportsDevSdk022, terminalText, type StoppableProcess } from "../src/dev-process.js";
import { DevSessionFollower, devSessionPath, devSessionSchema, raxDevSessionSchema, devStateLabel, devStoppedStampPath, isDevCommandLine, isRunningDevProcess, needsStoppedStamp, parseDevSession, type DevSession, type DevSnapshot } from "../src/dev-session.js";
import { DevProblems, LineSplitter, beginsNoWindowPattern, beginsPattern, diagnosticPattern, parseDevProblem, readyPattern } from "../src/dev-problems.js";
import { newestSourceMap, parseSourceMap, sourceMapTree, type SourceMap } from "../src/source-map.js";
import { coreTool, detectWorkspace, formerCoreTool, formerToolIds, isRunicSdkProject, manifestTool, projectCoreTool, projectRunicToolVersion, runicToolVersion } from "../src/workspace.js";
import { migrateArguments, migrationOffer, migrationProbeDirectory, migrationSummary, raxVersionArguments } from "../src/migration.js";
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
    expect(detectWorkspace(join(root, "translations-only"))).toEqual({ sdkProjects: [], windowProjects: [], translationsProjects: [join(root, "translations-only", "runic.json")] });
    // SDK: a template app (Window project with translations/runic.json); copies in bin/obj/node_modules are ignored.
    const app = join(root, "app");
    for (const directory of ["translations", "obj/Debug/net10.0", "bin/Debug", "Frontend/node_modules/pkg"]) mkdirSync(join(app, directory), { recursive: true });
    const window = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup></Project>`;
    writeFileSync(join(app, "App.csproj"), window);
    writeFileSync(join(app, "obj", "Debug", "net10.0", "Copy.csproj"), window);
    writeFileSync(join(app, "bin", "Debug", "Copy.csproj"), window);
    writeFileSync(join(app, "Frontend", "node_modules", "pkg", "runic.json"), "{}");
    writeFileSync(join(app, "translations", "runic.json"), "{}");
    expect(detectWorkspace(app)).toEqual({ sdkProjects: [join(app, "App.csproj")], windowProjects: [join(app, "App.csproj")], translationsProjects: [join(app, "translations", "runic.json")] });
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

test("the core tool is RunicArtifex.Tool (rax) or dotnet-runic (runic), in either manifest location", () => {
  // dotnet tool install writes the ID in lower case; NuGet IDs are case-insensitive.
  expect(manifestTool(JSON.stringify({ tools: { "runicartifex.tool": { version: "0.25.0", commands: ["rax"] } } }))).toEqual({ tool: coreTool, version: "0.25.0" });
  expect(manifestTool(JSON.stringify({ tools: { "dotnet-runic": { version: "0.24.0", commands: ["dotnet-runic"] } } }))?.tool).toBe(formerCoreTool);
  // Mid-migration a manifest lists both; the new tool runs.
  expect(manifestTool(JSON.stringify({ tools: { "dotnet-runic": { version: "0.24.0" }, "RunicArtifex.Tool": { version: "0.25.0" } } }))?.version).toBe("0.25.0");
  expect(manifestTool(JSON.stringify({ tools: { "dotnet-ef": { version: "9.0.0" } } }))).toBeUndefined();
  expect(coreTool.verb).toBe("rax"); expect(formerCoreTool.verb).toBe("runic");
  expect(devArguments("/a/App.csproj", false, true, coreTool.verb)).toEqual(["rax", "dev", "--debug-endpoint", "--project", "/a/App.csproj"]);
  expect(devArguments("/a/App.csproj", true)).toEqual(["runic", "dev", "--no-window", "--project", "/a/App.csproj"]);
  expect(doctorArguments("/a/App.csproj", "rax")[0]).toBe("rax");
  const root = temporary("rax-tool");
  try {
    mkdirSync(join(root, "src", "App"), { recursive: true });
    const project = join(root, "src", "App", "App.csproj");
    // The root dotnet-tools.json (.NET 10's dotnet new tool-manifest).
    writeFileSync(join(root, "dotnet-tools.json"), JSON.stringify({ isRoot: true, tools: { "runicartifex.tool": { version: "0.25.0", commands: ["rax"] } } }));
    expect(projectCoreTool(project)).toEqual({ tool: coreTool, version: "0.25.0", manifest: join(root, "dotnet-tools.json") });
    expect(devSessionPath(project, projectCoreTool(project)?.tool)).toBe(join(root, "src", "App", "obj", "rax", "dev-session.json"));
    // .config/dotnet-tools.json is read first in the same directory.
    mkdirSync(join(root, ".config"));
    writeFileSync(join(root, ".config", "dotnet-tools.json"), JSON.stringify({ tools: { "dotnet-runic": { version: "0.24.0", commands: ["dotnet-runic"] } } }));
    expect(projectCoreTool(project)?.tool).toBe(formerCoreTool);
    expect(devSessionPath(project, formerCoreTool)).toBe(join(root, "src", "App", "obj", "runic", "dev-session.json"));
    // A nearer root manifest without a Runic tool ends the search.
    writeFileSync(join(root, "src", "dotnet-tools.json"), JSON.stringify({ isRoot: true, tools: {} }));
    expect(projectCoreTool(project)).toBeUndefined();
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("former tool IDs get a migration offer only where RunicArtifex.Tool is available", () => {
  const former = JSON.stringify({ tools: { "dotnet-runic": { version: "0.24.0" }, "Dotnet-Runic-Translations": { version: "0.16.0" } } });
  const manifest = join("/w", ".config", "dotnet-tools.json");
  expect(formerToolIds(former)).toEqual(["dotnet-runic", "dotnet-runic-translations"]);
  // New IDs only: nothing to migrate.
  expect(migrationOffer([{ path: manifest, text: JSON.stringify({ tools: { "runicartifex.tool": {} } }) }])).toBeUndefined();
  // An old ID only, and no rax resolves (0.24, before the rename ships): no offer, not even the guide.
  expect(migrationProbeDirectory([{ path: manifest, text: former }])).toBe("/w");
  expect(migrationOffer([{ path: manifest, text: former }])).toBeUndefined();
  // An old ID and a rax that resolves (dotnet rax --version or a global rax --version): the offer runs it.
  const global = migrationOffer([{ path: manifest, text: former }], { directory: "/w", program: "rax" })!;
  expect(global.formerIds).toEqual(["dotnet-runic", "dotnet-runic-translations"]);
  expect(global.runner).toEqual({ directory: "/w", program: "rax" });
  expect(global.message).toContain("dotnet-runic → RunicArtifex.Tool (rax)");
  // An old ID next to RunicArtifex.Tool in the manifest: offered without a probe.
  const listed = [{ path: manifest, text: JSON.stringify({ tools: { "RunicArtifex.Tool": { version: "0.25.0" }, "dotnet-runic-translations": {} } }) }];
  expect(migrationProbeDirectory(listed)).toBeUndefined();
  const runnable = migrationOffer(listed)!;
  expect(runnable.formerIds).toEqual(["dotnet-runic-translations"]);
  expect(runnable.runner).toEqual({ directory: "/w", program: "dotnet" });
  expect(raxVersionArguments("dotnet")).toEqual(["rax", "--version"]);
  expect(raxVersionArguments("rax")).toEqual(["--version"]);
  expect(migrateArguments(true)).toEqual(["rax", "migrate", "--dry-run", "--json"]);
  expect(migrateArguments(false, "rax")).toEqual(["migrate", "--json"]);
  expect(migrationSummary(JSON.stringify({ payload: { rewritten: [1, 2], moved: [], reported: [3] } }))).toBe("2 rewritten, 0 moved, 1 reported");
  expect(migrationSummary("Nothing to do.\n")).toBe("Nothing to do.");
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
  expect(parseDevSession(JSON.stringify({ ...session(), schema: raxDevSessionSchema }))?.state).toBe("ready");
});

test("dotnet runic dev's command line is recognized as the DLL, the apphost or the tool name", () => {
  expect(isDevCommandLine(["/usr/share/dotnet/dotnet", "exec", "--runtimeconfig", "x.json", "/home/u/.nuget/packages/dotnet-runic/0.20.0/tools/net10.0/any/dotnet-runic.dll", "dev", "--no-window"])).toBe(true);
  expect(isDevCommandLine(["/home/u/.dotnet/tools/dotnet-runic", "dev"])).toBe(true);
  expect(isDevCommandLine(["C:\\Users\\u\\.nuget\\packages\\dotnet-runic\\0.20.0\\tools\\net10.0\\any\\dotnet-runic.exe", "dev"])).toBe(true);
  expect(isDevCommandLine(["/usr/share/dotnet/dotnet", "exec", "/x/dotnet-runic.dll", "doctor"])).toBe(false);
  // After the rename: RunicArtifex.Tool's DLL under dotnet, the rax shim, and under NativeAOT rax itself with no dotnet parent.
  expect(isDevCommandLine(["/usr/share/dotnet/dotnet", "exec", "/home/u/.nuget/packages/runicartifex.tool/0.25.0/tools/net10.0/any/RunicArtifex.Tool.dll", "dev"])).toBe(true);
  expect(isDevCommandLine(["/home/u/.nuget/packages/runicartifex.tool/0.25.0/tools/net10.0/linux-x64/rax", "dev", "--no-window"])).toBe(true);
  expect(isDevCommandLine(["C:\\Users\\u\\.dotnet\\tools\\rax.exe", "dev"])).toBe(true);
  expect(isDevCommandLine(["/usr/bin/rax", "doctor"])).toBe(false);
  expect(isDevCommandLine(["/usr/bin/raxx", "dev"])).toBe(false);
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
  for (const code of ["RUNICBRIDGE013", "RAPPDEV1007", "RAPPCLI1009", "RAPPPKG1001", "RCLI8101", "RUNICP008"]) expect(isRunicCode(code)).toBe(true);
  for (const code of ["CS1002", "RTR0090", "RUNICBRIDGE", "MSB3073", "RUNICPA001"]) expect(isRunicCode(code)).toBe(false);
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
  // 0.25's rax.* IDs (D080.7) chain onto runic.* and its own former name.
  const chain = { "rax.dotnetPath": "runic.dotnetPath", "runic.dotnetPath": "runicTranslations.dotnetPath" };
  expect(settingValue("rax.dotnetPath", "dotnet", configuration({ "runic.dotnetPath": { globalValue: "/runic/dotnet" }, "runicTranslations.dotnetPath": { globalValue: "/old/dotnet" } }), chain)).toBe("/runic/dotnet");
  expect(settingValue("rax.dotnetPath", "dotnet", configuration({ "runicTranslations.dotnetPath": { globalValue: "/old/dotnet" } }), chain)).toBe("/old/dotnet");
  expect(settingValue("rax.dotnetPath", "dotnet", configuration({ "rax.dotnetPath": { defaultValue: "dotnet" } }), { ...chain, "runicTranslations.dotnetPath": "rax.dotnetPath" })).toBe("dotnet");
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
  // The extension's dev terminal reports the problems itself (DevProblems); its matchers only mark the task ready.
  expect(matchers["runic-dev-terminal"]).toEqual(matchers["runic-dev"]);
  expect(matchers["runic-dev-terminal-no-window"]).toEqual(matchers["runic-dev-no-window"]);
  expect(matchers["runic-dev"]).toEqual({ activeOnStart: true, beginsPattern: beginsPattern.source, endsPattern: readyPattern.source });
  expect(matchers["runic-dev-no-window"].beginsPattern).toBe(beginsNoWindowPattern.source);
  const patterns = Object.fromEntries(manifest.contributes.problemPatterns.map((item: { name: string; regexp: string }) => [item.name, item.regexp]));
  expect(patterns["runic-dev"]).toBe(diagnosticPattern.source);
  expect(new RegExp(patterns["runic-dev-none"]).test("[host] /app/X.cs(1,1): error CS1002: ; expected")).toBe(false);
  const pattern = new RegExp(manifest.contributes.problemPatterns[0].regexp);
  expect(pattern.exec("/app/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.")?.slice(1, 7)).toEqual(["/app/App.csproj", undefined, undefined, "error", "RAPPDEV1007", "The vite development server exited unexpectedly with code 1."]);
  expect(pattern.exec("CounterViewModel.cs(18,5): error CS1002: ; expected [/app/App.csproj]")?.slice(1, 7)).toEqual(["CounterViewModel.cs", "18", "5", "error", "CS1002", "; expected"]);
  // dev's [host] label before each build line (#36): the file is the path, not "[host] <path>".
  expect(pattern.exec("[host] /app/WorkspaceViewModel.cs(77,26): error CS1002: ; expected [/app/App.csproj]")?.slice(1, 7)).toEqual(["/app/WorkspaceViewModel.cs", "77", "26", "error", "CS1002", "; expected"]);
  expect(pattern.exec("[host] C:\\src\\App\\WorkspaceViewModel.cs(77,26): error CS1002: ; expected [C:\\src\\App\\App.csproj]")?.slice(1, 7)).toEqual(["C:\\src\\App\\WorkspaceViewModel.cs", "77", "26", "error", "CS1002", "; expected"]);
  expect(pattern.exec("[host]     C:\\src\\App\\CounterViewModel.cs(5,16,5,21): warning CS0168: The variable 'x' is declared but never used [C:\\src\\App\\App.csproj]")?.slice(1, 7)).toEqual(["C:\\src\\App\\CounterViewModel.cs", "5", "16", "warning", "CS0168", "The variable 'x' is declared but never used"]);
  expect(pattern.exec("[app] 1>/app/App.csproj : warning RUNICBRIDGE013: A ViewModel member is not supported.")?.slice(1, 7)).toEqual(["/app/App.csproj", undefined, undefined, "warning", "RUNICBRIDGE013", "A ViewModel member is not supported."]);
  expect(pattern.exec("[host] dotnet watch ❌ Exited with error code 1")).toBeNull();
  expect(pattern.exec("[dev] Ready")).toBeNull();
});

// ---- dev's problems in the extension's own terminal (#36) -----------------------------------------------------------

test("a dev problem keeps its file, line, column, severity and code without the [host] label", () => {
  expect(parseDevProblem("[host] \u001b[31m/app/X.cs(77,26): error CS1002: ; expected [/app/App.csproj]\u001b[39m\r", "/app")).toEqual(
    { file: "/app/X.cs", line: 77, column: 26, severity: "error", code: "CS1002", message: "; expected" });
  expect(parseDevProblem("[host] C:\\src\\App\\X.cs(3,9): warning CS0168: unused [C:\\src\\App\\App.csproj]", "/app")?.file).toBe("C:\\src\\App\\X.cs");
  expect(parseDevProblem("[host] Models/X.cs(3,9): warning CS0168: unused", "/app")).toEqual(
    { file: "/app/Models/X.cs", line: 3, column: 9, severity: "warning", code: "CS0168", message: "unused" });
  expect(parseDevProblem("/app/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.", "/app")).toEqual(
    { file: "/app/App.csproj", line: 1, column: 1, severity: "error", code: "RAPPDEV1007", message: "The vite development server exited unexpectedly with code 1." });
  expect(parseDevProblem("[vite] 12:00:01 [vite] page reload src/App.tsx", "/app")).toBeUndefined();
});

test("[dev] Ready drops a fixed build error and keeps the warnings; a new cycle drops both", () => {
  const problems = new DevProblems("/app", false);
  const summary = () => problems.problems.map(item => `${item.severity} ${item.code} ${item.file}:${item.line}`);
  expect(problems.add("[dev] Evaluating Views Window project...")).toBe(false);
  expect(problems.add("[host] /app/A.cs(3,9): warning CS0168: unused [/app/App.csproj]")).toBe(true);
  expect(problems.add("[host] /app/B.cs(77,26): error CS1002: ; expected [/app/App.csproj]")).toBe(true);
  // MSBuild's summary repeats them.
  expect(problems.add("[host] /app/B.cs(77,26): error CS1002: ; expected [/app/App.csproj]")).toBe(false);
  expect(problems.add("[host] dotnet watch ❌ Exited with error code 1")).toBe(false);
  expect(summary()).toEqual(["warning CS0168 /app/A.cs:3", "error CS1002 /app/B.cs:77"]);
  // The fix: dotnet watch rebuilds and starts the Window, with no Window exited in between.
  expect(problems.add("[host] /app/A.cs(3,9): warning CS0168: unused [/app/App.csproj]")).toBe(false);
  expect(problems.add("[dev] Window started: process 4120")).toBe(false);
  expect(problems.add("[dev] Ready\r")).toBe(true);
  expect(summary()).toEqual(["warning CS0168 /app/A.cs:3"]);
  expect(problems.add("[dev] Ready")).toBe(false);
  // The same error again after Ready is a problem again.
  expect(problems.add("[dev] Window exited: process 4120")).toBe(true);
  expect(problems.problems).toEqual([]);
  expect(problems.add("[host] /app/B.cs(77,26): error CS1002: ; expected [/app/App.csproj]")).toBe(true);
  expect(summary()).toEqual(["error CS1002 /app/B.cs:77"]);
  // --no-window begins a cycle only at [dev] Evaluating.
  const frontend = new DevProblems("/app", true);
  frontend.add("/app/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.");
  expect(frontend.add("[dev] Window exited: process 1")).toBe(false);
  expect(frontend.add("[dev] Evaluating Views Window project...")).toBe(true);
});

/** dev's output with runic-sdk 0.23's frontend errors (runic-sdk#1212, recorded), around a .NET build error. */
const viteOutput = [
  "[dev] Evaluating Views Window project...",
  "[host] /tmp/t1160/App/WorkspaceViewModel.cs(77,26): error CS1002: ; expected [/tmp/t1160/App/App.csproj]",
  "[dev] Ready",
  "[vite] 1:11:03 PM [vite] (client) hmr update /src/App.tsx",
  "[vite] /tmp/t1160/App/Frontend/src/App.tsx(29,50): error RUNICP008: [vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.",
  "[host] /tmp/t1160/App/WorkspaceViewModel.cs(77,26): error CS1002: ; expected [/tmp/t1160/App/App.csproj]",
  "[dev] Ready",
  "[vite] 1:11:06 PM [vite] (client) hmr update /src/App.tsx",
  "[vite] [runic] Frontend errors cleared",
];

test("editors#52: a frontend error from dev's [vite] label stays until the frontend errors are cleared", () => {
  const problems = new DevProblems("/tmp/t1160/App", false);
  const codes = () => problems.problems.map(item => item.code);
  for (const line of viteOutput.slice(0, 5)) problems.add(line);
  expect(problems.problems).toEqual([{ file: "/tmp/t1160/App/Frontend/src/App.tsx", line: 29, column: 50, severity: "error", code: "RUNICP008",
    message: "[vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'." }]);
  expect(problems.add(viteOutput[5])).toBe(true);
  // [dev] Ready ends the .NET build error, not the frontend's.
  expect(problems.add(viteOutput[6])).toBe(true);
  expect(codes()).toEqual(["RUNICP008"]);
  expect(problems.add(viteOutput[7])).toBe(false);
  expect(problems.add(`\u001b[2m${viteOutput[8]}\u001b[22m\r`)).toBe(true);
  expect(problems.problems).toEqual([]);
  expect(problems.add(viteOutput[8])).toBe(false);
  expect(problems.add("[host] [runic] Frontend errors cleared")).toBe(false);
});

test("editors#52: the clear line drops only the frontend errors, and the same error comes back after it", () => {
  const problems = new DevProblems("/app", true);
  const frontendError = "[vite] C:\\app\\Frontend\\src\\App.tsx(3,7): error RUNICP008: [vite:import-analysis] Failed to resolve import \"./Missing\" from \"src/App.tsx\".";
  expect(problems.add(frontendError)).toBe(true);
  expect(problems.problems[0]!.file).toBe("C:\\app\\Frontend\\src\\App.tsx");
  expect(problems.add("/app/B.cs(77,26): error CS1002: ; expected [/app/App.csproj]")).toBe(true);
  expect(problems.add("3>A.cs(10,9,10,20): warning RUNICBRIDGE1003: The state Count has no setter. [/app/App.csproj]")).toBe(true);
  expect(problems.add("[vite] [runic] Frontend errors cleared")).toBe(true);
  expect(problems.problems.map(item => item.code)).toEqual(["CS1002", "RUNICBRIDGE1003"]);
  expect(problems.add(frontendError)).toBe(true);
  expect(problems.add("[dev] Evaluating Views Window project...")).toBe(true);
  expect(problems.problems).toEqual([]);
});

test("editors#52: the $runic-dev problem pattern of a process task reads a [vite] RUNICP008 line", async () => {
  const manifest = JSON.parse(await Bun.file(join(import.meta.dir, "..", "package.json")).text());
  const pattern = manifest.contributes.problemPatterns.find((item: { name: string }) => item.name === "runic-dev");
  const match = new RegExp(pattern.regexp).exec(viteOutput[4]);
  expect(match?.[1]).toBe("/tmp/t1160/App/Frontend/src/App.tsx");
  expect(match?.[5]).toBe("RUNICP008");
});

test("dev's output splits into lines across chunks", () => {
  const lines = new LineSplitter();
  expect(lines.push("[host] /app/B.cs(77,26): err")).toEqual([]);
  expect(lines.push("or CS1002: ; expected\r\n[dev] Re")).toEqual(["[host] /app/B.cs(77,26): error CS1002: ; expected\r"]);
  expect(lines.push("ady\n")).toEqual(["[dev] Ready"]);
  expect(lines.push("[dev] Window exited: process 1")).toEqual([]);
  expect(lines.flush()).toEqual(["[dev] Window exited: process 1"]);
  expect(lines.flush()).toEqual([]);
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

test("dev has ended when its process exits, even while a child it left behind holds its output", async () => {
  if (process.platform === "win32") return;
  // The 0.22 checklist, row 6: dev stops on its input and exits, but its frontend server keeps dev's output pipe open.
  const child = spawnDev("/bin/sh", ["-c", "sleep 60 & echo \"child=$!\"; read line; exit 130"], tmpdir(), true);
  let output = "";
  child.stdout!.setEncoding("utf8"); child.stdout!.on("data", (text: string) => { output += text; });
  const ended = new Promise<number | null>(resolveEnd => onDevEnd(child, code => resolveEnd(code), 300));
  while (!/child=(\d+)/.test(output)) await new Promise(resolveWait => setTimeout(resolveWait, 50));
  const orphan = Number(/child=(\d+)/.exec(output)![1]);
  try {
    const started = Date.now();
    expect(await stopDevProcess(child, { stopOnInput: true, inputTimeout: 5000, force: forceStop })).toBe("input");
    expect(await ended).toBe(130);
    expect(Date.now() - started).toBeLessThan(3000);
  } finally {
    try { process.kill(orphan, "SIGKILL"); } catch { /* gone already */ }
    child.stdout!.destroy(); child.stderr!.destroy();
  }
}, 20000);

test("the forced stop signals dev's process group off Windows, and the process alone when it leads none", () => {
  const calls: [number, string][] = [];
  const kill = (pid: number, signal: NodeJS.Signals) => { calls.push([pid, signal]); };
  forceStop({ pid: 4242 } as never, 1, "linux", kill, 60_000);
  forceStop({ pid: 4242 } as never, 2, "darwin", kill);
  expect(calls).toEqual([[-4242, "SIGINT"], [-4242, "SIGKILL"]]);
  const fallback: [number, string][] = [];
  forceStop({ pid: 77 } as never, 2, "linux", (pid, signal) => { fallback.push([pid, signal]); if (pid < 0) throw new Error("ESRCH"); });
  expect(fallback).toEqual([[-77, "SIGKILL"], [77, "SIGKILL"]]);
});

/** A `dotnet` launcher stand-in that runs `dev` as its child and forwards no signal; `dev` prints its PID. */
function launcherWithDev(devScript: string) {
  const child = spawnDev("/bin/sh", ["-c", `sh -c '${devScript}'; echo launcher-done`], tmpdir(), true);
  let output = "";
  child.stdout!.setEncoding("utf8"); child.stdout!.on("data", (text: string) => { output += text; });
  const devPid = async () => {
    while (!/dev=(\d+)/.test(output)) await new Promise(resolveWait => setTimeout(resolveWait, 50));
    return Number(/dev=(\d+)/.exec(output)![1]);
  };
  return { child, devPid };
}

const isAlive = (pid: number) => { try { process.kill(pid, 0); return true; } catch { return false; } };
async function gone(pid: number, within: number): Promise<boolean> {
  const deadline = Date.now() + within;
  while (Date.now() < deadline) { if (!isAlive(pid)) return true; await new Promise(resolveWait => setTimeout(resolveWait, 50)); }
  return !isAlive(pid);
}

test("editors#53: the forced stop reaches dev behind the launcher; signalling the launcher alone left dev running", async () => {
  if (process.platform === "win32") return;
  // dev ignores its input (an older dev, or a hung one) and stops on SIGINT, as dotnet runic dev does on Ctrl+C.
  const devScript = `trap "exit 130" INT; echo dev=$$; while :; do sleep 0.2; done`;
  const before = launcherWithDev(devScript);
  const oldDev = await before.devPid();
  const launcherOnly = (child: { pid?: number }, attempt: 1 | 2) => { try { process.kill(child.pid!, attempt === 1 ? "SIGINT" : "SIGKILL"); } catch { /* gone */ } };
  expect(await stopDevProcess(before.child, { stopOnInput: true, inputTimeout: 300, forceTimeout: 1500, force: launcherOnly })).toBe("forced");
  expect(await gone(oldDev, 1000)).toBe(false); // the bug: dev outlives its launcher
  process.kill(oldDev, "SIGKILL");

  const now = launcherWithDev(devScript);
  const dev = await now.devPid();
  expect(await stopDevProcess(now.child, { stopOnInput: true, inputTimeout: 300, forceTimeout: 3000, force: (child, attempt) => forceStop(child, attempt, process.platform, undefined, 60_000) })).toBe("forced");
  expect(await gone(dev, 3000)).toBe(true);
}, 30000);

test("editors#53: a dev that ignores SIGINT too is killed with its process group", async () => {
  if (process.platform === "win32") return;
  const { child, devPid } = launcherWithDev(`trap "" INT; echo dev=$$; while :; do sleep 0.2; done`);
  const dev = await devPid();
  expect(await stopDevProcess(child, { stopOnInput: true, inputTimeout: 300, forceTimeout: 1000, force: (target, attempt) => forceStop(target, attempt, process.platform, undefined, 1500) })).toBe("forced");
  expect(await gone(dev, 4000)).toBe(true);
}, 30000);
