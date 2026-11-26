import { existsSync, readFileSync } from "node:fs";
import { dirname, isAbsolute, join, resolve } from "node:path";

/**
 * A `Project` profile of `Properties/launchSettings.json`: the keys `dotnet run`, Visual Studio, Rider and the C#
 * debugger all read, and the optional `rax` object only Runic DevKit reads (design/ide-run-debug §3.2, D084.4).
 */
export interface LaunchProfile {
  name: string;
  commandLineArgs?: string;
  environmentVariables: Record<string, string>;
  workingDirectory?: string;
  rax?: RaxProfileSettings;
}

/** The profile's `rax` object. E1 reads `devArguments`; `dev` and `page` belong to later slices and are only kept. */
export interface RaxProfileSettings { dev?: string; page?: boolean; devArguments?: string[] }

/** `Properties/launchSettings.json` beside a project file. */
export function launchSettingsPath(project: string): string { return join(dirname(project), "Properties", "launchSettings.json"); }

/**
 * The `Project` profiles of a launchSettings.json text, in file order. Like `dotnet run`, it accepts comments and
 * trailing commas, and it skips profiles of another `commandName` (IIS Express, Executable, a custom kind).
 */
export function parseLaunchProfiles(text: string): LaunchProfile[] {
  const settings = JSON.parse(stripJsonComments(text)) as { profiles?: Record<string, unknown> } | null;
  const profiles = settings && typeof settings === "object" && settings.profiles && typeof settings.profiles === "object" ? settings.profiles : {};
  const result: LaunchProfile[] = [];
  for (const [name, value] of Object.entries(profiles)) {
    if (!value || typeof value !== "object") continue;
    const profile = value as Record<string, unknown>;
    if (profile.commandName !== "Project") continue;
    const variables = profile.environmentVariables && typeof profile.environmentVariables === "object" ? profile.environmentVariables as Record<string, unknown> : {};
    result.push({
      name,
      commandLineArgs: typeof profile.commandLineArgs === "string" ? profile.commandLineArgs : undefined,
      environmentVariables: Object.fromEntries(Object.entries(variables).filter(([, item]) => typeof item === "string")) as Record<string, string>,
      workingDirectory: typeof profile.workingDirectory === "string" && profile.workingDirectory.trim() ? profile.workingDirectory : undefined,
      rax: raxSettings(profile.rax),
    });
  }
  return result;
}

function raxSettings(value: unknown): RaxProfileSettings | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const rax = value as Record<string, unknown>;
  return {
    ...(typeof rax.dev === "string" ? { dev: rax.dev } : {}),
    ...(typeof rax.page === "boolean" ? { page: rax.page } : {}),
    ...(Array.isArray(rax.devArguments) ? { devArguments: rax.devArguments.filter((item): item is string => typeof item === "string") } : {}),
  };
}

/** The profiles of a project's `Properties/launchSettings.json` (or `file`); none when there is no file. */
export function readLaunchProfiles(project: string, file = launchSettingsPath(project)): LaunchProfile[] {
  if (!existsSync(file)) return [];
  try { return parseLaunchProfiles(readFileSync(file, "utf8")); }
  catch (error) { throw new Error(`${file} could not be read: ${error instanceof Error ? error.message : String(error)}`); }
}

/**
 * The profile a launch uses (design §3.5): `launchProfile` names one; `null` (or `""`, as the C# debugger's
 * `launchSettingsProfile` reads it) means none; no value means the first `Project` profile, as `dotnet run` and the C#
 * debugger choose. A named profile that is missing or of another kind is an error, not a silent launch without it.
 */
export function selectLaunchProfile(profiles: LaunchProfile[], requested: string | null | undefined, file: string): LaunchProfile | undefined {
  if (requested === null || requested === "") return undefined;
  if (requested === undefined) return profiles[0];
  const profile = profiles.find(item => item.name === requested);
  if (!profile) throw new Error(`The launch profile "${requested}" is not a "Project" profile of ${file}${profiles.length ? ` (it has ${profiles.map(item => `"${item.name}"`).join(", ")})` : ""}.`);
  return profile;
}

/** The profile's working directory, which Visual Studio and Rider resolve against the project folder. */
export function profileWorkingDirectory(project: string, profile: LaunchProfile | undefined): string | undefined {
  const directory = profile?.workingDirectory?.trim();
  if (!directory) return undefined;
  return isAbsolute(directory) ? directory : resolve(dirname(project), directory);
}

/**
 * Splits a command line the way .NET's `ProcessStartInfo.Arguments` turns it into argv, which is how `dotnet run`
 * passes `RunArguments` to the program on every OS (and the Windows C runtime's rule): spaces and tabs separate,
 * double quotes group (and are removed), `""` inside quotes is a literal quote, and backslashes are literal unless
 * they precede a quote (2n backslashes + `"` give n and a quote toggle, 2n+1 give n and a literal quote).
 * So `/config:"C:\Program Files\x.json"` is the one argument `/config:C:\Program Files\x.json`.
 */
export function splitCommandLine(text: string | undefined): string[] {
  const args: string[] = [];
  const value = text ?? "";
  let index = 0;
  while (index < value.length) {
    while (index < value.length && (value[index] === " " || value[index] === "\t")) index++;
    if (index === value.length) break;
    let current = "";
    let quoted = false;
    while (index < value.length) {
      let backslashes = 0;
      while (index < value.length && value[index] === "\\") { index++; backslashes++; }
      if (backslashes > 0) {
        if (index >= value.length || value[index] !== "\"") current += "\\".repeat(backslashes);
        else {
          current += "\\".repeat(backslashes >> 1);
          if (backslashes % 2 !== 0) { current += "\""; index++; }
        }
        continue;
      }
      const character = value[index];
      if (character === "\"") {
        if (quoted && value[index + 1] === "\"") { current += "\""; index++; }
        else quoted = !quoted;
        index++;
        continue;
      }
      if ((character === " " || character === "\t") && !quoted) break;
      current += character;
      index++;
    }
    args.push(current);
  }
  return args;
}

/**
 * A `.env` file as the C# debugger's `envFile` reads it: `NAME=value` lines, `#` comments, an optional `export `, and
 * values in double quotes (with `\n` for a line break) or single quotes.
 */
export function parseEnvFile(text: string): Record<string, string> {
  const result: Record<string, string> = {};
  for (const line of text.split(/\r?\n/)) {
    const match = /^\s*(?:export\s+)?([\w.-]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (!match || line.trimStart().startsWith("#")) continue;
    let value = match[2];
    if (value.length >= 2 && value.startsWith("\"") && value.endsWith("\"")) value = value.slice(1, -1).replace(/\\n/g, "\n");
    else if (value.length >= 2 && value.startsWith("'") && value.endsWith("'")) value = value.slice(1, -1);
    result[match[1]] = value;
  }
  return result;
}

/** Removes line and block comments and trailing commas outside strings, so JSON.parse reads a JSONC text. */
export function stripJsonComments(text: string): string {
  let result = "";
  let index = 0;
  while (index < text.length) {
    const character = text[index];
    if (character === "\"") {
      const start = index++;
      while (index < text.length && text[index] !== "\"") index += text[index] === "\\" ? 2 : 1;
      result += text.slice(start, ++index);
    } else if (character === "/" && text[index + 1] === "/") {
      while (index < text.length && text[index] !== "\n") index++;
    } else if (character === "/" && text[index + 1] === "*") {
      const end = text.indexOf("*/", index + 2);
      index = end < 0 ? text.length : end + 2;
    } else if (character === "," && "}]".includes(nextSignificant(text, index + 1) || "-")) {
      index++;
    } else { result += character; index++; }
  }
  return result;
}

/** The first character from `index` on that is neither whitespace nor in a comment ("" at the end). */
function nextSignificant(text: string, index: number): string {
  for (;;) {
    while (index < text.length && /\s/.test(text[index])) index++;
    if (text.startsWith("//", index)) { const end = text.indexOf("\n", index); index = end < 0 ? text.length : end; }
    else if (text.startsWith("/*", index)) { const end = text.indexOf("*/", index + 2); index = end < 0 ? text.length : end + 2; }
    else return text.charAt(index);
  }
}
