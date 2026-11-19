import { execFile, spawn, type ChildProcess } from "node:child_process";

/**
 * The variable that asks `dotnet runic dev` (0.22, runic-sdk#1092) to read its standard input and stop as on Ctrl+C at
 * a line `stop` or at the end of the input (WIN-06): it marks the session file `"stopped"`, deletes its frontend
 * marker and development pages, and exits with 130.
 */
export const stopOnInputVariable = "RUNIC_DEV_STOP_ON_INPUT";

/** How long a stop waits for `dev` to stop on its input before it ends the process by force. */
export const inputStopTimeout = 10_000;

const versionPattern = /(?<![\d.])(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?/;

/** The first version in `dotnet runic --version`'s output (`0.22.0`, `0.22.0+abc`, `dotnet-runic 0.22.0`), or undefined. */
export function parseToolVersion(text: string | undefined): string | undefined {
  return versionPattern.exec(text ?? "")?.[0];
}

/**
 * Whether the project's `dotnet-runic` is 0.22 or later (its pre-releases and 1.x too), which has `dev --debug-endpoint`
 * (runic-sdk#1082, D070.17) and the stop request on standard input (runic-sdk#1092). False for an unknown version, so
 * an older or undetected tool runs as before. The same rule as Visual Studio's `DevToolVersions`.
 */
export function supportsDevSdk022(version: string | undefined): boolean {
  const match = versionPattern.exec(version ?? "");
  if (!match) return false;
  return Number(match[1]) > 0 || Number(match[2]) >= 22;
}

/**
 * The arguments of `dotnet runic dev` for a project. `debugEndpoint` adds `--debug-endpoint` (0.22) to a `dev` with its
 * Window, so a CS-WebUI Window opens its DevTools Protocol port because an IDE asked; `--no-window` needs nothing.
 */
export function devArguments(project: string, noWindow: boolean, debugEndpoint = false): string[] {
  return ["runic", "dev", ...(noWindow ? ["--no-window"] : debugEndpoint ? ["--debug-endpoint"] : []), "--project", project];
}

/**
 * The tool manifest's version when it has one, else `<dotnet> runic --version` in the project's directory (within 15
 * seconds); undefined when neither says.
 */
export function detectToolVersion(dotnet: string, cwd: string, manifestVersion: string | undefined): Promise<string | undefined> {
  if (manifestVersion) return Promise.resolve(manifestVersion);
  return new Promise(resolveVersion => execFile(dotnet, ["runic", "--version"], { cwd, timeout: 15_000, windowsHide: true, env: { ...process.env, DOTNET_NOLOGO: "1" } },
    (error, stdout) => resolveVersion(error ? undefined : parseToolVersion(stdout))));
}

/** The part of a child process the stop needs; a `ChildProcess` with a piped standard input, or a test's stand-in. */
export interface StoppableProcess {
  readonly pid?: number;
  readonly exitCode: number | null;
  readonly signalCode: NodeJS.Signals | null;
  readonly stdin: NodeJS.WritableStream | null;
  once(event: "exit", listener: (code: number | null, signal: NodeJS.Signals | null) => void): unknown;
}

export type DevStop = "exited" | "input" | "forced";

export interface StopOptions {
  /** Whether `dev` runs with {@link stopOnInputVariable} and is first asked on its input. */
  stopOnInput: boolean;
  inputTimeout?: number;
  forceTimeout?: number;
  /** The forced stop: on Windows the process tree, elsewhere SIGINT as Ctrl+C; a second call (after `forceTimeout`) is a kill. */
  force(child: StoppableProcess, attempt: 1 | 2): void;
}

/** Whether the process has exited. */
export function hasExited(child: StoppableProcess): boolean {
  return child.exitCode !== null || child.signalCode !== null;
}

/**
 * Stops `dev`: with `stopOnInput`, writes `stop` and closes its standard input, then waits up to `inputTimeout` (10 s)
 * for it to exit; else, or after that, ends it with `force` and waits up to `forceTimeout`, then once more. Resolves with
 * how it ended.
 */
export async function stopDevProcess(child: StoppableProcess, options: StopOptions): Promise<DevStop> {
  if (hasExited(child)) return "exited";
  const exited = new Promise<void>(resolveExit => child.once("exit", () => resolveExit()));
  const within = (ms: number) => new Promise<boolean>(resolveWait => {
    if (hasExited(child)) { resolveWait(true); return; }
    const timer = setTimeout(() => resolveWait(false), ms);
    void exited.then(() => { clearTimeout(timer); resolveWait(true); });
  });
  if (options.stopOnInput && requestStop(child) && await within(options.inputTimeout ?? inputStopTimeout)) return "input";
  if (hasExited(child)) return options.stopOnInput ? "input" : "exited";
  options.force(child, 1);
  if (await within(options.forceTimeout ?? 5000)) return "forced";
  options.force(child, 2);
  await within(5000);
  return "forced";
}

/** Writes the `stop` line and ends the input; false when the pipe is gone. */
function requestStop(child: StoppableProcess): boolean {
  const input = child.stdin;
  if (!input || !input.writable) return false;
  try {
    input.on("error", () => { /* dev closed its input already; the forced stop follows. */ });
    input.write("stop\n");
    input.end();
    return true;
  } catch { return false; }
}

/** The forced stop of today: `taskkill /T /F` on Windows (no console to send Ctrl+C to), SIGINT and then SIGKILL elsewhere. */
export function forceStop(child: StoppableProcess, attempt: 1 | 2, platform: NodeJS.Platform = process.platform): void {
  if (!child.pid) return;
  if (platform === "win32") { execFile("taskkill", ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true }, () => { /* gone already */ }); return; }
  try { process.kill(child.pid, attempt === 1 ? "SIGINT" : "SIGKILL"); } catch { /* gone already */ }
}

/** Starts `dev` with a piped standard input and, with `stopOnInput`, the variable set; without it the variable is removed. */
export function spawnDev(dotnet: string, args: string[], cwd: string, stopOnInput: boolean, env: NodeJS.ProcessEnv = process.env): ChildProcess {
  const childEnv: NodeJS.ProcessEnv = { ...env, DOTNET_NOLOGO: "1" };
  if (stopOnInput) childEnv[stopOnInputVariable] = "1"; else delete childEnv[stopOnInputVariable];
  return spawn(dotnet, args, { cwd, env: childEnv, stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
}

/** How long the output's last lines are read after `dev` exits; processes it left behind can keep its pipes open. */
export const outputDrainTimeout = 2000;

/**
 * Calls `onEnd` once `dev`'s process has exited and its output pipes have closed, or `drainTimeout` after the exit: a
 * frontend server `dev` left behind holds the pipes open, so `close` alone can come much later or never (the 0.22
 * checklist, row 6), and `dev` would count as running until then.
 */
export function onDevEnd(child: Pick<ChildProcess, "once">, onEnd: (code: number | null, signal: NodeJS.Signals | null) => void, drainTimeout = outputDrainTimeout): void {
  let closed = false;
  child.once("close", () => { closed = true; });
  child.once("exit", (code: number | null, signal: NodeJS.Signals | null) => {
    if (closed) { onEnd(code, signal); return; }
    const timer = setTimeout(() => onEnd(code, signal), drainTimeout);
    child.once("close", () => { clearTimeout(timer); onEnd(code, signal); });
  });
}

/** Text for a terminal: bare line feeds become CR LF. */
export function terminalText(text: string): string {
  return text.replace(/\r?\n/g, "\r\n");
}
