import { basename, dirname, isAbsolute, resolve } from "node:path";
import type { DevSession } from "./dev-session.js";
import { profileWorkingDirectory, splitCommandLine, type LaunchProfile } from "./launch-settings.js";

/** `dotnet msbuild <project> -getProperty:…` for the program the Window runs as, the way `dotnet run` starts it. */
export function runPropertiesArguments(project: string, configuration = "Debug"): string[] {
  return ["msbuild", project, `-p:Configuration=${configuration}`, "-getProperty:RunCommand", "-getProperty:RunArguments", "-getProperty:RunWorkingDirectory", "-getProperty:TargetPath"];
}

export interface RunProperties { RunCommand?: string; RunArguments?: string; RunWorkingDirectory?: string; TargetPath?: string }

/** `dotnet msbuild -getProperty` prints the value alone for one property and `{ "Properties": { … } }` for several. */
export function parseRunProperties(stdout: string): RunProperties {
  const value = JSON.parse(stdout) as { Properties?: RunProperties };
  if (!value.Properties) throw new Error("dotnet msbuild returned no properties.");
  return value.Properties;
}

/** Splits MSBuild's `RunArguments` into argv as `dotnet run` does (.NET's `ProcessStartInfo.Arguments` rules). */
export const splitArguments = splitCommandLine;

/** The attributes of a `runic` configuration that DevKit reads itself and does not pass to the C# debugger. */
export const runicAttributes = ["project", "launchProfile", "launchSettingsProfile", "launchSettingsFilePath", "envFile", "devArgs"] as const;

/** What the Window's launch is made of besides MSBuild's run properties and the dev session. */
export interface WindowLaunchInput {
  /** The user's `runic` configuration (launch.json, a dynamic entry or `{}`), with every attribute it has. */
  configuration?: Record<string, unknown>;
  /** The launch profile the configuration selects (`selectLaunchProfile`); none for `launchProfile: null` or no file. */
  profile?: LaunchProfile;
  /** The variables of the configuration's `envFile`, read by DevKit. */
  envFile?: Record<string, string>;
}

/**
 * The C# extension's `coreclr` launch configuration of the Window (design/ide-run-debug §3.3, D084.2):
 *
 * - **program:** what `dotnet run` starts: `RunCommand` (the application host, on macOS the development bundle's
 *   executable), or the DLL when the project has no application host (`RunCommand` is dotnet and `RunArguments` start
 *   with `exec <dll>`);
 * - **args:** `RunArguments` (from the project's `StartArguments`), then the profile's `commandLineArgs`, then the
 *   configuration's `args`, each appended, so no argument is lost (Visual Studio's rule). Strings are split with
 *   .NET's rules; an `args` array is kept as it is;
 * - **cwd:** the configuration's `cwd`, else the profile's `workingDirectory` against the project folder, else
 *   `RunWorkingDirectory`, else the project folder;
 * - **env**, lowest first: `DOTNET_ENVIRONMENT=Development` (or the session's value) as a default, the profile's
 *   `environmentVariables`, `envFile`, `env`, and last the session's Runic variables (`window.environment`);
 * - `launchSettingsProfile: null`, so the C# debugger does not apply a profile a second time.
 *
 * Every other attribute of the configuration (`console`, `stopAtEntry`, `justMyCode`, `logging`, …) passes through.
 */
export function windowLaunchConfiguration(project: string, properties: RunProperties, session: DevSession | undefined, input: WindowLaunchInput = {}): Record<string, unknown> {
  const command = properties.RunCommand?.trim();
  if (!command) throw new Error(`The project has no RunCommand; is ${basename(project)} an executable?`);
  const viaDotnet = /(?:^|[\\/])dotnet(?:\.exe)?$/i.test(command);
  const projectArgs = splitArguments(properties.RunArguments);
  // Without an application host RunCommand is dotnet and RunArguments start with `exec <dll>` or the DLL itself.
  if (viaDotnet) { if (projectArgs[0] === "exec") projectArgs.shift(); if (projectArgs[0]?.toLowerCase().endsWith(".dll")) projectArgs.shift(); }
  const program = viaDotnet ? properties.TargetPath : command;
  if (!program) throw new Error("The project names no program to debug.");
  const configuration = input.configuration ?? {};
  const profile = input.profile;
  const passed = Object.fromEntries(Object.entries(configuration).filter(([key]) => !(runicAttributes as readonly string[]).includes(key)));
  const args = [...projectArgs, ...splitArguments(profile?.commandLineArgs), ...configurationArguments(configuration.args)];
  const cwd = typeof configuration.cwd === "string" && configuration.cwd.trim() ? configuration.cwd
    : profileWorkingDirectory(project, profile) ?? (properties.RunWorkingDirectory?.trim() || dirname(project));
  return {
    console: "internalConsole",
    ...passed,
    type: "coreclr", request: "launch", name: typeof configuration.name === "string" ? configuration.name : "Runic: Window", program, args, cwd,
    env: windowEnvironment(session, profile, input.envFile, stringRecord(configuration.env)),
    launchSettingsProfile: null,
  };
}

/** The Window's variables, lowest first (§3.3 step 4); the session's own `DOTNET_ENVIRONMENT` counts as a default. */
export function windowEnvironment(session: DevSession | undefined, profile: LaunchProfile | undefined, envFile: Record<string, string> = {}, env: Record<string, string> = {}): Record<string, string> {
  const { DOTNET_ENVIRONMENT: sessionEnvironment, ...runic } = session?.window?.environment ?? {};
  const result: Record<string, string> = { DOTNET_ENVIRONMENT: sessionEnvironment ?? "Development", ...profile?.environmentVariables, ...envFile, ...env };
  for (const [name, value] of Object.entries(runic)) {
    // WebView2's DevTools port joins the user's own browser arguments instead of replacing them.
    result[name] = name === "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS" && result[name]?.trim() && !result[name].includes(value) ? `${result[name]} ${value}` : value;
  }
  return result;
}

/** A configuration's `args`: an array is kept as it is; a string is a command line, split as `RunArguments` is. */
function configurationArguments(value: unknown): string[] {
  if (Array.isArray(value)) return value.filter(item => item !== null && item !== undefined).map(String);
  return typeof value === "string" ? splitArguments(value) : [];
}

function stringRecord(value: unknown): Record<string, string> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null && item !== undefined && typeof item !== "object").map(([name, item]) => [name, String(item)]));
}

/** Whether a configuration names a task (`preLaunchTask`, `preRestartTask`): a label, or a task identifier object. */
export function hasTask(value: unknown): boolean {
  return typeof value === "string" ? value.trim().length > 0 : !!value && typeof value === "object";
}

/**
 * The tasks of the Window's resolved `coreclr` configuration that run the build (`task`: the template's `build` task or
 * DevKit's own, see {@link taskReference}). Restart (Ctrl+Shift+F5) reuses the resolved configuration without calling
 * DevKit's resolver again and runs only its `preRestartTask` and `preLaunchTask`, so the build must be one of them, or
 * Restart starts the old build. Without a `preLaunchTask` of the configuration's own, the build is the `preLaunchTask`: VS Code runs it before
 * the launch and on each Restart, and a failed build stops the launch as `debug.onTaskErrors` says. With one, that task
 * stays; DevKit builds before it on F5, and the build is the `preRestartTask` unless the configuration names its own.
 */
export function buildTasks(task: TaskReference, configuration: Record<string, unknown>): { preLaunchTask?: TaskReference; preRestartTask?: TaskReference } {
  if (!hasTask(configuration.preLaunchTask)) return { preLaunchTask: task };
  return hasTask(configuration.preRestartTask) ? {} : { preRestartTask: task };
}

/**
 * How a configuration names a task: a workspace task (tasks.json) by its label; an extension's task by its definition,
 * which VS Code matches by the definition's declared properties (a contributed task's label is not its name). `name`
 * is not one of them; VS Code shows it in "Errors exist after running preLaunchTask …".
 */
export type TaskReference = string | { type: string; name?: string; [property: string]: unknown };

/** The reference of a task: the label of a workspace task, else its definition with its name. */
export function taskReference(task: { name: string; source: string; definition: { type: string; [property: string]: unknown } }): TaskReference {
  return task.source === "Workspace" ? task.name : { ...task.definition, name: task.name };
}

/** The launch profile a `runic` configuration asks for: `launchProfile` (also `null`), else the C# debugger's `launchSettingsProfile`. */
export function requestedProfile(configuration: Record<string, unknown>): string | null | undefined {
  const value = "launchProfile" in configuration ? configuration.launchProfile : configuration.launchSettingsProfile;
  return typeof value === "string" || value === null ? value : undefined;
}

/** The arguments a `runic` configuration adds to the `dev --no-window` DevKit starts: `devArgs`, else the profile's `rax.devArguments`. */
export function devExtraArguments(configuration: Record<string, unknown>, profile: LaunchProfile | undefined): string[] {
  if (Array.isArray(configuration.devArgs)) return configuration.devArgs.filter((item): item is string => typeof item === "string");
  return profile?.rax?.devArguments ?? [];
}

/**
 * The few variables of a path in a configuration that DevKit reads before VS Code substitutes them (`envFile`,
 * `launchSettingsFilePath`): `${workspaceFolder}`, `${workspaceFolderBasename}`, `${userHome}` and `${env:NAME}`. A
 * relative path is taken from the workspace folder.
 */
export function substitutePath(value: string, folder: string, home: string, environment: NodeJS.ProcessEnv = process.env): string {
  const substituted = value
    .replace(/\$\{workspaceFolder\}/g, folder).replace(/\$\{workspaceFolderBasename\}/g, basename(folder))
    .replace(/\$\{userHome\}/g, home).replace(/\$\{env:([^}]+)\}/g, (_, name: string) => environment[name] ?? "");
  return isAbsolute(substituted) ? substituted : resolve(folder, substituted);
}

/** A Window project and its launch profiles, for the Run and Debug entries. */
export interface WindowProjectProfiles { project: string; relative: string; profiles: readonly { name: string }[] }

/**
 * Run and Debug's `runic` entries (dynamic and initial): one per Window project and `Project` profile, named
 * "Runic: Acme.App (Mock data)", or one "Runic: Acme.App" for a project with at most one profile.
 */
export function windowDebugConfigurations(projects: readonly WindowProjectProfiles[]): Record<string, unknown>[] {
  return projects.flatMap(({ project, relative, profiles }) => {
    const name = `Runic: ${basename(project).replace(/\.csproj$/i, "")}`;
    if (profiles.length <= 1) return [{ type: "runic", request: "launch", name, project: relative }];
    return profiles.map(profile => ({ type: "runic", request: "launch", name: `${name} (${profile.name})`, project: relative, launchProfile: profile.name }));
  });
}

/**
 * The template's F5 configuration (runic-sdk `.vscode/launch.json`, "Window"): a configuration whose `preLaunchTask` is
 * `runic: window`, or else one named `Window`. The extension starts it instead of its own when it exists.
 */
export function templateWindowConfiguration(configurations: unknown): string | undefined {
  if (!Array.isArray(configurations)) return undefined;
  const named = configurations.filter((item): item is { name: string; preLaunchTask?: unknown; request?: unknown } => !!item && typeof item === "object" && typeof (item as { name?: unknown }).name === "string");
  return (named.find(item => item.preLaunchTask === "runic: window" && item.request === "launch") ?? named.find(item => item.name === "Window"))?.name;
}

/** The template's task labels (runic-sdk `.vscode/tasks.json`); the extension runs a workspace task of that label when there is one. */
export const templateTaskLabels = { dev: "runic: dev", devNoWindow: "runic: dev --no-window", devBrowser: "runic: dev --browser", doctor: "runic: doctor", build: "build" } as const;
