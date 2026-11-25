import { existsSync, lstatSync, readFileSync, realpathSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { manifestDirectory, toolManifestsAbove, translationsTools } from "./workspace.js";

export interface ServerLaunch { command: string; args: string[]; cwd: string }
/**
 * Return a canonical, containment-minimal RMF2 watch plan. The opened workspace
 * is the security boundary: nested mounts share its recursive watcher, while
 * external or linked mounts require opening a common containing workspace.
 */
export function sourceWatchRoots(root: string): string[] {
  const workspace = canonicalPath(root);
  const config = [join(workspace, "runic.json"), join(workspace, "translations", "runic.json")]
    .find(path => existsSync(path));
  if (!config) return [workspace];
  let settings: unknown;
  try { settings = JSON.parse(readFileSync(config, "utf8")); } catch { return [workspace]; }
  if (!settings || typeof settings !== "object") return [workspace];
  const mounts = (settings as { sourceRoots?: unknown }).sourceRoots;
  if (!Array.isArray(mounts)) return [workspace];
  const roots = [workspace];
  for (const mount of mounts) {
    if (!mount || typeof mount !== "object" || typeof (mount as { path?: unknown }).path !== "string") continue;
    const lexicalPath = resolve(dirname(config), (mount as { path: string }).path);
    // Watching a symlink would make the host recurse into an untrusted tree;
    // the server will issue the detailed diagnostic for malformed mounts.
    if (hasSymlinkAncestor(lexicalPath)) continue;
    const path = canonicalPath(lexicalPath);
    // Mounted roots are supported only inside the opened workspace. The
    // containing workspace watcher already owns nested roots, so adding a
    // second recursive watcher would duplicate every event.
    if (!isWithin(workspace, path) || roots.some(candidate => isWithin(candidate, path))) continue;
    for (let index = roots.length - 1; index >= 0; index--)
      if (isWithin(path, roots[index])) roots.splice(index, 1);
    roots.push(path);
  }
  return roots.sort();
}

function canonicalPath(path: string): string {
  let current = resolve(path);
  const suffix: string[] = [];
  while (!existsSync(current)) {
    const parent = dirname(current);
    if (parent === current) return resolve(current, ...suffix);
    suffix.unshift(basename(current));
    current = parent;
  }
  return resolve(realpathSync.native(current), ...suffix);
}

function isWithin(root: string, path: string): boolean {
  const candidate = relative(root, path);
  return candidate === "" || (!isAbsolute(candidate) && candidate !== ".." && !candidate.startsWith(`..${sep}`));
}

function hasSymlinkAncestor(path: string): boolean {
  let current = resolve(path);
  while (true) {
    try {
      if (lstatSync(current).isSymbolicLink()) return true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    const parent = dirname(current);
    if (parent === current) return false;
    current = parent;
  }
}

/** Resolve only an explicit development assembly or the workspace's restored local tool. */
export function serverLaunch(root: string, dotnet: string, assembly: string): ServerLaunch {
  return toolLaunch(root, dotnet, assembly, ["lsp"]);
}

/** The project manifest a workspace folder's commands use: `runic.json`, else `translations/runic.json`. */
export function projectManifest(root: string): string | undefined {
  return [join(root, "runic.json"), join(root, "translations", "runic.json")].find(path => existsSync(path));
}

/**
 * The runic.json of the project a file belongs to: the nearest one in its directory or above, up to and including
 * `root` (the workspace folder), as the language server resolves it. Undefined when none is found within `root`.
 */
export function nearestProjectManifest(file: string, root: string): string | undefined {
  const boundary = resolve(root);
  let directory = dirname(resolve(file));
  for (;;) {
    const rel = relative(boundary, directory);
    if (rel.startsWith("..") || isAbsolute(rel)) return undefined;
    const candidate = join(directory, "runic.json");
    if (existsSync(candidate)) return candidate;
    if (directory === boundary) return undefined;
    const parent = dirname(directory);
    if (parent === directory) return undefined;
    directory = parent;
  }
}

/** `runic-translations xliff export` of the project into `output`: one XLIFF 2.1 document per translated locale. */
export function xliffExportArguments(project: string, output: string): string[] {
  return ["xliff", "export", "--project", project, "--output", output];
}

/** `runic-translations xliff import --dry-run`: the per-unit outcome of importing `source`, without writing files. */
export function xliffImportDryRunArguments(project: string, source: string): string[] {
  return ["xliff", "import", "--project", project, "--source", source, "--dry-run"];
}

/** `runic-translations status --format json` for one project: coverage per locale. */
export function statusArguments(project: string): string[] {
  return ["status", "--project", project, "--format", "json"];
}

/** `runic-translations import --from resx|i18next <source>`, first with `--dry-run`; `--overwrite` replaces existing text. */
export function importArguments(project: string, from: "resx" | "i18next", source: string, options: { dryRun: boolean; overwrite?: boolean }): string[] {
  return ["import", "--from", from, source, "--project", project, ...(options.overwrite ? ["--overwrite"] : []), ...(options.dryRun ? ["--dry-run"] : [])];
}

/**
 * The Translations command a tool manifest's text declares: `rax-translations` before `runic-translations` (the rename,
 * D080.7), by the tools' `commands`, or undefined.
 */
export function translationsCommand(manifestText: string): string | undefined {
  let manifest: { tools?: Record<string, { commands?: unknown }> };
  try { manifest = JSON.parse(manifestText); } catch { return undefined; }
  const commands = Object.values(manifest?.tools ?? {}).flatMap(tool => Array.isArray(tool?.commands) ? tool.commands : []);
  return translationsTools.map(tool => tool.command).find(command => commands.includes(command));
}

/** Runs the same local tool as the language server with other arguments (the `xliff`, `status` and `import` commands). */
export function toolLaunch(root: string, dotnet: string, assembly: string, toolArguments: readonly string[]): ServerLaunch {
  if (assembly) {
    const path = resolve(root, assembly);
    if (!existsSync(path)) throw new Error(`Runic server assembly does not exist: ${path}`);
    return { command: dotnet, args: [path, ...toolArguments], cwd: root };
  }
  for (const { path, text } of toolManifestsAbove(root)) {
    const command = translationsCommand(text);
    if (command) return { command: dotnet, args: ["tool", "run", command, "--", ...toolArguments], cwd: manifestDirectory(path) };
  }
  throw new Error("No project-local runic-translations tool was found. Add it to .config/dotnet-tools.json (or dotnet-tools.json) and run dotnet tool restore, or configure runic.translations.serverAssembly for SDK development.");
}
