import { execFile } from "node:child_process";
import { readFile, stat, utimes, writeFile } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import type { RunicToolId } from "./workspace.js";

/** The session file schema this extension reads; later versions of the schema only add properties. */
export const devSessionSchema = "runic.application.tool.dev-session/1";
/** The same schema under the rename's names (D079, R5 A8), which `rax dev` writes to `obj/rax/`. */
export const raxDevSessionSchema = "rax.application.tool.dev-session/1";

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

/**
 * The session file of a project: `obj/runic/dev-session.json` beside the project file (the default
 * `BaseIntermediateOutputPath`), or `obj/rax/dev-session.json` when the project's core tool is `RunicArtifex.Tool`
 * (R5 A8; `tool` from workspace.ts `projectCoreTool`).
 */
export function devSessionPath(project: string, tool?: RunicToolId): string {
  return join(dirname(project), "obj", tool && !tool.former ? "rax" : "runic", "dev-session.json");
}

/**
 * The stamp that `dotnet runic dev` touches when it stops (runic-sdk `DevSession.StoppedStampName`). Visual Studio's
 * fast up-to-date check compares it with the build's `runic-www.stamp`, so the next build copies the frontend over the
 * development pages that `dev` left in `www/`.
 */
export const devStoppedStampName = "dev-stopped";

/** The `dev-stopped` stamp beside a session file (`obj/runic/dev-stopped`). */
export function devStoppedStampPath(sessionPath: string): string {
  return join(dirname(sessionPath), devStoppedStampName);
}

/**
 * Whether the follower touches `dev-stopped` for this snapshot (Visual Studio: `DevSessions.NeedsStoppedStamp`): only
 * when the file still says starting or ready and its `dev` no longer runs (`exited`; not for a live process or a file
 * that says stopped, whose `dev` touched it), and not when the stamp is already at least as new as the session file
 * (milliseconds since the epoch; an earlier touch for this session, also from another editor or before a reload). The
 * follower adds: at most once per process ID.
 */
export function needsStoppedStamp(snapshot: DevSnapshot, stampWritten?: number, sessionWritten?: number): boolean {
  if (!snapshot.exited || (snapshot.session?.state !== "starting" && snapshot.session?.state !== "ready")) return false;
  return !(stampWritten !== undefined && sessionWritten !== undefined && stampWritten >= sessionWritten);
}

/** Parses a session file; undefined for another schema or a file that is not a session. */
export function parseDevSession(text: string): DevSession | undefined {
  let value: unknown;
  try { value = JSON.parse(text); } catch { return undefined; }
  if (!value || typeof value !== "object") return undefined;
  const session = value as Partial<DevSession>;
  if (session.schema !== devSessionSchema && session.schema !== raxDevSessionSchema) return undefined;
  if (session.state !== "starting" && session.state !== "ready" && session.state !== "stopped") return undefined;
  if (typeof session.processId !== "number" || typeof session.project !== "string") return undefined;
  return session as DevSession;
}

/**
 * The core tool's program in a command line: `dotnet-runic` (its DLL or apphost), or after the rename `rax` (the
 * command, the global tool's shim and, under NativeAOT, the native program itself, R5 A7) and `RunicArtifex.Tool` (its
 * DLL or apphost). `dotnet rax dev` need not run under a `dotnet` parent, so the program alone decides.
 */
const toolProgram = /^(?:dotnet-runic(?:\.dll|\.exe)?|rax(?:\.exe)?|RunicArtifex\.Tool(?:\.dll|\.exe)?)$/i;
/** Windows images of a running `dev`: the `dotnet` host, or the tool's own program (apphost, shim or native). */
const devImages = new Set(["dotnet.exe", "dotnet-runic.exe", "rax.exe", "runicartifex.tool.exe"]);

/** True when `args` (a process's command line) is `dotnet runic dev` or `rax dev`: the tool's program with `dev`. */
export function isDevCommandLine(args: readonly string[]): boolean {
  const tool = args.findIndex(arg => toolProgram.test(basename(arg.replaceAll("\\", "/"))));
  return tool >= 0 && args.slice(tool + 1).includes("dev");
}

/**
 * Whether `pid` is a running `dotnet runic dev`. Linux reads `/proc/<pid>/cmdline` and macOS asks `ps`, so a reused
 * process ID of another program reads as stopped. On Windows the process's image must be `dotnet.exe` or the tool's
 * own program (`dotnet-runic.exe`, `rax.exe`, `RunicArtifex.Tool.exe`; the command line is not checked there). A process that exists but cannot be inspected counts as running.
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
    return image !== undefined && devImages.has(image);
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
  /** A file's last write time in milliseconds, or undefined when it does not exist; without it, the follower touches no stamp. */
  modified?(path: string): Promise<number | undefined>;
  /** Writes the file empty with the current time, as `dev` does when it stops; rejects when it cannot. */
  touch?(path: string): Promise<void>;
}
export const fileSystemSource: DevSessionSource = {
  read: async path => { try { return await readFile(path, "utf8"); } catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined; throw error; } },
  isRunning: pid => isRunningDevProcess(pid),
  modified: async path => { try { return (await stat(path)).mtimeMs; } catch { return undefined; } },
  // As runic-sdk's DevSession.Stop: an empty file, and its time set, since rewriting an empty file need not change it.
  // The directory is not created: without obj/runic (a clean), the next build runs anyway.
  touch: async path => { await writeFile(path, ""); const now = new Date(); await utimes(path, now, now); },
};

/**
 * Follows one project's session file. `refresh()` rereads it (after a file watcher event, and on a timer, because a
 * killed `dev` leaves `"ready"` behind without another write) and applies the liveness rule: `"starting"` or `"ready"`
 * counts only while the top-level `processId` is a running `dotnet runic dev`. `dev` replaces the whole file on each
 * write; a file that does not parse (another schema, or a writer that is not `dev`) keeps the last snapshot. When the
 * file says starting or ready but its `dev` has exited (killed, crashed), it touches `obj/runic/dev-stopped` beside the
 * file once per process ID, as `dev` does on a clean stop ({@link needsStoppedStamp}), so the next build puts the
 * production frontend back in `www/` (runic-editors#29); `log` gets the touch or why it failed.
 */
export class DevSessionFollower {
  private current: DevSnapshot = { state: "none" };
  private revision = 0;
  private readonly marked = new Set<number>();
  private readonly listeners = new Set<(snapshot: DevSnapshot) => void>();
  constructor(readonly project: string, private readonly source: DevSessionSource = fileSystemSource, readonly path = devSessionPath(project),
    private readonly log?: (message: string) => void) {}

  /** `obj/runic/dev-stopped` beside the session file. */
  get stoppedStampPath(): string { return devStoppedStampPath(this.path); }

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
    if (next.exited) await this.markStopped(next);
    // A later refresh started while this one waited; it wins.
    if (revision !== this.revision) return this.current;
    if (snapshotKey(next) !== snapshotKey(this.current)) {
      this.current = next;
      for (const listener of [...this.listeners]) listener(next);
    }
    return this.current;
  }

  /** Touches `dev-stopped` for a killed `dev`, at most once per process ID; logs a failure instead of throwing. */
  private async markStopped(snapshot: DevSnapshot): Promise<void> {
    const source = this.source;
    const pid = snapshot.session?.processId;
    if (!source.modified || !source.touch || pid === undefined || this.marked.has(pid)) return;
    // Recorded before the first await, so two refreshes that both see the dead process touch once.
    this.marked.add(pid);
    const stamp = this.stoppedStampPath;
    const name = basename(this.project);
    try {
      if (!needsStoppedStamp(snapshot, await source.modified(stamp), await source.modified(this.path))) return;
      await source.touch(stamp);
      this.log?.(`${name}: dotnet runic dev (process ${pid}) exited without stopping; touched ${stamp} so that the next build restores www/.`);
    } catch (error) {
      this.log?.(`${name}: dotnet runic dev (process ${pid}) exited without stopping, and ${stamp} could not be touched (${(error as Error).message}); rebuild before the next launch if www/ still has the development pages.`);
    }
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
