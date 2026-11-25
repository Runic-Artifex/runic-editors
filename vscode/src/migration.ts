import { coreTool, formerToolIds, manifestDirectory, manifestTool } from "./workspace.js";

/**
 * The offer to move a project to the Runic Artifex tool IDs (D079.13, D080.7). `rax migrate` ships with the rename
 * (runic-sdk 0.25, in `RunicArtifex.Tool`), so the offer shows only where that tool is available: a tool manifest lists
 * it, or `dotnet rax --version` / `rax --version` runs. Before the rename ships nothing shows.
 */
export const migrationGuideUri = "https://github.com/Runic-Artifex/runic-editors/blob/main/vscode/README.md#moving-to-the-runic-artifex-tool-ids";

/** The workspace state key that records the offer was shown, so it shows once per workspace. */
export const migrationOfferShownKey = "runic.migrationOffer.shown";

/** How `rax migrate` runs: `dotnet rax …` from a manifest (or a `dotnet rax` that resolves), or a global `rax …`. */
export interface MigrateRunner { directory: string; program: "dotnet" | "rax" }

export interface MigrationOffer {
  /** The tool manifests that list a former ID. */
  manifests: string[];
  /** The former package IDs found, in a stable order. */
  formerIds: string[];
  /** Where and how `rax migrate` runs. */
  runner: MigrateRunner;
  message: string;
}

/** The manifests that list a former ID (`dotnet-runic`, `dotnet-runic-translations`), with the IDs. */
function formerManifests(manifests: readonly { path: string; text: string }[]) {
  return manifests.map(manifest => ({ ...manifest, former: formerToolIds(manifest.text) })).filter(manifest => manifest.former.length);
}

/**
 * Where to look for a `rax` the manifests don't list, or undefined: the folder of the first manifest with a former ID,
 * when no manifest with a former ID also lists `RunicArtifex.Tool`. The caller runs `dotnet rax --version`, then
 * `rax --version` there and passes what resolved to {@link migrationOffer}.
 */
export function migrationProbeDirectory(manifests: readonly { path: string; text: string }[]): string | undefined {
  const found = formerManifests(manifests);
  if (!found.length || found.some(manifest => manifestTool(manifest.text)?.tool === coreTool)) return undefined;
  return manifestDirectory(found[0].path);
}

/**
 * The offer for the tool manifests of a workspace (`path` and `text`), or undefined: when none lists a former ID, or
 * when `RunicArtifex.Tool` is not available, neither in a manifest with a former ID nor as `resolved` (what
 * `dotnet rax --version` or `rax --version` found).
 */
export function migrationOffer(manifests: readonly { path: string; text: string }[], resolved?: MigrateRunner): MigrationOffer | undefined {
  const found = formerManifests(manifests);
  if (!found.length) return undefined;
  const listed = found.find(manifest => manifestTool(manifest.text)?.tool === coreTool);
  const runner: MigrateRunner | undefined = listed ? { directory: manifestDirectory(listed.path), program: "dotnet" } : resolved;
  if (!runner) return undefined;
  const formerIds = ["dotnet-runic", "dotnet-runic-translations"].filter(id => found.some(manifest => manifest.former.includes(id)));
  const names = formerIds.map(id => id === "dotnet-runic" ? "dotnet-runic → RunicArtifex.Tool (rax)" : "dotnet-runic-translations → RunicArtifex.Translations.Tool (rax-translations)");
  return {
    manifests: found.map(manifest => manifest.path),
    formerIds,
    runner,
    message: `This workspace's tool manifest uses the former Runic tool IDs. Runic Artifex renames them: ${names.join("; ")}. rax migrate rewrites the project; DevKit shows its dry run first.`,
  };
}

/** `rax --version` through `program`: what {@link migrationOffer}'s `resolved` probes. */
export function raxVersionArguments(program: MigrateRunner["program"]): string[] {
  return program === "dotnet" ? [coreTool.verb, "--version"] : ["--version"];
}

/** `rax migrate`: first `--dry-run --json` (the report only), then the run that writes; `dotnet rax …` or `rax …`. */
export function migrateArguments(dryRun: boolean, program: MigrateRunner["program"] = "dotnet"): string[] {
  return [...(program === "dotnet" ? [coreTool.verb] : []), "migrate", ...(dryRun ? ["--dry-run"] : []), "--json"];
}

/**
 * A one-line summary of `rax migrate --json`'s report (D079.10: rewritten, moved, and reported but not changed), or of
 * any other output: its counts when the report has those lists, else its last line.
 */
export function migrationSummary(stdout: string): string {
  let report: unknown;
  try { report = JSON.parse(stdout); } catch { report = undefined; }
  const body = report && typeof report === "object" ? ((report as { payload?: unknown }).payload ?? report) as Record<string, unknown> : undefined;
  const counts = body ? (["rewritten", "moved", "reported"] as const).filter(key => Array.isArray(body[key])).map(key => `${(body[key] as unknown[]).length} ${key}`) : [];
  if (counts.length) return counts.join(", ");
  return stdout.trim().split("\n").at(-1)?.trim() || "no report";
}
