import { test, expect } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { dependencyLabel, dependencyList, dependencyStateLabel, dependencyWatchLabel, frontendGraphPaths, frontendInstall, graphDependencies, installCommand, parseFrontendGraph, projectFrontendDirectory, readFrontendGraph, sessionDependencies, unbuiltDependencies, withDevFailures, withOwners, workspaceRoot, type DevDependency } from "../src/dependencies.js";
import { addWorkspaceMember, dependencyFixes, dependencyPrefix, propertyLine, removeJsonProperty, type TextChange } from "../src/dependency-fixes.js";
import { devSessionSchema, parseDevSession, type DevSession } from "../src/dev-session.js";
import { coreTool, detectWorkspace, formerCoreTool, isWindowProject } from "../src/workspace.js";

const temporary = (prefix: string) => mkdtempSync(join(tmpdir(), `runic-vscode-${prefix}-`));
const apply = (text: string, change: TextChange | undefined) => change ? text.slice(0, change.start) + change.text + text.slice(change.end) : text;

/** The `dependencies[]` entry of runic-sdk's README ("Session file", 0.24, devtools-library-dx S5). */
const sharedUi = {
  package: "@acme/shared-ui", directory: "/repo/shared-ui", kind: "compiled", owner: "/repo/SharedUi/SharedUi.csproj", ownerName: "SharedUi",
  specifier: "@acme/shared-ui/generated", buildCommand: "bun run --bun build", watchCommand: null, exports: ["/repo/shared-ui/dist/index.js"],
  state: "built", watch: "rebuild", message: null,
};
const session = (overrides: Record<string, unknown> = {}): DevSession => parseDevSession(JSON.stringify({
  schema: devSessionSchema, state: "ready", processId: 41250, project: "/repo/App/App.csproj", frontend: { directory: "/repo/App/Frontend" }, ...overrides,
}))!;
/** A graph file as runic-frontend-graph.mjs writes it (rax.frontend-graph/1). */
const graphText = (packages: unknown[], extra: Record<string, unknown> = {}) => JSON.stringify({
  schema: "rax.frontend-graph/1", workspaceRoot: "/repo", packageManager: "bun", lockFile: "/repo/bun.lock", member: true,
  application: { directory: "/repo/App/Frontend", package: "@acme/workbench", kind: "compiled", build: "bun run --bun build", watch: null, exports: [], generatedExport: null, outputs: [] },
  members: [{ directory: "/repo/App/Frontend", package: "@acme/workbench" }, { directory: "/repo/shared-ui", package: "@acme/shared-ui" }], packages, ...extra,
});
const compiledPackage = { directory: "/repo/shared-ui", package: "@acme/shared-ui", kind: "compiled", build: "bun run --bun build", watch: "bun run --bun watch", exports: ["./dist/index.js", "./dist/generated/runic-clients.js"], generatedExport: "./dist/generated", outputs: ["dist"] };
const sourcePackage = { directory: "/repo/tokens", package: "@acme/tokens", kind: "source", build: null, watch: null, exports: ["./src/index.ts"], generatedExport: null, outputs: [] };

// ---- Window projects -------------------------------------------------------------------------------------------------

test("only Window projects are dev targets: a client library, a plain library and a test project are not", () => {
  // The template's App (RunicWindowApp.csproj) and the shared-library example's App (an executable with a host, no property).
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><OutputType Condition="x">WinExe</OutputType><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views.Desktop" Version="0.24.0" /></ItemGroup></Project>`)).toBe(true);
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="../SharedUi/SharedUi.csproj" /><ProjectReference Include="../../../packages/dotnet/Runic.Application.Views.CsWebUi/Runic.Application.Views.CsWebUi.csproj" /></ItemGroup></Project>`)).toBe(true);
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="Runic.Desktop.Kestrel" Version="0.24.0" /></ItemGroup></Project>`)).toBe(true);
  // The example's SharedUi (a client library, no OutputType) and a library that sets the property anyway.
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>`)).toBe(false);
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>`)).toBe(false);
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>`)).toBe(false);
  // The template's tests: an executable (TUnit) that references Runic.Application.Testing.
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="TUnit" Version="1.73.19" /><PackageReference Include="Runic.Application.Testing" Version="0.24.0" /></ItemGroup></Project>`)).toBe(false);
  expect(isWindowProject(`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Translations" Version="0.15.1" /></ItemGroup></Project>`)).toBe(false);
});

test("workspace detection lists a library + app solution's projects and only the app as a Window project", () => {
  const root = temporary("window");
  try {
    for (const directory of ["App", "SharedUi", "App.Tests"]) mkdirSync(join(root, directory));
    writeFileSync(join(root, "App", "App.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject></PropertyGroup></Project>`);
    writeFileSync(join(root, "SharedUi", "SharedUi.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Views" Version="0.24.0" /></ItemGroup></Project>`);
    writeFileSync(join(root, "App.Tests", "App.Tests.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Runic.Application.Testing" Version="0.24.0" /></ItemGroup></Project>`);
    const kind = detectWorkspace(root);
    expect(kind.sdkProjects).toEqual([join(root, "App.Tests", "App.Tests.csproj"), join(root, "App", "App.csproj"), join(root, "SharedUi", "SharedUi.csproj")]);
    expect(kind.windowProjects).toEqual([join(root, "App", "App.csproj")]);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// ---- The session file's dependencies ----------------------------------------------------------------------------------

test("dependencies[] of a 0.24 session parse; a session before 0.24 has none, and the session still parses", () => {
  const old = session();
  expect(old.state).toBe("ready");
  expect(sessionDependencies(old)).toBeUndefined();
  expect(sessionDependencies(undefined)).toBeUndefined();
  const current = session({ dependencies: [sharedUi, { directory: "/repo/tokens", state: "source", kind: "source", watch: "vite", future: 1 }, { package: "no-directory" }, 7] });
  expect(sessionDependencies(current)).toEqual([sharedUi as DevDependency, {
    package: null, directory: "/repo/tokens", kind: "source", owner: null, ownerName: null, specifier: null, buildCommand: null, watchCommand: null, exports: [], state: "source", watch: "vite", message: null,
  }]);
  expect(sessionDependencies(session({ dependencies: [] }))).toEqual([]);
});

test("the graph file's packages: built, missing output or source, from the files on disk, with owners of the last session", () => {
  expect(parseFrontendGraph("{")).toBeUndefined();
  expect(parseFrontendGraph(JSON.stringify({ schema: "rax.frontend-graph/2", workspaceRoot: "/repo", application: { directory: "/repo/App/Frontend" } }))).toBeUndefined();
  const graph = parseFrontendGraph(graphText([sourcePackage, compiledPackage]))!;
  expect(graph.workspaceRoot).toBe("/repo");
  expect(graph.packages.map(item => item.package)).toEqual(["@acme/tokens", "@acme/shared-ui"]);
  const nothingBuilt = graphDependencies(graph, () => false);
  expect(nothingBuilt.map(item => [dependencyLabel(item), item.state, item.owner])).toEqual([["@acme/tokens", "source", null], ["@acme/shared-ui", "missing-output", null]]);
  expect(nothingBuilt[1].exports).toEqual([join("/repo/shared-ui", "dist/index.js"), join("/repo/shared-ui", "dist/generated/runic-clients.js")]);
  const built = graphDependencies(graph, () => true, [sharedUi as DevDependency]);
  expect(built[1]).toMatchObject({ state: "built", owner: "/repo/SharedUi/SharedUi.csproj", ownerName: "SharedUi", specifier: "@acme/shared-ui/generated", buildCommand: "bun run --bun build", watchCommand: "bun run --bun watch" });
  const half = graphDependencies(graph, path => path.endsWith("index.js"));
  expect(half[1].state).toBe("missing-output");
});

test("the dependency list: the running session's, else the graph's, else a stopped session's", () => {
  const graph = parseFrontendGraph(graphText([compiledPackage]))!;
  const failed = { ...sharedUi, state: "failed", message: "shared-ui/src/about-panel.ts(42,31): error TS1109: Expression expected." };
  const current = session({ dependencies: [failed] });
  // dev runs with dependencies[]: its states, also a failure.
  expect(dependencyList(current, true, () => graph)).toEqual({ source: "session", dependencies: [failed as DevDependency] });
  // dev runs, but before 0.24 (no dependencies): the graph file.
  expect(dependencyList(session(), true, () => graph, () => true)?.source).toBe("graph");
  // dev stopped: the graph, which says what is on disk now, with the session's owners.
  const stopped = dependencyList(current, false, () => graph, () => false)!;
  expect(stopped.source).toBe("graph");
  expect(stopped.dependencies[0]).toMatchObject({ state: "missing-output", ownerName: "SharedUi" });
  expect(stopped.workspace).toEqual({ root: "/repo", packageManager: "bun", lockFile: "/repo/bun.lock", member: true, application: "/repo/App/Frontend" });
  expect(unbuiltDependencies(stopped).map(dependencyLabel)).toEqual(["@acme/shared-ui"]);
  // No graph (a fresh clone or targets before 0.24): the stopped session's list, else nothing.
  expect(dependencyList(current, false, () => undefined)?.source).toBe("last-session");
  expect(dependencyList(session(), false, () => undefined)).toBeUndefined();
  expect(dependencyList(undefined, false, () => undefined)).toBeUndefined();
  // A running session's failure is the session's to report; the status bar offers no build for it.
  expect(unbuiltDependencies(dependencyList(current, true, () => graph))).toEqual([]);
});

test("a package without an owner gets the library project whose frontend folder it is", () => {
  const list = dependencyList(undefined, false, () => parseFrontendGraph(graphText([sourcePackage, compiledPackage])), () => true)!;
  const owned = withOwners(list, [{ project: "/repo/SharedUi/SharedUi.csproj", directory: "/repo/shared-ui/" }])!;
  expect(owned.dependencies.map(item => [item.ownerName, item.owner])).toEqual([[null, null], ["SharedUi", "/repo/SharedUi/SharedUi.csproj"]]);
  // A known owner stays; no libraries, no change.
  const known = { source: "session" as const, dependencies: [{ ...sharedUi, owner: "/x/Other.csproj", ownerName: "Other" } as DevDependency] };
  expect(withOwners(known, [{ project: "/repo/SharedUi/SharedUi.csproj", directory: "/repo/shared-ui" }])!.dependencies[0].ownerName).toBe("Other");
  expect(withOwners(list, [])).toBe(list);
  expect(withOwners(undefined, [{ project: "/a.csproj", directory: "/a" }])).toBeUndefined();
});

test("the graph file sits in obj/runic, and in obj/rax first for RunicArtifex.Tool", () => {
  expect(frontendGraphPaths("/repo/App/App.csproj")).toEqual([join("/repo/App", "obj", "runic", "frontend-graph.json")]);
  expect(frontendGraphPaths("/repo/App/App.csproj", formerCoreTool)).toEqual([join("/repo/App", "obj", "runic", "frontend-graph.json")]);
  expect(frontendGraphPaths("/repo/App/App.csproj", coreTool)).toEqual([join("/repo/App", "obj", "rax", "frontend-graph.json"), join("/repo/App", "obj", "runic", "frontend-graph.json")]);
  const root = temporary("graph");
  try {
    mkdirSync(join(root, "obj", "runic"), { recursive: true });
    writeFileSync(join(root, "obj", "runic", "frontend-graph.json"), graphText([compiledPackage]));
    expect(readFrontendGraph(frontendGraphPaths(join(root, "App.csproj"), coreTool))?.packages.length).toBe(1);
    expect(readFrontendGraph([join(root, "missing.json")])).toBeUndefined();
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("state and watcher labels", () => {
  const entry = sharedUi as DevDependency;
  expect(["built", "building", "failed", "missing-output", "source", "not-built", "later"].map(state => dependencyStateLabel({ ...entry, state })))
    .toEqual(["built", "building", "failed", "not built", "source", "build off", "later"]);
  expect(["script", "rebuild", "vite", "none"].map(watch => dependencyWatchLabel({ ...entry, watch }))).toEqual(["watched by its watch script", "rebuilt on change", "watched by Vite", undefined]);
  expect(dependencyLabel({ ...entry, package: null })).toBe("shared-ui");
});

// ---- The fresh clone ---------------------------------------------------------------------------------------------------

test("a fresh clone's install: the workspace root's locked install, offered only while node_modules is missing", () => {
  const root = temporary("install");
  try {
    const frontend = join(root, "App", "Frontend");
    mkdirSync(frontend, { recursive: true }); mkdirSync(join(root, "shared-ui"));
    writeFileSync(join(root, "package.json"), JSON.stringify({ private: true, workspaces: ["App/Frontend", "shared-ui"], packageManager: "bun@1.4.4" }));
    writeFileSync(join(frontend, "package.json"), JSON.stringify({ name: "app", dependencies: { "@acme/shared-ui": "workspace:*" } }));
    expect(workspaceRoot(frontend, root)).toBe(root);
    // No lock: nothing to offer but the hint (RUNICBRIDGE026).
    expect(frontendInstall(frontend, root)).toEqual({ root, packageManager: "bun", needed: true });
    writeFileSync(join(root, "bun.lock"), "{}");
    expect(frontendInstall(frontend, root)).toEqual({ root, packageManager: "bun", lockFile: join(root, "bun.lock"), needed: true });
    expect(installCommand("bun")).toEqual(["bun", "install", "--frozen-lockfile", "--ignore-scripts"]);
    mkdirSync(join(root, "node_modules"));
    expect(frontendInstall(frontend, root)?.needed).toBe(false);
    // The workspace above the VS Code folder does not count; the frontend is its own root then.
    expect(workspaceRoot(frontend, join(root, "App"))).toBe(frontend);
    // A frontend with a lock of its own is its own root; the lock decides the manager without packageManager.
    writeFileSync(join(frontend, "package-lock.json"), "{}");
    expect(frontendInstall(frontend, root)).toEqual({ root: frontend, packageManager: "npm", lockFile: join(frontend, "package-lock.json"), needed: true });
    expect(installCommand("npm")).toEqual(["npm", "ci", "--ignore-scripts"]);
    expect(installCommand("pnpm")).toEqual(["pnpm", "install", "--frozen-lockfile", "--ignore-scripts"]);
    // A frontend without dependencies needs no install; one without package.json has none.
    writeFileSync(join(frontend, "package.json"), JSON.stringify({ name: "app" }));
    expect(frontendInstall(frontend, root)?.needed).toBe(false);
    expect(frontendInstall(join(root, "missing"), root)).toBeUndefined();
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the frontend folder a project file names", () => {
  expect(projectFrontendDirectory("/repo/App/App.csproj", "<Project />")).toBe(join("/repo/App", "Frontend"));
  expect(projectFrontendDirectory("/repo/SharedUi/SharedUi.csproj", "<RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/../shared-ui</RunicApplicationFrontendDirectory>")).toBe("/repo/shared-ui");
  expect(projectFrontendDirectory("/repo/App/App.csproj", "<RunicApplicationFrontendDirectory>ui</RunicApplicationFrontendDirectory>")).toBe("/repo/App/ui");
  expect(projectFrontendDirectory("/repo/App/App.csproj", "<RunicApplicationFrontendDirectory>$(SolutionDir)ui</RunicApplicationFrontendDirectory>")).toBeUndefined();
});

// ---- Quick fixes -------------------------------------------------------------------------------------------------------

test("RUNICBRIDGE026: create the lock at the workspace root or in the frontend, behind a click", () => {
  const workspace = dependencyFixes("RUNICBRIDGE026", "The frontend /repo/App/Frontend is a member of the bun workspace at /repo, which has no bun.lock, so the workspace install would not be locked. Run 'bun install' in /repo and commit bun.lock, or set RunicApplicationFrontendAllowUnlockedInstall=true to install without one.");
  expect(workspace).toEqual([
    { kind: "run", title: "Create bun.lock: run 'bun install --ignore-scripts' in /repo", cwd: "/repo", args: ["bun", "install", "--ignore-scripts"] },
    { kind: "open", title: `Open ${join("/repo", "package.json")}`, file: join("/repo", "package.json"), property: "dependencies" },
  ]);
  const own = dependencyFixes("RUNICBRIDGE026", "/repo/App/Frontend declares dependencies but has no package-lock.json, so the frontend install would not be locked. Create the lock file with 'npm install' and commit it, or set RunicApplicationFrontendAllowUnlockedInstall=true to install without one.");
  expect(own[0]).toEqual({ kind: "run", title: "Create package-lock.json: run 'npm install --ignore-scripts' in /repo/App/Frontend", cwd: "/repo/App/Frontend", args: ["npm", "install", "--ignore-scripts"] });
  expect(dependencyFixes("RUNICBRIDGE026", "Something else.")).toEqual([]);
});

test("RUNICBRIDGE087: add the frontend to the workspaces", () => {
  const fixes = dependencyFixes("RUNICBRIDGE087", "The frontend /repo/shared-ui is below the bun workspace at /repo, but /repo/package.json workspaces do not include it, so the workspace's install would not install it. Add it to the workspace, or set RunicApplicationFrontendWorkspaceRoot=none to install it on its own with its own lock file.");
  expect(fixes[0]).toEqual({ kind: "addWorkspaceMember", title: `Add shared-ui to the workspaces of ${join("/repo", "package.json")}`, file: join("/repo", "package.json"), member: "shared-ui" });
  expect(dependencyFixes("RUNICBRIDGE087", "The frontend /repo/a/ui is below the pnpm workspace at /repo, but /repo/pnpm-workspace.yaml do not include it")[0]).toMatchObject({ kind: "open", file: join("/repo", "pnpm-workspace.yaml") });
});

test("RUNICBRIDGE088: build the package, or add its build script, and open its exports", () => {
  const unbuilt = dependencyFixes("RUNICBRIDGE088", "/repo/App/Frontend depends on @acme/shared-ui (/repo/shared-ui), the frontend package of SharedUi, but its package.json exports ./dist/index.js, which do not exist: its package build is off in SharedUi (RunicApplicationFrontendBuildEnabled=false). Build the package, or turn its build on.");
  expect(unbuilt).toEqual([{ kind: "build", title: "Build frontend dependencies (dotnet build)" }, { kind: "open", title: "Open @acme/shared-ui's exports", file: join("/repo/shared-ui", "package.json"), property: "exports" }]);
  const noScript = dependencyFixes("RUNICBRIDGE088", "/repo/App/Frontend depends on @acme/tokens (/repo/tokens), a workspace package that no .NET project owns, but its package.json exports ./dist/index.js, which do not exist: it has no build script. Add the files, or a build script that writes them.");
  expect(noScript[0]).toEqual({ kind: "open", title: "Add a build script to @acme/tokens's package.json", file: join("/repo/tokens", "package.json"), property: "scripts" });
  const own = dependencyFixes("RUNICBRIDGE088", "SharedUi's frontend package @acme/shared-ui exports ./dist/generated/runic-clients.js, which its build 'bun run --bun build' did not write, so applications that import it fail to resolve it. Make the build script write them, or correct exports in /repo/shared-ui/package.json.");
  expect(own).toEqual([{ kind: "open", title: `Open the exports of ${join("/repo/shared-ui", "package.json")}`, file: join("/repo/shared-ui", "package.json"), property: "exports" }]);
});

test("RUNICBRIDGE089: remove the member's packageManager", () => {
  const fixes = dependencyFixes("RUNICBRIDGE089", "/repo/shared-ui/package.json declares npm (packageManager), but its workspace /repo uses bun (packageManager bun@1.4.4 in /repo/package.json), so the build installs and builds it with bun. Remove the member's packageManager or make it match the workspace's.");
  expect(fixes[0]).toEqual({ kind: "removePackageManager", title: "Remove packageManager from /repo/shared-ui/package.json (the workspace uses bun)", file: "/repo/shared-ui/package.json" });
  expect(fixes[1]).toMatchObject({ kind: "open", file: join("/repo", "package.json"), property: "packageManager" });
});

test("RUNICBRIDGE090: the install page of Node.js, Bun or pnpm", () => {
  expect(dependencyFixes("RUNICBRIDGE090", "The frontend workspace /repo uses npm (lock file), and the build runs 'node' to resolve it, but 'node' did not run: it is not on PATH. Install Node.js 20 or later, or set RunicApplicationFrontendPackageManager or RunicApplicationFrontendCodegenRuntime."))
    .toEqual([{ kind: "url", title: "Open the Node.js install page", url: "https://nodejs.org/en/download" }]);
  expect(dependencyFixes("RUNICBRIDGE090", "The frontend workspace /repo uses bun (packageManager bun@1.4.4 in /repo/package.json), and the build runs 'bun' to resolve it, but 'bun' did not run: it is not on PATH. Install Bun 1.4.4 or later.")[0]).toMatchObject({ url: "https://bun.sh/docs/installation" });
  expect(dependencyFixes("RUNICBRIDGE090", "The frontend workspace at /repo uses pnpm (pnpm-lock.yaml), but 'pnpm --version' failed with exit code 127: pnpm is not on PATH or does not run. Install pnpm, or set RunicApplicationFrontendPackageManager.")[0]).toMatchObject({ title: "Open the pnpm install page", url: "https://pnpm.io/installation" });
});

test("RAPPDEV1018: show the dependency that a dev line names", () => {
  expect(dependencyPrefix("[shared-ui] bun run --bun build exited with code 1: src/x.ts(3,1): error TS1005")).toBe("shared-ui");
  expect(dependencyFixes("RAPPDEV1018", "[shared-ui] bun run --bun build exited with code 1")).toEqual([{ kind: "showDependency", title: "Show the frontend dependencies of dotnet runic dev", label: "shared-ui" }]);
  expect(dependencyFixes("RAPPDEV1018", "A frontend package that the page depends on failed to build, so dev did not start: …")).toEqual([{ kind: "showDependency", title: "Show the frontend dependencies of dotnet runic dev" }]);
  expect(dependencyFixes("RAPPDEV1007", "x")).toEqual([]);
});

test("package.json edits keep the file's formatting", () => {
  const member = `{\n  "name": "@acme/shared-ui",\n  "packageManager": "npm@10.9.0",\n  "private": true\n}\n`;
  expect(apply(member, removeJsonProperty(member, "packageManager"))).toBe(`{\n  "name": "@acme/shared-ui",\n  "private": true\n}\n`);
  const last = `{\n  "name": "@acme/shared-ui",\n  "packageManager": "npm@10.9.0"\n}\n`;
  expect(apply(last, removeJsonProperty(last, "packageManager"))).toBe(`{\n  "name": "@acme/shared-ui"\n}\n`);
  expect(apply(`{"packageManager":"npm@10", "name":"x"}`, removeJsonProperty(`{"packageManager":"npm@10", "name":"x"}`, "packageManager"))).toBe(`{"name":"x"}`);
  expect(removeJsonProperty(`{"name":"x"}`, "packageManager")).toBeUndefined();
  const root = `{\n  "private": true,\n  "workspaces": [\n    "App/Frontend"\n  ]\n}\n`;
  expect(apply(root, addWorkspaceMember(root, "shared-ui"))).toBe(`{\n  "private": true,\n  "workspaces": [\n    "shared-ui",\n    "App/Frontend"\n  ]\n}\n`);
  expect(apply(`{"workspaces": []}`, addWorkspaceMember(`{"workspaces": []}`, "ui"))).toBe(`{"workspaces": ["ui"]}`);
  expect(apply(`{"workspaces": {"packages": ["a"]}}`, addWorkspaceMember(`{"workspaces": {"packages": ["a"]}}`, "ui"))).toBe(`{"workspaces": {"packages": ["ui", "a"]}}`);
  expect(addWorkspaceMember(`{"name": "x"}`, "ui")).toBeUndefined();
  expect(propertyLine(root, "workspaces")).toBe(2);
  expect(propertyLine(root, "missing")).toBe(0);
});

test("the last dev's errors mark the packages they concern as failed, which the graph alone reads as built", () => {
  // The 0.24 VM run (part 3, finding 5): after RAPPDEV1018 the tree said "shop-core · built" from an earlier build's dist.
  const built = { ...sharedUi, state: "built", watch: "none", message: null };
  const other = { ...sharedUi, package: "@acme/charts", directory: "/repo/charts", state: "built", watch: "none", message: null };
  const list = { source: "graph" as const, dependencies: [built, other] };
  const located = { file: "/repo/shared-ui/src/index.ts", line: 12, column: 14, severity: "error", code: "TS2322", message: "Type 'string' is not assignable to type 'number'." };
  const failure = { file: "/repo/App/App.csproj", line: 1, column: 1, severity: "error", code: "RAPPDEV1018", message: "A frontend package that the page depends on failed to build, so dev did not start: /repo/shared-ui/src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'." };
  expect(withDevFailures(list, [located, failure])?.dependencies.map(item => [item.package, item.state, item.message])).toEqual([
    ["@acme/shared-ui", "failed", "src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'."],
    ["@acme/charts", "built", null],
  ]);
  // A RAPPDEV1018 alone names the package by its directory, its [folder] label or its package name.
  expect(withDevFailures(list, [failure])?.dependencies[0].state).toBe("failed");
  expect(withDevFailures(list, [{ ...failure, message: "[charts] bun run --bun build exited with code 1: src/x.ts(3,1): error TS1005: ';' expected." }])?.dependencies.map(item => item.state)).toEqual(["built", "failed"]);
  expect(withDevFailures(list, [{ ...failure, message: "@acme/charts is the frontend package of Charts, and the page imports it, but it has not been built: charts/dist/index.js is missing; its build 'bun run build' did not write it." }])?.dependencies.map(item => item.state)).toEqual(["built", "failed"]);
  // Warnings, other projects' errors and a running session's own states change nothing.
  expect(withDevFailures(list, [{ ...located, severity: "warning" }])).toBe(list);
  expect(withDevFailures(list, [{ ...located, file: "/repo/App/Frontend/src/index.ts" }])?.dependencies.map(item => item.state)).toEqual(["built", "built"]);
  const session = { source: "session" as const, dependencies: [built] };
  expect(withDevFailures(session, [located])).toBe(session);
});
