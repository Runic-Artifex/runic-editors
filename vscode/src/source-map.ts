import { readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";

/**
 * The development source map (`runic-bridge-sources/1`) that the Bridge generator writes to
 * `obj/<Configuration>/<TargetFramework>/runic-bridge/sources.json` (Runic.Application.Views README, "Development source map").
 */
export interface SourceLocation { file: string; line: number; column: number }
export interface SourceViewModel { id: string; name: string; route?: string; contract?: string; pageKinds?: string[]; symbol?: string; location?: SourceLocation }
export type SourceMemberKind = "state" | "setter" | "command" | "commandState" | "interaction" | "request";
export interface SourceMember { id: string; viewModel: string; member: string; kind: SourceMemberKind | string; symbol?: string; property?: string; location?: SourceLocation }
export interface SourceMap { format: string; viewModels: SourceViewModel[]; members: SourceMember[] }

export const sourceMapFormat = "runic-bridge-sources/1";

export function parseSourceMap(text: string): SourceMap {
  const map = JSON.parse(text) as Partial<SourceMap>;
  if (map.format !== sourceMapFormat) throw new Error(`Unsupported development source map format ${JSON.stringify(map.format)}; this extension reads ${sourceMapFormat}.`);
  if (!Array.isArray(map.viewModels) || !Array.isArray(map.members)) throw new Error("The development source map has no viewModels or members.");
  return map as SourceMap;
}

/**
 * The newest `runic-bridge/sources.json` under the project's `obj` (`obj/<Configuration>/<TargetFramework>/`), as the Vite
 * plugin picks it; undefined before the first build.
 */
export function newestSourceMap(project: string): string | undefined {
  const obj = join(dirname(project), "obj");
  let newest: { path: string; time: number } | undefined;
  const consider = (path: string) => {
    try { const time = statSync(path).mtimeMs; if (!newest || time > newest.time) newest = { path, time }; } catch { /* Not there. */ }
  };
  const directories = (path: string) => { try { return readdirSync(path, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => join(path, entry.name)); } catch { return []; } };
  for (const configuration of directories(obj))
    for (const framework of directories(configuration)) consider(join(framework, "runic-bridge", "sources.json"));
  return newest?.path;
}

/** One node of the ViewModels view: a ViewModel with its members, or a member. */
export interface SourceTreeNode {
  id: string; label: string; description?: string; tooltip: string; kind: "viewModel" | SourceMember["kind"];
  location?: SourceLocation; children: SourceTreeNode[];
}

const kindOrder: Record<string, number> = { state: 0, setter: 1, commandState: 2, command: 3, interaction: 4, request: 5 };
const kindLabel: Record<string, string> = { state: "state", setter: "setter", commandState: "command state", command: "command", interaction: "interaction", request: "View request" };

/**
 * The ViewModels tree: every ViewModel (by name, with its route) and below it its members in client order (state,
 * setters, command state, commands, interactions, View requests), each opening its C# location. Members whose ViewModel
 * the map does not list are kept under a ViewModel node of their own.
 */
export function sourceMapTree(map: SourceMap): SourceTreeNode[] {
  const nodes = new Map<string, SourceTreeNode>();
  for (const viewModel of map.viewModels) {
    nodes.set(viewModel.id, {
      id: viewModel.id, label: viewModel.name || viewModel.id, kind: "viewModel", location: viewModel.location, children: [],
      description: viewModel.route ? `route ${viewModel.route}` : undefined,
      tooltip: [viewModel.symbol ?? viewModel.id, viewModel.location ? locationText(viewModel.location) : "no source location (build with a PDB)"].join("\n"),
    });
  }
  for (const member of map.members) {
    let parent = nodes.get(member.viewModel);
    if (!parent) nodes.set(member.viewModel, parent = { id: member.viewModel, label: member.viewModel, kind: "viewModel", children: [], tooltip: member.viewModel });
    parent.children.push({
      id: member.id, label: member.member, kind: member.kind, location: member.location, children: [],
      description: kindLabel[member.kind] ?? member.kind,
      tooltip: [member.symbol ?? member.id, member.property && member.property !== member.symbol ? `via ${member.property}` : undefined,
        member.location ? locationText(member.location) : "no source location (build with a PDB)"].filter(Boolean).join("\n"),
    });
  }
  const tree = [...nodes.values()].sort((a, b) => a.label.localeCompare(b.label));
  for (const node of tree) node.children.sort((a, b) => (kindOrder[a.kind] ?? 9) - (kindOrder[b.kind] ?? 9) || a.label.localeCompare(b.label));
  return tree;
}

export function locationText(location: SourceLocation): string {
  return `${location.file}:${location.line}:${location.column}`;
}
