import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { basename, dirname, join } from "node:path";

/** The session file schema this extension reads; later versions of the schema only add properties. */
export const devSessionSchema = "runic.application.tool.dev-session/1";

/** `obj/runic/dev-session.json`, which `dotnet runic dev` writes (tools/dotnet-runic/README.md, "Session file"). */
export interface DevSession {
  schema: string;
  state: "starting" | "ready" | "stopped";
  processId: number;
  startedAt?: string;
  project: string;
  configuration?: string;
  frontend?: {
    developmentServer?: "vite" | "angular" | null; url?: string | null; port?: number | null; stateUrl?: string | null;
    directory?: string; outputDirectory?: string; generatedDirectory?: string | null;
  };
  webRoot?: string;
  pages?: { document: string; file?: string; url?: string | null }[];
  window?: { owner?: "dev" | "ide"; program?: string; processId?: number | null; starts?: number; environment?: Record<string, string> };
  debugEndpoints?: { engine: string; protocol: string; url: string }[];
}

/**
 * What the extension shows for one project: `none` without a session file; `stopped` when the file says so or when its
 * `dotnet runic dev` process no longer runs (`exited`: the file still says starting or ready, as after a killed `dev`).
 */
export type DevState = "none" | "starting" | "ready" | "stopped";
export interface DevSnapshot { state: DevState; session?: DevSession; exited?: boolean }

/** The session file of a project: `obj/runic/dev-session.json` beside the project file (the default `BaseIntermediateOutputPath`). */
export function devSessionPath(project: string): string {
  return join(dirname(project), "obj", "runic", "dev-session.json");
}

/** Parses a session file; undefined for another schema or a file that is not a session. */
export function parseDevSession(text: string): DevSession | undefined {
  let value: unknown;
  try { value = JSON.parse(text); } catch { return undefined; }
  if (!value || typeof value !== "object") return undefined;
  const session = value as Partial<DevSession>;
  if (session.schema !== devSessionSchema) return undefined;
  if (session.state !== "starting" && session.state !== "ready" && session.state !== "stopped") return undefined;
  if (typeof session.processId !== "number" || typeof session.project !== "string") return undefined;
  return session as DevSession;
}

/** True when `args` (a process's command line) is `dotnet runic dev`: the tool (`dotnet-runic`, its DLL or apphost) with `dev`. */
export function isDevCommandLine(args: readonly string[]): boolean {
  const tool = args.findIndex(arg => /^dotnet-runic(?:\.dll|\.exe)?$/i.test(basename(arg.replaceAll("\\", "/"))));
  return tool >= 0 && args.slice(tool + 1).includes("dev");
}

/**
 * Whether `pid` is a running `dotnet runic dev`. Linux reads `/proc/<pid>/cmdline` and macOS asks `ps`, so a reused
 * process ID of another program reads as stopped. On Windows the process's image must be `dotnet.exe` or
 * `dotnet-runic.exe` (the command line is not checked there). A process that exists but cannot be inspected counts as running.
 */
export async function isRunningDevProcess(pid: number, platform: NodeJS.Platform = process.platform): Promise<boolean> {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); } catch (error) { if ((error as NodeJS.ErrnoException).code !== "EPERM") return false; }
  if (platform === "linux") {
    try { return isDevCommandLine((await readFile(`/proc/${pid}/cmdline`, "utf8")).split("\0").filter(Boolean)); }
    catch (error) { return (error as NodeJS.ErrnoException).code !== "ENOENT"; }
  }
  if (platform === "darwin") {
    const args = await run("ps", ["-o", "args=", "-p", String(pid)]);
    return args === undefined ? true : args.trim() !== "" && isDevCommandLine(args.trim().split(/\s+/));
  }
  if (platform === "win32") {
    const line = await run("tasklist", ["/FI", `PID eq ${pid}`, "/FO", "CSV", "/NH"]);
    if (line === undefined) return true;
    const image = /^"([^"]+)"/.exec(line.trim())?.[1]?.toLowerCase();
    return image === "dotnet.exe" || image === "dotnet-runic.exe";
  }
  return true;
}

function run(command: string, args: string[]): Promise<string | undefined> {
  return new Promise(resolveRun => execFile(command, args, { timeout: 5000, windowsHide: true }, (error, stdout) => resolveRun(error && !stdout ? undefined : stdout)));
}

export interface DevSessionSource {
  /** The file's text, or undefined when it does not exist. */
  read(path: string): Promise<string | undefined>;
  /** Whether a process ID is a running `dotnet runic dev`. */
  isRunning(pid: number): Promise<boolean>;
}
export const fileSystemSource: DevSessionSource = {
  read: async path => { try { return await readFile(path, "utf8"); } catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined; throw error; } },
  isRunning: pid => isRunningDevProcess(pid),
};

/**
 * Follows one project's session file. `refresh()` rereads it (after a file watcher event, and on a timer, because a
 * killed `dev` leaves `"ready"` behind without another write) and applies the liveness rule: `"starting"` or `"ready"`
 * counts only while the top-level `processId` is a running `dotnet runic dev`. `dev` replaces the whole file on each
 * write; a file that does not parse (another schema, or a writer that is not `dev`) keeps the last snapshot.
 */
export class DevSessionFollower {
  private current: DevSnapshot = { state: "none" };
  private revision = 0;
  private readonly listeners = new Set<(snapshot: DevSnapshot) => void>();
  constructor(readonly project: string, private readonly source: DevSessionSource = fileSystemSource, readonly path = devSessionPath(project)) {}

  get snapshot(): DevSnapshot { return this.current; }

  onDidChange(listener: (snapshot: DevSnapshot) => void): { dispose(): void } {
    this.listeners.add(listener);
    return { dispose: () => { this.listeners.delete(listener); } };
  }

  /** Rereads the file; resolves to the new snapshot and notifies listeners when the shown state changed. */
  async refresh(): Promise<DevSnapshot> {
    const revision = ++this.revision;
    let next: DevSnapshot;
    let text: string | undefined;
    try { text = await this.source.read(this.path); } catch { return this.current; }
    if (text === undefined) next = { state: "none" };
    else {
      const session = parseDevSession(text);
      if (!session) return this.current;
      if (session.state === "stopped") next = { state: "stopped", session };
      else next = await this.source.isRunning(session.processId) ? { state: session.state, session } : { state: "stopped", session, exited: true };
    }
    // A later refresh started while this one waited; it wins.
    if (revision !== this.revision) return this.current;
    if (snapshotKey(next) !== snapshotKey(this.current)) {
      this.current = next;
      for (const listener of [...this.listeners]) listener(next);
    }
    return this.current;
  }
}

function snapshotKey(snapshot: DevSnapshot): string {
  return JSON.stringify([snapshot.state, snapshot.exited ?? false, snapshot.session ?? null]);
}

/** One line for the status bar and the Dev Session view. */
export function devStateLabel(snapshot: DevSnapshot): string {
  switch (snapshot.state) {
    case "none": return "not started";
    case "starting": return "starting";
    case "ready": return snapshot.session?.window?.owner === "ide" ? "ready (Window in the IDE)" : "ready";
    case "stopped": return snapshot.exited ? `stopped (process ${snapshot.session?.processId} exited)` : "stopped";
  }
}

/** Whether a dev session runs (the liveness rule applied): starting or ready. */
export function isActive(snapshot: DevSnapshot): boolean {
  return snapshot.state === "starting" || snapshot.state === "ready";
}
