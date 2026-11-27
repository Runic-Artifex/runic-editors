import { test, expect } from "bun:test";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { launchSettingsPath, parseEnvFile, parseLaunchProfiles, profileWorkingDirectory, readLaunchProfiles, selectLaunchProfile, splitCommandLine, stripJsonComments, type LaunchProfile } from "../src/launch-settings.js";
import { devExtraArguments, requestedProfile, substitutePath, windowDebugConfigurations, windowEnvironment, windowLaunchConfiguration, type RunProperties } from "../src/window-debug.js";
import { devSessionSchema, type DevSession } from "../src/dev-session.js";

// design/ide-run-debug §3.3 and scenario M: an app whose Debug build starts only with /mock.
const project = "/w/Acme.Mock/Acme.Mock.csproj";
const appHost: RunProperties = { RunCommand: "/w/Acme.Mock/bin/Debug/net10.0/Acme.Mock", RunArguments: "", RunWorkingDirectory: "", TargetPath: "/w/Acme.Mock/bin/Debug/net10.0/Acme.Mock.dll" };
const profile = (overrides: Partial<LaunchProfile> = {}): LaunchProfile => ({ name: "Acme.Mock", environmentVariables: {}, ...overrides });
const ideSession = (environment: Record<string, string> = { DOTNET_ENVIRONMENT: "Development", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/w/Acme.Mock/Frontend/dist" }): DevSession => ({
  schema: devSessionSchema, state: "ready", processId: 41250, startedAt: "2026-11-26T10:00:00Z", project, configuration: "Debug",
  frontend: { developmentServer: "vite", url: "http://127.0.0.1:5173/", port: 5173, stateUrl: null, directory: "/w/Acme.Mock/Frontend", outputDirectory: "/w/Acme.Mock/Frontend/dist", generatedDirectory: null },
  webRoot: "/w/Acme.Mock/bin/Debug/net10.0/www", pages: [], window: { owner: "ide", program: "Acme.Mock", processId: null, starts: 0, environment }, debugEndpoints: [],
});
const args = (properties: Partial<RunProperties>, launchProfile?: LaunchProfile, configuration: Record<string, unknown> = {}) =>
  windowLaunchConfiguration(project, { ...appHost, ...properties }, ideSession(), { configuration, profile: launchProfile }).args;

test("RunArguments split as .NET's ProcessStartInfo.Arguments does (checked against .NET 10.0.404)", () => {
  // Each row's result is what a .NET 10 program got from ProcessStartInfo.Arguments = <input> (Linux, 2026-11-26).
  const table: [string, string[]][] = [
    ["/mock", ["/mock"]],
    ['/mock "two words"', ["/mock", "two words"]],
    ['/mock /config:"C:\\Program Files\\Acme\\dev.json"', ["/mock", "/config:C:\\Program Files\\Acme\\dev.json"]],
    ['"C:\\data\\new"', ["C:\\data\\new"]],
    ["C:\\data\\new", ["C:\\data\\new"]],
    ['a "b c" "d \\"e\\""', ["a", "b c", 'd "e"']],
    ['"a""b" c', ['a"b', "c"]],
    ['"" x', ["", "x"]],
    ['a\\\\"b c"', ["a\\b c"]],
    ['a\\\\\\"b', ['a\\"b']],
    ["trailing\\\\", ["trailing\\\\"]],
    ["tab\tsep  multiple   spaces", ["tab", "sep", "multiple", "spaces"]],
    ['"unterminated quote and\\ more', ["unterminated quote and\\ more"]],
    ['--opt="x y"z', ["--opt=x yz"]],
    ["", []],
  ];
  for (const [input, expected] of table) expect([input, splitCommandLine(input)]).toEqual([input, expected]);
  expect(splitCommandLine(undefined)).toEqual([]);
});

test("the command line: the project's arguments, then the profile's, then the configuration's args (D084.2)", () => {
  // Nowhere: the maintainer's failure, an app that refuses to start.
  expect(args({})).toEqual([]);
  // The project only (StartArguments in a Debug PropertyGroup, which the SDK turns into RunArguments).
  expect(args({ RunArguments: "/mock" })).toEqual(["/mock"]);
  // The profile only.
  expect(args({}, profile({ commandLineArgs: "/mock" }))).toEqual(["/mock"]);
  // Both: appended, where dotnet run and the C# debugger alone drop the profile's (M8g, V4).
  expect(args({ RunArguments: "/mock" }, profile({ commandLineArgs: "/verbose" }))).toEqual(["/mock", "/verbose"]);
  // The configuration's args (V1): they used to be dropped.
  expect(args({}, undefined, { args: ["/mock"] })).toEqual(["/mock"]);
  // All three, in order.
  expect(args({ RunArguments: "/mock" }, profile({ commandLineArgs: "/verbose" }), { args: ["/extra"] })).toEqual(["/mock", "/verbose", "/extra"]);
  // An args array is passed as it is, quotes and backslashes included; a string is split as RunArguments is.
  expect(args({}, undefined, { args: ['/config:"C:\\Program Files\\x"', "two words"] })).toEqual(['/config:"C:\\Program Files\\x"', "two words"]);
  expect(args({}, undefined, { args: '/mock /config:"C:\\Program Files\\x"' })).toEqual(["/mock", "/config:C:\\Program Files\\x"]);
  // Windows paths and slash options from the project and the profile stay one argument each, with their backslashes (B3).
  expect(args({ RunArguments: '/mock /config:"C:\\Program Files\\Acme\\dev.json"' }, profile({ commandLineArgs: '"C:\\data\\new"' })))
    .toEqual(["/mock", "/config:C:\\Program Files\\Acme\\dev.json", "C:\\data\\new"]);
  // Without an application host: dotnet exec "<dll>" with StartArguments after it; the debugger runs the DLL.
  const viaDotnet = windowLaunchConfiguration(project, { RunCommand: "dotnet", RunArguments: 'exec "/w/Acme Mock/bin/Debug/net10.0/Acme.Mock.dll" /mock', TargetPath: "/w/Acme Mock/bin/Debug/net10.0/Acme.Mock.dll" }, ideSession(),
    { profile: profile({ commandLineArgs: "/verbose" }), configuration: { args: ["/extra"] } });
  expect([viaDotnet.program, viaDotnet.args]).toEqual(["/w/Acme Mock/bin/Debug/net10.0/Acme.Mock.dll", ["/mock", "/verbose", "/extra"]]);
  expect(windowLaunchConfiguration(project, { RunCommand: "C:\\w\\bin\\dotnet.exe", RunArguments: "C:\\w\\App.dll /mock", TargetPath: "C:\\w\\App.dll" }, undefined).args).toEqual(["/mock"]);
});

test("the working directory: cwd, else the profile's, else RunWorkingDirectory, else the project folder", () => {
  const cwd = (properties: Partial<RunProperties>, launchProfile?: LaunchProfile, configuration: Record<string, unknown> = {}) =>
    windowLaunchConfiguration(project, { ...appHost, ...properties }, undefined, { configuration, profile: launchProfile }).cwd;
  expect(cwd({})).toBe("/w/Acme.Mock");
  expect(cwd({ RunWorkingDirectory: "/srv/run" })).toBe("/srv/run");
  expect(cwd({ RunWorkingDirectory: "/srv/run" }, profile({ workingDirectory: "data" }))).toBe("/w/Acme.Mock/data");
  expect(cwd({}, profile({ workingDirectory: "/abs" }))).toBe("/abs");
  // cwd is passed on as written; VS Code substitutes ${workspaceFolder} after DevKit's resolver.
  expect(cwd({ RunWorkingDirectory: "/srv/run" }, profile({ workingDirectory: "data" }), { cwd: "${workspaceFolder}/other" })).toBe("${workspaceFolder}/other");
});

test("the environment: default, profile, envFile, env, then the session's Runic variables", () => {
  const session = ideSession({ DOTNET_ENVIRONMENT: "Development", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/assets", WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: "--remote-debugging-port=9333" });
  expect(windowEnvironment(session, profile({ environmentVariables: { DOTNET_ENVIRONMENT: "Staging", FROM: "profile", PROFILE_ONLY: "1" } }), { FROM: "envFile", FILE_ONLY: "1" },
    { FROM: "env", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/mine", WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: "--lang=de" })).toEqual({
    DOTNET_ENVIRONMENT: "Staging", FROM: "env", PROFILE_ONLY: "1", FILE_ONLY: "1",
    RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/assets", WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: "--lang=de --remote-debugging-port=9333" });
  expect(windowEnvironment(undefined, undefined)).toEqual({ DOTNET_ENVIRONMENT: "Development" });
  // env values that are not strings: numbers and booleans as text, objects and null dropped.
  const launch = windowLaunchConfiguration(project, appHost, undefined, { configuration: { env: { PORT: 8080, DEBUG: true, SKIP: null, NESTED: {} } } });
  expect(launch.env).toEqual({ DOTNET_ENVIRONMENT: "Development", PORT: "8080", DEBUG: "true" });
});

test("every other attribute of the configuration passes through to the C# debugger", () => {
  const launch = windowLaunchConfiguration(project, { ...appHost, RunArguments: "/mock" }, ideSession(), { profile: profile({ commandLineArgs: "/verbose" }), configuration: {
    type: "runic", request: "launch", name: "Mock", project: "Acme.Mock/Acme.Mock.csproj", launchProfile: "Acme.Mock", launchSettingsProfile: "Other", launchSettingsFilePath: "x.json",
    envFile: "${workspaceFolder}/.env", devArgs: ["--verbosity", "minimal"], program: "/ignored", console: "integratedTerminal", stopAtEntry: true, justMyCode: false,
    logging: { moduleLoad: false }, symbolOptions: { searchMicrosoftSymbolServer: true }, preLaunchTask: "prepare", aFutureOption: 1 } });
  expect(launch).toEqual({
    type: "coreclr", request: "launch", name: "Mock", program: "/w/Acme.Mock/bin/Debug/net10.0/Acme.Mock", args: ["/mock", "/verbose"], cwd: "/w/Acme.Mock",
    env: { DOTNET_ENVIRONMENT: "Development", RUNIC_APPLICATION_DEVELOPMENT_ASSETS: "/w/Acme.Mock/Frontend/dist" }, launchSettingsProfile: null,
    console: "integratedTerminal", stopAtEntry: true, justMyCode: false, logging: { moduleLoad: false }, symbolOptions: { searchMicrosoftSymbolServer: true }, preLaunchTask: "prepare", aFutureOption: 1 });
});

test("which profile a configuration asks for, and dev's extra arguments", () => {
  expect(requestedProfile({})).toBeUndefined();
  expect(requestedProfile({ launchProfile: "Mock data" })).toBe("Mock data");
  expect(requestedProfile({ launchProfile: null, launchSettingsProfile: "Other" })).toBeNull();
  expect(requestedProfile({ launchSettingsProfile: "Other" })).toBe("Other");
  expect(requestedProfile({ launchSettingsProfile: null })).toBeNull();
  expect(requestedProfile({ launchProfile: 3 })).toBeUndefined();
  const raxProfile = profile({ rax: { devArguments: ["--verbosity", "detailed"] } });
  expect(devExtraArguments({}, raxProfile)).toEqual(["--verbosity", "detailed"]);
  expect(devExtraArguments({ devArgs: ["--verbosity", "minimal"] }, raxProfile)).toEqual(["--verbosity", "minimal"]);
  expect(devExtraArguments({ devArgs: [] }, raxProfile)).toEqual([]);
  expect(devExtraArguments({}, undefined)).toEqual([]);
});

const launchSettings = `{
  // The template's profile, then a mock one (scenario M) and kinds Runic does not read.
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "Acme.Mock": { "commandName": "Project", "environmentVariables": { "DOTNET_ENVIRONMENT": "Development" } },
    "IIS Express": { "commandName": "IISExpress", "commandLineArgs": "/iis" },
    "Mock data": {
      "commandName": "Project",
      "commandLineArgs": "/mock /config:\\"C:\\\\Program Files\\\\Acme\\\\dev.json\\"", /* a slash option with a Windows path */
      "environmentVariables": { "ACME_DATA": "mock", "PORT": "8080", },
      "workingDirectory": "data",
      "rax": { "dev": "no-window", "page": true, "devArguments": ["--verbosity", "minimal", 3] },
    },
    "Custom": { "commandName": "RunicWindow", "commandLineArgs": "/mock" },
  },
}`;

test("launchSettings.json: Project profiles in file order, comments and trailing commas as dotnet run reads them", () => {
  expect(parseLaunchProfiles(launchSettings)).toEqual([
    { name: "Acme.Mock", commandLineArgs: undefined, environmentVariables: { DOTNET_ENVIRONMENT: "Development" }, workingDirectory: undefined, rax: undefined },
    { name: "Mock data", commandLineArgs: '/mock /config:"C:\\Program Files\\Acme\\dev.json"', environmentVariables: { ACME_DATA: "mock", PORT: "8080" }, workingDirectory: "data",
      rax: { dev: "no-window", page: true, devArguments: ["--verbosity", "minimal"] } },
  ]);
  expect(parseLaunchProfiles("{}")).toEqual([]);
  expect(parseLaunchProfiles('{ "profiles": { "Broken": null, "Empty": { "commandName": "Project", "workingDirectory": " " } } }')).toEqual([
    { name: "Empty", commandLineArgs: undefined, environmentVariables: {}, workingDirectory: undefined, rax: undefined }]);
  // A value Visual Studio refuses (editors#79's fixture had "ACME_PORT": 5000) refuses the file, in any profile; null is unset.
  const refused = (profile: string) => () => parseLaunchProfiles(`{ "profiles": { "Mock data": ${profile} } }`);
  expect(refused('{ "commandName": "Project", "environmentVariables": { "ACME_PORT": 5000 } }')).toThrow('the launch profile Mock data sets the variable ACME_PORT to 5000, a number, and Visual Studio reads only strings there (its F5 refuses the file: "Expected string"), so Runic DevKit does not use the file either. Write it as a string: "ACME_PORT": "5000".');
  expect(refused('{ "commandName": "IISExpress", "environmentVariables": { "DEBUG": true } }')).toThrow("DEBUG to true, a Boolean");
  expect(refused('{ "commandName": "Project", "commandLineArgs": ["/mock"] }')).toThrow("has an array as commandLineArgs");
  expect(parseLaunchProfiles('{ "profiles": { "p": { "commandName": "Project", "commandLineArgs": null, "environmentVariables": { "A": null, "B": "b" } } } }')[0])
    .toEqual({ name: "p", commandLineArgs: undefined, environmentVariables: { B: "b" }, workingDirectory: undefined, rax: undefined });
  // Comment markers and trailing-comma look-alikes inside strings stay.
  expect(JSON.parse(stripJsonComments('{ "a": "http://x/*y*/", "b": ", }", /* c */ "d": [1, 2, ], }'))).toEqual({ a: "http://x/*y*/", b: ", }", d: [1, 2] });
});

test("the profile a launch uses: the first by default, a named one, or none", () => {
  const profiles = parseLaunchProfiles(launchSettings);
  const file = "/w/Acme.Mock/Properties/launchSettings.json";
  expect(selectLaunchProfile(profiles, undefined, file)?.name).toBe("Acme.Mock");
  expect(selectLaunchProfile(profiles, "Mock data", file)?.name).toBe("Mock data");
  expect(selectLaunchProfile(profiles, null, file)).toBeUndefined();
  expect(selectLaunchProfile(profiles, "", file)).toBeUndefined();
  expect(selectLaunchProfile([], undefined, file)).toBeUndefined();
  expect(() => selectLaunchProfile(profiles, "Mock", file)).toThrow(`The launch profile "Mock" is not a "Project" profile of ${file} (it has "Acme.Mock", "Mock data").`);
  expect(() => selectLaunchProfile(profiles, "Custom", file)).toThrow('"Custom" is not a "Project" profile');
  expect(profileWorkingDirectory(project, profiles[1])).toBe("/w/Acme.Mock/data");
  expect(profileWorkingDirectory(project, profiles[0])).toBeUndefined();
  // Through the composition: the profile's arguments, variables and working directory.
  const launch = windowLaunchConfiguration(project, { ...appHost, RunArguments: "/start" }, ideSession(), { profile: profiles[1], configuration: { args: ["/extra"] } });
  expect([launch.args, launch.cwd, (launch.env as Record<string, string>).ACME_DATA]).toEqual([["/start", "/mock", "/config:C:\\Program Files\\Acme\\dev.json", "/extra"], "/w/Acme.Mock/data", "mock"]);
});

test("a project's launchSettings.json is read from Properties, and a missing file means no profile", () => {
  const directory = mkdtempSync(join(tmpdir(), "runic-vscode-launch-"));
  try {
    const app = join(directory, "App.csproj");
    expect(launchSettingsPath(app)).toBe(join(directory, "Properties", "launchSettings.json"));
    expect(readLaunchProfiles(app)).toEqual([]);
    mkdirSync(join(directory, "Properties"));
    writeFileSync(launchSettingsPath(app), launchSettings);
    expect(readLaunchProfiles(app).map(item => item.name)).toEqual(["Acme.Mock", "Mock data"]);
    writeFileSync(join(directory, "other.json"), '{ "profiles": { "Other": { "commandName": "Project", "commandLineArgs": "/other" } } }');
    expect(readLaunchProfiles(app, join(directory, "other.json")).map(item => item.commandLineArgs)).toEqual(["/other"]);
    writeFileSync(launchSettingsPath(app), "{ not json");
    expect(() => readLaunchProfiles(app)).toThrow(`${launchSettingsPath(app)} could not be read`);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test("envFile and the paths DevKit reads before VS Code substitutes variables", () => {
  expect(parseEnvFile('# comment\nACME_DATA=mock\nexport PORT = 8080\nQUOTED="two\\nlines"\nSINGLE=\'a b\'\nEMPTY=\n  # indented comment\nnot a line\n')).toEqual({ ACME_DATA: "mock", PORT: "8080", QUOTED: "two\nlines", SINGLE: "a b", EMPTY: "" });
  expect(substitutePath("${workspaceFolder}/.env", "/w", "/home/u")).toBe("/w/.env");
  expect(substitutePath(".env.${workspaceFolderBasename}", "/w/acme", "/home/u")).toBe("/w/acme/.env.acme");
  expect(substitutePath("${userHome}/acme/${env:ACME_STAGE}.env", "/w", "/home/u", { ACME_STAGE: "dev" })).toBe("/home/u/acme/dev.env");
});

test("Run and Debug offers one entry per Window project and launch profile (E2)", () => {
  const two = [{ name: "Acme.App" }, { name: "Mock data" }];
  expect(windowDebugConfigurations([
    { project: "/w/Acme.App/Acme.App.csproj", relative: "Acme.App/Acme.App.csproj", profiles: two },
    { project: "/w/Acme.Admin/Acme.Admin.csproj", relative: "Acme.Admin/Acme.Admin.csproj", profiles: two },
  ])).toEqual([
    { type: "runic", request: "launch", name: "Runic: Acme.App (Acme.App)", project: "Acme.App/Acme.App.csproj", launchProfile: "Acme.App" },
    { type: "runic", request: "launch", name: "Runic: Acme.App (Mock data)", project: "Acme.App/Acme.App.csproj", launchProfile: "Mock data" },
    { type: "runic", request: "launch", name: "Runic: Acme.Admin (Acme.App)", project: "Acme.Admin/Acme.Admin.csproj", launchProfile: "Acme.App" },
    { type: "runic", request: "launch", name: "Runic: Acme.Admin (Mock data)", project: "Acme.Admin/Acme.Admin.csproj", launchProfile: "Mock data" },
  ]);
  // One profile, or none: one entry, which takes the first profile by default.
  expect(windowDebugConfigurations([
    { project: "/w/App.csproj", relative: "App.csproj", profiles: [{ name: "App" }] },
    { project: "/w/Other/Other.csproj", relative: "Other/Other.csproj", profiles: [] },
  ])).toEqual([
    { type: "runic", request: "launch", name: "Runic: App", project: "App.csproj" },
    { type: "runic", request: "launch", name: "Runic: Other", project: "Other/Other.csproj" },
  ]);
  expect(windowDebugConfigurations([])).toEqual([]);
});
