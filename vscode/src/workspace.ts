import { existsSync, readdirSync, readFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";

/**
 * What a workspace folder holds: Runic SDK projects (a `.csproj` of a Runic Views Window or one that references a Runic
 * SDK package) and Translations projects (`runic.json`). The Runic SDK views show only for the first; a Translations-only
 * workspace gets the same extension without them.
 */
export interface WorkspaceKind { sdkProjects: string[]; translationsProjects: string[] }

/** Directories never searched: build output, package installs and version control. */
const skipped = new Set(["bin", "obj", "node_modules", "artifacts", "dist", "www"]);
/** How deep below the folder projects are searched; a solution's projects sit one or two levels down. */
export const searchDepth = 4;

/**
 * True for a project file of the Runic SDK: a Window project (`RunicApplicationFrontendWindowProject`), or one that
 * references a `Runic.Application.*`, `Runic.Desktop*` or `Runic.Platform.*` package or project. A project that references
 * only Runic Translations packages (`Runic.Translations*`) is not one.
 */
export function isRunicSdkProject(projectText: string): boolean {
  if (/<RunicApplicationFrontendWindowProject>\s*true\s*</i.test(projectText)) return true;
  return /<(?:PackageReference|ProjectReference)\b[^>]*\bInclude\s*=\s*"[^"]*\bRunic\.(?:Application|Desktop|Platform)\b[^"]*"/i.test(projectText);
}

/**
 * A Runic tool as a tool manifest names it. The rename (D079, D080.7) gives the core tool the ID `RunicArtifex.Tool` and
 * the command `rax` (`dotnet rax` from a manifest) in place of `dotnet-runic` (`dotnet runic`), and the Translations
 * tool `RunicArtifex.Translations.Tool` / `rax-translations` in place of `dotnet-runic-translations` /
 * `runic-translations`. DevKit accepts both and decides by what the manifest contains; the new ID wins when a manifest
 * lists both (mid-migration).
 */
export interface RunicToolId {
  /** The NuGet package ID (the manifest's key; NuGet IDs are case-insensitive). */
  readonly id: string;
  /** The tool's command. */
  readonly command: string;
  /** What follows `dotnet` to run it from a manifest: `dotnet runic …`, `dotnet rax …`. */
  readonly verb: string;
  /** True for the IDs before the rename, which `rax migrate` rewrites. */
  readonly former: boolean;
}

export const coreTool: RunicToolId = { id: "RunicArtifex.Tool", command: "rax", verb: "rax", former: false };
export const formerCoreTool: RunicToolId = { id: "dotnet-runic", command: "dotnet-runic", verb: "runic", former: true };
export const translationsTool: RunicToolId = { id: "RunicArtifex.Translations.Tool", command: "rax-translations", verb: "rax-translations", former: false };
export const formerTranslationsTool: RunicToolId = { id: "dotnet-runic-translations", command: "runic-translations", verb: "runic-translations", former: true };
/** Newest first: the order in which a manifest's tools are looked up. */
export const coreTools: readonly RunicToolId[] = [coreTool, formerCoreTool];
export const translationsTools: readonly RunicToolId[] = [translationsTool, formerTranslationsTool];

/**
 * The tool manifests of one directory in the order .NET reads them: `.config/dotnet-tools.json`, then
 * `dotnet-tools.json` at the root (which `dotnet new tool-manifest` writes since .NET 10).
 */
export const toolManifestNames: readonly string[] = [join(".config", "dotnet-tools.json"), "dotnet-tools.json"];

/** The directory a tool manifest belongs to: the parent of `.config` for `.config/dotnet-tools.json`. */
export function manifestDirectory(manifest: string): string {
  const directory = dirname(manifest);
  return basename(directory) === ".config" ? dirname(directory) : directory;
}

/** A Runic tool a manifest lists: which one, and its pinned version when the manifest has one. */
export interface ManifestTool { tool: RunicToolId; version?: string; manifest?: string }

type ManifestJson = { isRoot?: unknown; tools?: Record<string, { version?: unknown; commands?: unknown }> };
function parseManifest(text: string): ManifestJson | undefined {
  try { const value = JSON.parse(text) as unknown; return value && typeof value === "object" ? value as ManifestJson : undefined; } catch { return undefined; }
}
function manifestEntry(manifest: ManifestJson | undefined, id: string) {
  const key = Object.keys(manifest?.tools ?? {}).find(name => name.toLowerCase() === id.toLowerCase());
  const entry = key === undefined ? undefined : manifest!.tools![key];
  return entry && typeof entry === "object" ? entry : undefined;
}

/** The first of `tools` that a manifest's text lists (by package ID, any case), with its version. */
export function manifestTool(manifestText: string, tools: readonly RunicToolId[] = coreTools): ManifestTool | undefined {
  const manifest = parseManifest(manifestText);
  for (const tool of tools) {
    const entry = manifestEntry(manifest, tool.id);
    if (entry) return { tool, ...(typeof entry.version === "string" ? { version: entry.version } : {}) };
  }
  return undefined;
}

/** The core tool version a tool manifest pins (`RunicArtifex.Tool` or `dotnet-runic`), or undefined. */
export function runicToolVersion(manifestText: string): string | undefined {
  return manifestTool(manifestText)?.version;
}

/** The Runic tools before the rename that a manifest's text lists, as their package IDs. */
export function formerToolIds(manifestText: string): string[] {
  const manifest = parseManifest(manifestText);
  return [formerCoreTool, formerTranslationsTool].filter(tool => manifestEntry(manifest, tool.id)).map(tool => tool.id);
}

/** Whether a manifest's text is the last one .NET reads (`"isRoot": true`). */
export function isRootManifest(manifestText: string): boolean {
  return parseManifest(manifestText)?.isRoot === true;
}

/** The tool manifests from `directory` up to the first root manifest or the file system's root, nearest first. */
export function* toolManifestsAbove(directory: string): Generator<{ path: string; text: string }> {
  for (;;) {
    let root = false;
    for (const name of toolManifestNames) {
      const path = join(directory, name);
      if (!existsSync(path)) continue;
      let text = "";
      try { text = readFileSync(path, "utf8"); } catch { /* Unreadable: keep looking. */ }
      yield { path, text };
      root ||= isRootManifest(text);
    }
    const parent = dirname(directory);
    if (root || parent === directory) return;
    directory = parent;
  }
}

/** The core tool of the nearest tool manifest at or above the project's directory that lists one, if any. */
export function projectCoreTool(project: string): ManifestTool | undefined {
  for (const { path, text } of toolManifestsAbove(dirname(project))) {
    const found = manifestTool(text);
    if (found) return { ...found, manifest: path };
  }
  return undefined;
}

/** Finds the Runic SDK and Translations projects below `root` (at most `searchDepth` levels), sorted. */
export function detectWorkspace(root: string, depth = searchDepth): WorkspaceKind {
  const sdkProjects: string[] = [];
  const translationsProjects: string[] = [];
  const visit = (directory: string, level: number) => {
    let entries;
    try { entries = readdirSync(directory, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const path = join(directory, entry.name);
      if (entry.isFile()) {
        if (entry.name.endsWith(".csproj")) {
          let text = "";
          try { text = readFileSync(path, "utf8"); } catch { /* An unreadable project is not one. */ }
          if (isRunicSdkProject(text)) sdkProjects.push(path);
        } else if (entry.name === "runic.json") translationsProjects.push(path);
      } else if (entry.isDirectory() && level < depth && !entry.name.startsWith(".") && !skipped.has(entry.name)) {
        visit(path, level + 1);
      }
    }
  };
  visit(root, 0);
  return { sdkProjects: sdkProjects.sort(), translationsProjects: translationsProjects.sort() };
}

/** The core tool version of the nearest tool manifest at or above the project's directory, if any. */
export function projectRunicToolVersion(project: string): string | undefined {
  return projectCoreTool(project)?.version;
}
