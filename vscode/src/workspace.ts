import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";

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

/** The `dotnet-runic` version a tool manifest (`.config/dotnet-tools.json`) pins, or undefined. */
export function runicToolVersion(manifestText: string): string | undefined {
  try {
    const manifest = JSON.parse(manifestText) as { tools?: Record<string, { version?: unknown; commands?: unknown }> };
    const tool = manifest.tools?.["dotnet-runic"];
    return tool && typeof tool.version === "string" ? tool.version : undefined;
  } catch { return undefined; }
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

/** The `dotnet-runic` version of the nearest tool manifest at or above the project's directory, if any. */
export function projectRunicToolVersion(project: string): string | undefined {
  let directory = dirname(project);
  for (;;) {
    const manifest = join(directory, ".config", "dotnet-tools.json");
    if (existsSync(manifest)) {
      let text = "";
      try { text = readFileSync(manifest, "utf8"); } catch { /* Unreadable: keep looking up. */ }
      const version = runicToolVersion(text);
      if (version) return version;
      try { if ((JSON.parse(text) as { isRoot?: boolean }).isRoot) return undefined; } catch { /* Not JSON. */ }
    }
    const parent = dirname(directory);
    if (parent === directory) return undefined;
    directory = parent;
  }
}
