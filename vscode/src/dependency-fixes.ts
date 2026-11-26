import { join, relative } from "node:path";

/**
 * The fixes DevKit offers for the frontend workspace and dependency diagnostics of the Views targets and `dotnet runic
 * dev` (runic-sdk 0.24, devtools-library-dx §6): each points at the file to change and offers the change or the command
 * that makes it. Every command runs only on a click (D081.4). They are read from the diagnostic's message, whose wording
 * runic-sdk's `docs/diagnostics.md` documents; a message that does not match gets no fix beyond the catalog link.
 */
export type DependencyFix =
  /** Open a file, at a JSON property when it has one. */
  | { kind: "open"; title: string; file: string; property?: string }
  /** Remove the `packageManager` property of a member's package.json (RUNICBRIDGE089). */
  | { kind: "removePackageManager"; title: string; file: string }
  /** Add a folder to the root package.json's `workspaces` (RUNICBRIDGE087). */
  | { kind: "addWorkspaceMember"; title: string; file: string; member: string }
  /** Run a package manager command in a folder, in a terminal (the lock of RUNICBRIDGE026). */
  | { kind: "run"; title: string; cwd: string; args: string[] }
  /** `dotnet build` of the project, which builds the frontend packages (RUNICBRIDGE088). */
  | { kind: "build"; title: string }
  /** Open a web page (RUNICBRIDGE090: the install page of the missing program). */
  | { kind: "url"; title: string; url: string }
  /** Show the dependency in the Dev Session view, and its package.json when the label names one (RAPPDEV1018). */
  | { kind: "showDependency"; title: string; label?: string };

const manager = "(npm|pnpm|bun)";

/** The install pages of the programs RUNICBRIDGE090 names. */
export const installPages: Record<string, string> = {
  node: "https://nodejs.org/en/download",
  npm: "https://nodejs.org/en/download",
  bun: "https://bun.sh/docs/installation",
  pnpm: "https://pnpm.io/installation",
};

/** The fixes of one diagnostic, by its code and message. */
export function dependencyFixes(code: string, message: string): DependencyFix[] {
  switch (code.toUpperCase()) {
    case "RUNICBRIDGE026": return lockFixes(message);
    case "RUNICBRIDGE087": return memberFixes(message);
    case "RUNICBRIDGE088": return exportFixes(message);
    case "RUNICBRIDGE089": return managerFixes(message);
    case "RUNICBRIDGE090": return programFixes(message);
    case "RAPPDEV1018": return [{ kind: "showDependency", title: "Show the frontend dependencies of dotnet runic dev", ...(dependencyPrefix(message) ? { label: dependencyPrefix(message) } : {}) }];
    default: return [];
  }
}

/** RUNICBRIDGE026: no lock file in the frontend, or none at its workspace root. */
function lockFixes(message: string): DependencyFix[] {
  const workspace = new RegExp(`member of the ${manager} workspace at (.+?), which has no (\\S+?), so`).exec(message);
  const standalone = new RegExp(`^(.+?) declares dependencies but has no (\\S+?), so the frontend install would not be locked\\. Create the lock file with '${manager} install'`).exec(message);
  const [cwd, lock, tool] = workspace ? [workspace[2], workspace[3], workspace[1]] : standalone ? [standalone[1], standalone[2], standalone[3]] : [];
  if (!cwd || !lock || !tool) return [];
  return [
    { kind: "run", title: `Create ${lock}: run '${tool} install --ignore-scripts' in ${cwd}`, cwd, args: [tool, "install", "--ignore-scripts"] },
    { kind: "open", title: `Open ${join(cwd, "package.json")}`, file: join(cwd, "package.json"), property: "dependencies" },
  ];
}

/** RUNICBRIDGE087: the frontend is below a workspace that does not list it. */
function memberFixes(message: string): DependencyFix[] {
  const match = new RegExp(`The frontend (.+?) is below the ${manager} workspace at (.+?), but `).exec(message);
  if (!match) return [];
  const [, frontend, tool, root] = match;
  const member = relative(root, frontend).replaceAll("\\", "/");
  if (tool === "pnpm") return [{ kind: "open", title: `Open ${join(root, "pnpm-workspace.yaml")} to add ${member}`, file: join(root, "pnpm-workspace.yaml"), property: "packages" }];
  const file = join(root, "package.json");
  return [
    { kind: "addWorkspaceMember", title: `Add ${member} to the workspaces of ${file}`, file, member },
    { kind: "open", title: `Open ${file}`, file, property: "workspaces" },
  ];
}

/** RUNICBRIDGE088: a package's exports do not exist, in the consumer's form or the library's own. */
function exportFixes(message: string): DependencyFix[] {
  const consumer = /depends on (.+?) \((.+?)\), (?:the frontend package of .+?|a workspace package that no \.NET project owns), but its package\.json exports (.+?), which do not exist: (.+)$/.exec(message);
  if (consumer) {
    const [, label, directory, , reason] = consumer;
    const file = join(directory, "package.json");
    if (/has no build script/.test(reason))
      return [{ kind: "open", title: `Add a build script to ${label}'s package.json`, file, property: "scripts" }, { kind: "open", title: `Open ${label}'s exports`, file, property: "exports" }];
    return [{ kind: "build", title: "Build frontend dependencies (dotnet build)" }, { kind: "open", title: `Open ${label}'s exports`, file, property: "exports" }];
  }
  const own = /correct exports in (.+?)[\\/]package\.json/.exec(message);
  if (own) return [{ kind: "open", title: `Open the exports of ${join(own[1], "package.json")}`, file: join(own[1], "package.json"), property: "exports" }];
  return [];
}

/** RUNICBRIDGE089: a member declares another package manager than its workspace. */
function managerFixes(message: string): DependencyFix[] {
  const match = new RegExp(`^(.+?[\\\\/]package\\.json) declares (\\S+) \\(packageManager\\), but its workspace (.+?) uses ${manager}`).exec(message);
  if (!match) return [];
  const [, file, , root, tool] = match;
  return [
    { kind: "removePackageManager", title: `Remove packageManager from ${file} (the workspace uses ${tool})`, file },
    { kind: "open", title: `Open the workspace's package.json (${join(root, "package.json")})`, file: join(root, "package.json"), property: "packageManager" },
  ];
}

/** RUNICBRIDGE090: Node.js, Bun or the package manager does not run. */
function programFixes(message: string): DependencyFix[] {
  const runtime = /the build runs '([^']+)' to resolve it/.exec(message)?.[1];
  const tool = runtime ? (/bun/i.test(runtime) ? "bun" : "node") : new RegExp(`uses ${manager}\\b`).exec(message)?.[1];
  if (!tool) return [];
  const name = tool === "node" || tool === "npm" ? "Node.js" : tool === "bun" ? "Bun" : "pnpm";
  return [{ kind: "url", title: `Open the ${name} install page`, url: installPages[tool] }];
}

/** The package label of a RAPPDEV1018 line, `[shared-ui] …`, or undefined. */
export function dependencyPrefix(message: string): string | undefined {
  return /^\s*\[([^\]\s]+)\]/.exec(message)?.[1];
}

// ---- Edits of package.json that keep its formatting ------------------------------------------------------------------

/** A replacement of `text.slice(start, end)`. */
export interface TextChange { start: number; end: number; text: string }

/**
 * The change that removes a top-level string property such as `"packageManager": "npm@10.9.0"` from a package.json's
 * text, with its comma and line, or undefined when the text has none.
 */
export function removeJsonProperty(text: string, name: string): TextChange | undefined {
  const property = new RegExp(`"${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}"\\s*:\\s*"(?:[^"\\\\]|\\\\.)*"`).exec(text);
  if (!property) return undefined;
  let start = property.index, end = start + property[0].length;
  const comma = /^\s*,[ \t]*/.exec(text.slice(end));
  if (comma) {
    end += comma[0].length;
    // Alone on its line: the whole line goes.
    const lineStart = text.lastIndexOf("\n", start - 1) + 1;
    const after = /^[ \t]*\r?\n/.exec(text.slice(end));
    if (/^[ \t]*$/.test(text.slice(lineStart, start)) && after) { start = lineStart; end += after[0].length; }
  } else {
    // The last property: the comma before it goes with it.
    const before = /,\s*$/.exec(text.slice(0, start));
    if (before) start = before.index;
  }
  return { start, end, text: "" };
}

/** The change that adds `member` first in a package.json's `workspaces` array (or `workspaces.packages`), or undefined. */
export function addWorkspaceMember(text: string, member: string): TextChange | undefined {
  const array = /"workspaces"\s*:\s*(?:\{[^{}]*?"packages"\s*:\s*)?\[(\s*)/.exec(text);
  if (!array) return undefined;
  const at = array.index + array[0].length;
  const empty = text[at] === "]";
  return { start: at, end: at, text: `${JSON.stringify(member)}${empty ? "" : `,${array[1] || " "}`}` };
}

/** The 0-based line of a top-level JSON property's key in a file's text, or 0. */
export function propertyLine(text: string, name: string): number {
  const index = text.search(new RegExp(`"${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}"\\s*:`));
  return index < 0 ? 0 : text.slice(0, index).split("\n").length - 1;
}
