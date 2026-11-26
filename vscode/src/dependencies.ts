import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import type { DevSession } from "./dev-session.js";
import type { RunicToolId } from "./workspace.js";

/**
 * The frontend packages that a Window project's page depends on (runic-sdk devtools-library-dx §4.1, S8): a client
 * library's package (`RunicApplicationFrontendClientLibrary`) or a JavaScript workspace package that no .NET project
 * owns. While `dotnet runic dev` runs they come from the session file's `dependencies[]` (runic-sdk 0.24, "Library
 * packages under dev"); before that, from the build's `obj/runic/frontend-graph.json` (`rax.frontend-graph/1`, written
 * by the Views targets since 0.24). An older session file has no `dependencies`; then the graph file is read instead.
 */

/** One entry of the session file's `dependencies[]`; every value is an open string, so unknown states still show. */
export interface DevDependency {
  package: string | null;
  directory: string;
  /** `compiled` (it has a build script) or `source`. */
  kind: string;
  /** The .NET project that owns it, or null for a workspace package. */
  owner: string | null;
  ownerName: string | null;
  specifier: string | null;
  buildCommand: string | null;
  watchCommand: string | null;
  exports: string[];
  /** `built`, `building`, `failed`, `missing-output`, `source` or `not-built`. */
  state: string;
  /** `script`, `rebuild`, `vite` or `none`. */
  watch: string;
  /** The last failure while `state` is `failed`. */
  message: string | null;
}

/** Where a list of dependencies came from: the running `dev`, its last session, or the build's graph file. */
export type DependencySource = "session" | "last-session" | "graph";

export interface DependencyList {
  source: DependencySource;
  dependencies: DevDependency[];
  /** The graph's workspace facts, when the list came from it. */
  workspace?: GraphWorkspace;
}

export interface GraphWorkspace { root: string; packageManager: string | null; lockFile: string | null; member: boolean; application: string }

const text = (value: unknown): string | null => typeof value === "string" ? value : null;

/**
 * The `dependencies` of a session file, or undefined when the file has none (a `dotnet runic dev` before 0.24). Entries
 * without a directory are dropped; missing values read as null, `[]` or `"none"`.
 */
export function sessionDependencies(session: DevSession | undefined): DevDependency[] | undefined {
  const value = (session as { dependencies?: unknown } | undefined)?.dependencies;
  if (!Array.isArray(value)) return undefined;
  return value.flatMap(item => {
    if (!item || typeof item !== "object") return [];
    const entry = item as Record<string, unknown>;
    const directory = text(entry.directory);
    if (!directory) return [];
    return [{
      package: text(entry.package), directory, kind: text(entry.kind) ?? "compiled", owner: text(entry.owner), ownerName: text(entry.ownerName),
      specifier: text(entry.specifier), buildCommand: text(entry.buildCommand), watchCommand: text(entry.watchCommand),
      exports: Array.isArray(entry.exports) ? entry.exports.filter((target): target is string => typeof target === "string") : [],
      state: text(entry.state) ?? "unknown", watch: text(entry.watch) ?? "none", message: text(entry.message),
    }];
  });
}

/** `rax.frontend-graph/1`, which the Views targets write to `obj/runic/frontend-graph.json` (runic-sdk 0.24). */
export const frontendGraphSchema = "rax.frontend-graph/1";

interface GraphPackage { directory: string; package: string | null; kind: string; build: string | null; watch: string | null; exports: string[] }
export interface FrontendGraph { workspaceRoot: string; packageManager: string | null; lockFile: string | null; member: boolean; application: GraphPackage; packages: GraphPackage[] }

function graphPackage(value: unknown): GraphPackage | undefined {
  if (!value || typeof value !== "object") return undefined;
  const entry = value as Record<string, unknown>;
  const directory = text(entry.directory);
  if (!directory) return undefined;
  return { directory, package: text(entry.package), kind: text(entry.kind) ?? "source", build: text(entry.build), watch: text(entry.watch),
    exports: Array.isArray(entry.exports) ? entry.exports.filter((target): target is string => typeof target === "string") : [] };
}

/** Parses a graph file; undefined for another schema or a file that is not one. */
export function parseFrontendGraph(fileText: string): FrontendGraph | undefined {
  let value: unknown;
  try { value = JSON.parse(fileText); } catch { return undefined; }
  if (!value || typeof value !== "object") return undefined;
  const graph = value as Record<string, unknown>;
  const root = text(graph.workspaceRoot);
  const application = graphPackage(graph.application);
  if (graph.schema !== frontendGraphSchema || !root || !application) return undefined;
  return {
    workspaceRoot: root, packageManager: text(graph.packageManager), lockFile: text(graph.lockFile), member: graph.member !== false, application,
    packages: Array.isArray(graph.packages) ? graph.packages.map(graphPackage).filter((entry): entry is GraphPackage => !!entry) : [],
  };
}

/**
 * The graph files a project's build may have written, in the order they are read: `obj/rax/` after the rename for a
 * project whose tool is `RunicArtifex.Tool` (as its session file), then `obj/runic/`, where the 0.24 targets write it.
 */
export function frontendGraphPaths(project: string, tool?: RunicToolId): string[] {
  const obj = join(dirname(project), "obj");
  const runic = join(obj, "runic", "frontend-graph.json");
  return tool && !tool.former ? [join(obj, "rax", "frontend-graph.json"), runic] : [runic];
}

/**
 * The dependencies of a graph file, with the state the files on disk give: a source package is `source`; a compiled
 * one is `built` when every file it exports exists, else `missing-output`. Owners come from `known` (the last session's
 * entries, by directory), since the graph names none.
 */
export function graphDependencies(graph: FrontendGraph, exists: (path: string) => boolean = existsSync, known: readonly DevDependency[] = []): DevDependency[] {
  return graph.packages.map(entry => {
    const exports = entry.exports.map(target => resolve(entry.directory, target));
    const previous = known.find(item => samePath(item.directory, entry.directory));
    const compiled = entry.kind === "compiled";
    return {
      package: entry.package, directory: entry.directory, kind: entry.kind, owner: previous?.owner ?? null, ownerName: previous?.ownerName ?? null,
      specifier: previous?.specifier ?? null, buildCommand: entry.build, watchCommand: entry.watch, exports,
      state: !compiled ? "source" : exports.every(exists) ? "built" : "missing-output", watch: "none", message: null,
    };
  });
}

function samePath(a: string, b: string): boolean {
  const normal = (path: string) => resolve(path).replace(/[\\/]+$/, "");
  return process.platform === "win32" || process.platform === "darwin" ? normal(a).toLowerCase() === normal(b).toLowerCase() : normal(a) === normal(b);
}

/**
 * What the Dev Session view lists for a project: the running session's `dependencies` when it has them; otherwise the
 * build's graph file (`readGraph`), with owners from the last session's entries; otherwise a stopped session's own
 * list; otherwise nothing (a fresh clone, or Views targets before 0.24).
 */
export function dependencyList(session: DevSession | undefined, active: boolean, readGraph: () => FrontendGraph | undefined, exists?: (path: string) => boolean): DependencyList | undefined {
  const fromSession = sessionDependencies(session);
  if (active && fromSession) return { source: "session", dependencies: fromSession };
  const graph = readGraph();
  if (graph) return { source: "graph", dependencies: graphDependencies(graph, exists, fromSession), workspace: { root: graph.workspaceRoot, packageManager: graph.packageManager, lockFile: graph.lockFile, member: graph.member, application: graph.application.directory } };
  if (fromSession?.length) return { source: "last-session", dependencies: fromSession };
  return undefined;
}

/**
 * Fills in the owner of each package that names none from the workspace's library projects (`project`, with the
 * frontend folder its project file names): the graph file and a session's last entries may know no owner.
 */
export function withOwners(list: DependencyList | undefined, libraries: readonly { project: string; directory: string }[]): DependencyList | undefined {
  if (!list || !libraries.length) return list;
  return { ...list, dependencies: list.dependencies.map(dependency => {
    if (dependency.owner) return dependency;
    const library = libraries.find(item => samePath(item.directory, dependency.directory));
    return library ? { ...dependency, owner: library.project, ownerName: library.project.replace(/\.csproj$/i, "").split(/[\\/]/).at(-1) ?? null } : dependency;
  }) };
}

/** Reads the first graph file of `paths` that parses. */
export function readFrontendGraph(paths: readonly string[]): FrontendGraph | undefined {
  for (const path of paths) {
    let fileText: string;
    try { fileText = readFileSync(path, "utf8"); } catch { continue; }
    const graph = parseFrontendGraph(fileText);
    if (graph) return graph;
  }
  return undefined;
}

/** The row label of a package: its package.json name, else its folder. */
export function dependencyLabel(dependency: DevDependency): string {
  return dependency.package ?? dependency.directory.replace(/[\\/]+$/, "").split(/[\\/]/).at(-1) ?? dependency.directory;
}

/** The state as the view says it. */
export function dependencyStateLabel(dependency: DevDependency): string {
  switch (dependency.state) {
    case "built": return "built";
    case "building": return "building";
    case "failed": return "failed";
    case "missing-output": return "not built";
    case "source": return "source";
    case "not-built": return "build off";
    default: return dependency.state;
  }
}

/** How `dev` keeps it current, or undefined when it does not. */
export function dependencyWatchLabel(dependency: DevDependency): string | undefined {
  switch (dependency.watch) {
    case "script": return "watched by its watch script";
    case "rebuild": return "rebuilt on change";
    case "vite": return "watched by Vite";
    default: return undefined;
  }
}

/** True for a package whose output the page cannot use yet: a failed build, or exports that are missing. */
export function needsBuild(dependency: DevDependency): boolean {
  return dependency.state === "failed" || dependency.state === "missing-output";
}

/** The packages a fresh clone has not built yet, from a list read before or without a session. */
export function unbuiltDependencies(list: DependencyList | undefined): DevDependency[] {
  return list && list.source !== "session" ? list.dependencies.filter(needsBuild) : [];
}

// ---- The fresh clone: a frontend whose packages are not installed ---------------------------------------------------

const lockFiles: readonly [string, string][] = [["bun.lock", "bun"], ["bun.lockb", "bun"], ["pnpm-lock.yaml", "pnpm"], ["package-lock.json", "npm"], ["npm-shrinkwrap.json", "npm"]];

type Manifest = { workspaces?: unknown; packageManager?: unknown; dependencies?: unknown; devDependencies?: unknown };
function readManifest(directory: string): Manifest | undefined {
  try { const value = JSON.parse(readFileSync(join(directory, "package.json"), "utf8")) as unknown; return value && typeof value === "object" ? value as Manifest : undefined; } catch { return undefined; }
}

/** The JavaScript workspace that installs a frontend, as the Views targets find it. */
export interface FrontendInstall {
  /** Where the install runs: the workspace root, or the frontend itself. */
  root: string;
  packageManager: "npm" | "pnpm" | "bun";
  /** The root's lock file, or undefined: then the build stops with RUNICBRIDGE026 and nothing is offered. */
  lockFile?: string;
  /** Whether the frontend or one of its packages declares dependencies and no `node_modules` exists at the root. */
  needed: boolean;
}

/**
 * The nearest folder at or above `frontend` (not above `boundary`) whose package.json has `workspaces` or that has
 * pnpm-workspace.yaml, unless the frontend has a lock file of its own (then it is its own root, as in the targets).
 */
export function workspaceRoot(frontend: string, boundary?: string): string {
  if (lockFiles.some(([name]) => existsSync(join(frontend, name)))) return frontend;
  for (let directory = dirname(frontend); isInside(directory, boundary);) {
    if (readManifest(directory)?.workspaces !== undefined || existsSync(join(directory, "pnpm-workspace.yaml"))) return directory;
    const parent = dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
  return frontend;
}

function isInside(path: string, boundary?: string): boolean {
  if (!boundary) return true;
  const full = resolve(path), base = resolve(boundary).replace(/[\\/]+$/, "");
  return full.startsWith(base) && (full.length === base.length || /^[\\/]/.test(full.slice(base.length)));
}

/**
 * The install of a frontend: its workspace root, the manager (the root's `packageManager`, then the root's lock file,
 * then the frontend's `packageManager`, then npm, as the targets resolve it), and whether it is needed.
 */
export function frontendInstall(frontend: string, boundary?: string): FrontendInstall | undefined {
  const own = readManifest(frontend);
  if (!own) return undefined;
  const root = workspaceRoot(frontend, boundary);
  const rootManifest = readManifest(root) ?? own;
  const lock = lockFiles.find(([name]) => existsSync(join(root, name)));
  const declared = (manifest?: Manifest) => typeof manifest?.packageManager === "string" ? /^(npm|pnpm|bun)@/.exec(manifest.packageManager)?.[1] as FrontendInstall["packageManager"] | undefined : undefined;
  const packageManager = declared(rootManifest) ?? (lock?.[1] as FrontendInstall["packageManager"] | undefined) ?? declared(own) ?? "npm";
  const hasDependencies = [own, rootManifest].some(manifest => [manifest.dependencies, manifest.devDependencies].some(value => value && typeof value === "object" && Object.keys(value).length > 0));
  return { root, packageManager, ...(lock ? { lockFile: join(root, lock[0]) } : {}), needed: hasDependencies && !existsSync(join(root, "node_modules")) };
}

/** The locked install the Views targets run (`RunicApplicationFrontendInstallCommand`'s default), as arguments. */
export function installCommand(packageManager: FrontendInstall["packageManager"]): string[] {
  switch (packageManager) {
    case "bun": return ["bun", "install", "--frozen-lockfile", "--ignore-scripts"];
    case "pnpm": return ["pnpm", "install", "--frozen-lockfile", "--ignore-scripts"];
    default: return ["npm", "ci", "--ignore-scripts"];
  }
}

/**
 * A project's frontend folder as its project file names it: `RunicApplicationFrontendDirectory` (with
 * `$(MSBuildProjectDirectory)` and `$(MSBuildThisFileDirectory)` replaced), else `Frontend` beside the project. A value
 * with another property is not followed.
 */
export function projectFrontendDirectory(project: string, projectText: string): string | undefined {
  const value = /<RunicApplicationFrontendDirectory(?:\s[^>]*)?>([^<]*)<\/RunicApplicationFrontendDirectory>/.exec(projectText)?.[1]?.trim();
  const directory = dirname(project);
  if (!value) return join(directory, "Frontend");
  const expanded = value.replace(/\$\((?:MSBuildProjectDirectory|MSBuildThisFileDirectory)\)[\\/]?/g, `${directory}/`);
  return expanded.includes("$(") ? undefined : resolve(directory, expanded);
}
