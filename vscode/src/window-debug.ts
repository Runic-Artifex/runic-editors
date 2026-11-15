import { basename, dirname } from "node:path";
import type { DevSession } from "./dev-session.js";

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

/** Splits MSBuild's `RunArguments` the way a shell would for plain and double-quoted arguments. */
export function splitArguments(text: string | undefined): string[] {
  const args: string[] = [];
  for (const match of (text ?? "").matchAll(/"((?:[^"\\]|\\.)*)"|(\S+)/g)) args.push(match[1] !== undefined ? match[1].replace(/\\(.)/g, "$1") : match[2]);
  return args;
}

/**
 * The C# extension's `coreclr` launch configuration of the Window: the program `dotnet run` starts (the application host,
 * on macOS the development bundle's executable; the DLL when the project has no application host) with the environment
 * the session file names for a Window the IDE starts (`window.environment`: `DOTNET_ENVIRONMENT=Development`, and
 * `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` without a development server). `env` adds to it, as in a launch.json.
 */
export function windowLaunchConfiguration(project: string, properties: RunProperties, session: DevSession | undefined, env: Record<string, string> = {}): Record<string, unknown> {
  const command = properties.RunCommand?.trim();
  if (!command) throw new Error(`The project has no RunCommand; is ${basename(project)} an executable?`);
  const viaDotnet = /^dotnet(?:\.exe)?$/i.test(basename(command));
  const args = splitArguments(properties.RunArguments);
  // Without an application host RunCommand is dotnet and RunArguments start with `exec <dll>` or the DLL itself.
  if (viaDotnet) { if (args[0] === "exec") args.shift(); if (args[0]?.toLowerCase().endsWith(".dll")) args.shift(); }
  const program = viaDotnet ? properties.TargetPath : command;
  if (!program) throw new Error("The project names no program to debug.");
  return {
    type: "coreclr", request: "launch", name: "Runic: Window", program, args,
    cwd: properties.RunWorkingDirectory?.trim() || dirname(project),
    env: { DOTNET_ENVIRONMENT: "Development", ...(session?.window?.environment ?? {}), ...env },
    console: "internalConsole",
  };
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
export const templateTaskLabels = { dev: "runic: dev", devNoWindow: "runic: dev --no-window", doctor: "runic: doctor", build: "build" } as const;
