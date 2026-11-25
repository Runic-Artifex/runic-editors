import { coreTool, formerToolIds, manifestDirectory, manifestTool } from "./workspace.js";

/**
 * The offer to move a project to the Runic Artifex tool IDs (D079.13, D080.7). `rax migrate` ships with the rename
 * (runic-sdk 0.25, in `RunicArtifex.Tool`). DevKit runs it only where the workspace's own tool has it (a manifest that
 * already lists `RunicArtifex.Tool`); otherwise the offer links the migration guide.
 */
export const migrationGuideUri = "https://github.com/Runic-Artifex/runic-editors/blob/main/vscode/README.md#moving-to-the-runic-artifex-tool-ids";

/** The workspace state key that records the offer was shown, so it shows once per workspace. */
export const migrationOfferShownKey = "runic.migrationOffer.shown";

export interface MigrationOffer {
  /** The tool manifests that list a former ID. */
  manifests: string[];
  /** The former package IDs found, in a stable order. */
  formerIds: string[];
  /** The directory to run `dotnet rax migrate` in, when a manifest with a former ID also lists `RunicArtifex.Tool`. */
  migrateDirectory?: string;
  message: string;
}

/**
 * The offer for the tool manifests of a workspace (`path` and `text`), or undefined when none lists a former ID
 * (`dotnet-runic`, `dotnet-runic-translations`).
 */
export function migrationOffer(manifests: readonly { path: string; text: string }[]): MigrationOffer | undefined {
  const found = manifests.map(manifest => ({ ...manifest, former: formerToolIds(manifest.text) })).filter(manifest => manifest.former.length);
  if (!found.length) return undefined;
  const formerIds = ["dotnet-runic", "dotnet-runic-translations"].filter(id => found.some(manifest => manifest.former.includes(id)));
  const runnable = found.find(manifest => manifestTool(manifest.text)?.tool === coreTool);
  const names = formerIds.map(id => id === "dotnet-runic" ? "dotnet-runic → RunicArtifex.Tool (rax)" : "dotnet-runic-translations → RunicArtifex.Translations.Tool (rax-translations)");
  return {
    manifests: found.map(manifest => manifest.path),
    formerIds,
    ...(runnable ? { migrateDirectory: manifestDirectory(runnable.path) } : {}),
    message: `This workspace's tool manifest uses the former Runic tool IDs. Runic Artifex renames them: ${names.join("; ")}. `
      + (runnable ? "rax migrate rewrites the project; DevKit shows its dry run first." : "rax migrate, which ships with the rename, rewrites the project; the migration guide has the steps."),
  };
}

/** `dotnet rax migrate`: first `--dry-run --json` (the report only), then the run that writes. */
export function migrateArguments(dryRun: boolean): string[] {
  return [coreTool.verb, "migrate", ...(dryRun ? ["--dry-run"] : []), "--json"];
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
