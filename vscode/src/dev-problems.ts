import { resolve } from "node:path";

/**
 * `dotnet runic dev`'s output as Problems. Two readers share these patterns: the `$runic-dev` and `$runic-dev-no-window`
 * problem matchers (package.json `problemPatterns`, `problemMatchers`), which a process task (dotnet-runic before 0.22)
 * and the template's tasks.json use, and {@link DevProblems}, which reads the output of the extension's own `dev`
 * terminal (dotnet-runic 0.22 and later). Visual Studio's `DevOutput` and `DevProblems` read it the same way.
 */

/**
 * An MSBuild-form line (runic-sdk `tools/dotnet-runic/README.md`, "Diagnostics"): file, line, column, severity, code,
 * message. `dev` writes a label before each line of a process it runs (`[host] `, `[app] `; runic-sdk `RunningProcess`),
 * which the pattern skips, so that the file is `C:\…\App.cs` and not `[host] C:\…\App.cs`. The file holds no ": " and
 * the optional subcategory before the severity no colon, so a sentence that quotes a located error (`dev`'s RAPPDEV1018
 * text "…, so dev did not start: C:\…\index.ts(12,14): error TS2322: …") is not read as an error in a file named by
 * the sentence; the MSBuild-form line of that failure (`<project> : error RAPPDEV1018: …`) still is one.
 */
export const diagnosticPattern = /^\s*(?:\[[A-Za-z][\w-]*\]\s+)?(?:\d+>)?(\S(?:(?!:\s).)*?)(?:\((\d+)(?:,(\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?:[^\s:]+\s+)?(error|warning)\s+([A-Za-z]+\d+)\s*:\s*(.*?)(?:\s+\[[^\]]+\])?$/;

/** The lines that begin a build cycle: with the Window, and with `--no-window` (the template's tasks.json). */
export const beginsPattern = /^\[dev\] (Evaluating|Window exited|Reloading)/;
export const beginsNoWindowPattern = /^\[dev\] Evaluating/;
/** `[dev] Ready`, which `dev` prints only after a build that succeeded. */
export const readyPattern = /^\[dev\] Ready$/;
/**
 * `[vite] [runic] Frontend errors cleared` (runic-sdk 0.23, runic-sdk#1212): each module that failed compiles again, so
 * the {@link frontendErrorCode} problems go.
 */
export const frontendClearedPattern = /^\[vite\] \[runic\] Frontend errors cleared$/;
/** The code of a frontend (Vite) error, which `dev` prints in MSBuild form after its `[vite] ` label (runic-sdk 0.23). */
export const frontendErrorCode = "RUNICP008";

const isFrontendError = (problem: DevProblem) => problem.code.toUpperCase() === frontendErrorCode;

// eslint-disable-next-line no-control-regex
const escapePattern = /\x1B\[[0-9;?]*[ -/]*[@-~]/g;

export interface DevProblem {
  /** The absolute file; a relative one resolves against `dev`'s working directory, the project's. */
  file: string;
  /** 1-based, as MSBuild writes them; 1 when the line has none. */
  line: number;
  column: number;
  severity: "error" | "warning";
  code: string;
  message: string;
}

/** The line without terminal colour codes or a trailing carriage return. */
export function plainLine(line: string): string {
  return line.replace(escapePattern, "").replace(/\r$/, "");
}

/** Whether the file is absolute on Windows or POSIX, whichever OS reads it. */
function isRooted(file: string): boolean {
  return file.startsWith("/") || file.startsWith("\\\\") || /^[A-Za-z]:[\\/]/.test(file);
}

/** The label `dev` writes before a line of a process it runs: `host`, `app`, `vite`, or a package's folder (`shared-ui`). */
const labelPattern = /^\s*\[([A-Za-z][\w-]*)\]\s+/;

/** The source files of a frontend package's build tool (tsc, Vite), as runic-sdk's `FrontendDependencies` lists them. */
const frontendSource = /\.(?:[cm]?[jt]sx?|svelte|vue|s?css)$/i;

/**
 * The problem of one output line, or undefined. A relative file resolves against `directory`, the project's (MSBuild's
 * relative C# paths), except a frontend source: a package's build tool writes it relative to the package, so it resolves
 * against the package that `packageDirectory` finds for the line's label (`[shared-ui] src/x.ts(3,1): error TS1005`, a
 * watcher's output), and without one the line is left out. That is tsc's own echo in the build's output, which the
 * Views targets report again with the full path (runic-sdk `FrontendDependencies.FindPackageFailure`), so the one
 * error is not listed a second time in a file of the Window project that does not exist.
 */
export function parseDevProblem(line: string, directory: string, packageDirectory?: (label: string) => string | undefined): DevProblem | undefined {
  const plain = plainLine(line);
  const match = diagnosticPattern.exec(plain);
  if (!match) return undefined;
  const file = match[1].trim();
  let base = directory;
  if (!isRooted(file) && frontendSource.test(file)) {
    const label = labelPattern.exec(plain)?.[1];
    const packageBase = label ? packageDirectory?.(label) : undefined;
    if (!packageBase) return undefined;
    base = packageBase;
  }
  return {
    file: isRooted(file) ? file : resolve(base, file),
    line: match[2] ? Number(match[2]) : 1,
    column: match[3] ? Number(match[3]) : 1,
    severity: match[4] as "error" | "warning",
    code: match[5],
    message: match[6],
  };
}

/**
 * The Problems of one running `dotnet runic dev`: {@link add} each output line; it returns true when the problems changed.
 * A new build cycle replaces the previous cycle's problems, as a background matcher does. `[dev] Ready` also drops the
 * errors read before it, which a background matcher cannot: after a failed build `dotnet watch` waits for a change,
 * rebuilds and starts the Window again without a new `[dev] Window exited`, so the fixed build's cycle is the failed
 * one's, and a matcher keeps every problem it read in a cycle. Warnings stay until the next cycle. Frontend errors
 * ({@link frontendErrorCode}, after `dev`'s `[vite] ` label) stay until `[vite] [runic] Frontend errors cleared` or the
 * next cycle (runic-editors#52); the problem matchers of a process task keep them until the next cycle.
 */
export class DevProblems {
  private items: DevProblem[] = [];
  private readonly seen = new Set<string>();

  /** `packageDirectory` finds the folder of the frontend package whose label a line has (`[shared-ui]`), see {@link parseDevProblem}. */
  constructor(private readonly directory: string, private readonly noWindow: boolean, private readonly packageDirectory?: (label: string) => string | undefined) {}

  get problems(): readonly DevProblem[] { return this.items; }

  add(rawLine: string): boolean {
    const line = plainLine(rawLine);
    if ((this.noWindow ? beginsNoWindowPattern : beginsPattern).test(line)) {
      if (this.items.length === 0) return false;
      this.items = []; this.seen.clear();
      return true;
    }
    // A .NET build that succeeded fixes the build errors, not the frontend's.
    if (readyPattern.test(line.trimEnd())) return this.keep(item => item.severity !== "error" || isFrontendError(item));
    if (frontendClearedPattern.test(line.trimEnd())) return this.keep(item => !isFrontendError(item));
    const problem = parseDevProblem(line, this.directory, this.packageDirectory);
    // MSBuild repeats a build's diagnostics in its summary; one problem each.
    if (!problem || this.seen.has(key(problem))) return false;
    this.seen.add(key(problem));
    this.items.push(problem);
    return true;
  }

  /** Keeps the problems that match; true when that dropped any. */
  private keep(match: (problem: DevProblem) => boolean): boolean {
    const kept = this.items.filter(match);
    if (kept.length === this.items.length) return false;
    this.items = kept;
    this.seen.clear();
    for (const item of kept) this.seen.add(key(item));
    return true;
  }
}

function key(problem: DevProblem): string {
  return `${problem.severity}|${problem.file}|${problem.line}|${problem.column}|${problem.code}|${problem.message}`;
}

/** Splits a process's output chunks into lines; a line without its newline yet waits for the next chunk. */
export class LineSplitter {
  private rest = "";

  push(chunk: string): string[] {
    const lines = (this.rest + chunk).split("\n");
    this.rest = lines.pop() ?? "";
    return lines;
  }

  /** The last line, at the end of the output. */
  flush(): string[] {
    const rest = this.rest; this.rest = "";
    return rest ? [rest] : [];
  }
}
